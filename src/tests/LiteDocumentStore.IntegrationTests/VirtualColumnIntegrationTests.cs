using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for virtual column functionality.
/// Tests cover virtual column creation and query optimization via the generated columns.
/// </summary>
public class VirtualColumnIntegrationTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteConnection _connection;
    private readonly DocumentStore _store;

    public VirtualColumnIntegrationTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_virtualcol_{Guid.NewGuid()}.db");
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();

        _connection = connectionFactory.CreateConnection(options);
        _store = new DocumentStore(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); }
            catch (IOException) { /* ignore */ }
        }
    }

    // Seeks the given WHERE clause against the Product table (typically hitting a virtual
    // column) and loads each matching document through the public store API. Range/string
    // predicates are no longer expressible through the store query API, so tests exercise the
    // virtual columns via raw SQL and rehydrate documents by id.
    private async Task<List<Product>> LoadProductsAsync(
        string whereSql,
        params (string Name, object? Value)[] parameters)
    {
        var ids = await _connection.QueryStringsAsync(
            $"SELECT id FROM Product WHERE {whereSql}", parameters);

        var products = new List<Product>();
        foreach (var id in ids)
        {
            var product = await _store.GetAsync<Product>(id!);
            Assert.NotNull(product);
            products.Add(product);
        }

        return products;
    }

    #region Virtual Column Creation Tests

    [Fact]
    public async Task AddVirtualColumnAsync_Debug_SQLiteSyntax()
    {
        // Debug test to verify SQLite generated (virtual) column syntax works
        using var memConnection = new SqliteConnection("Data Source=:memory:");
        await memConnection.OpenAsync();

        var version = await memConnection.QueryFirstStringAsync("SELECT sqlite_version()");

        await memConnection.ExecuteAsync(@"
            CREATE TABLE Test1 (
                id INTEGER PRIMARY KEY,
                a INTEGER,
                b INTEGER,
                c INTEGER GENERATED ALWAYS AS (a + b)
            )");

        var introspector = new SchemaIntrospector(memConnection);
        var columns = await introspector.GetColumnsAsync("Test1");
        var colNames = columns.Select(c => c.Name).ToList();

        await memConnection.ExecuteAsync("INSERT INTO Test1 (id, a, b) VALUES (1, 10, 20)");
        var generated = await memConnection.ExecuteScalarAsync<long>("SELECT c FROM Test1 WHERE id = 1");

        Assert.True(colNames.Contains("c") || generated == 30,
            $"Generated column 'c' not found. Columns: [{string.Join(", ", colNames)}]. SQLite version: {version}");
    }

    [Fact]
    public async Task AddVirtualColumnAsync_CreatesVirtualColumn()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget", Category = "Electronics", Price = 29.99m });

        // Act
        await _store.AddVirtualColumnAsync<Product>(x => x.Category, "category");

        // Assert
        var introspector = new SchemaIntrospector(_connection);
        var columns = await introspector.GetColumnsAsync("Product");
        Assert.Contains(columns, c => c.Name == "category");
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithIndex_CreatesColumnAndIndex()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget", Category = "Electronics", Price = 29.99m });

        // Act
        await _store.AddVirtualColumnAsync<Product>(x => x.Price, "price", createIndex: true, columnType: "REAL");

        // Assert
        var introspector = new SchemaIntrospector(_connection);
        var columns = await introspector.GetColumnsAsync("Product");
        Assert.Contains(columns, c => c.Name == "price");

        var indexExists = await introspector.IndexExistsAsync("idx_Product_price");
        Assert.True(indexExists);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_NestedProperty_CreatesColumn()
    {
        // Arrange
        await _store.CreateTableAsync<ProductWithMetadata>();
        await _store.UpsertAsync("p1", new ProductWithMetadata
        {
            Name = "Widget",
            Metadata = new ProductMetadata { Brand = "Acme", Country = "USA" }
        });

        // Act
        await _store.AddVirtualColumnAsync<ProductWithMetadata>(x => x.Metadata.Brand, "brand", createIndex: true);

        // Assert
        var introspector = new SchemaIntrospector(_connection);
        var columns = await introspector.GetColumnsAsync("ProductWithMetadata");
        Assert.Contains(columns, c => c.Name == "brand");

        var indexExists = await introspector.IndexExistsAsync("idx_ProductWithMetadata_brand");
        Assert.True(indexExists);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_ColumnAlreadyExists_DoesNotThrow()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.AddVirtualColumnAsync<Product>(x => x.Category, "category");

        // Act & Assert - should not throw
        await _store.AddVirtualColumnAsync<Product>(x => x.Category, "category");

        var introspector = new SchemaIntrospector(_connection);
        var columns = await introspector.GetColumnsAsync("Product");
        Assert.Contains(columns, c => c.Name == "category");
    }

    [Fact]
    public async Task AddVirtualColumnAsync_VirtualColumnValues_AreCorrect()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget", Category = "Electronics", Price = 29.99m });
        await _store.UpsertAsync("p2", new Product { Name = "Gadget", Category = "Electronics", Price = 49.99m });
        await _store.UpsertAsync("p3", new Product { Name = "Tool", Category = "Hardware", Price = 19.99m });

        // Act
        await _store.AddVirtualColumnAsync<Product>(x => x.Category, "category");
        await _store.AddVirtualColumnAsync<Product>(x => x.Price, "price", columnType: "REAL");

        // Assert - Query using virtual columns directly
        var categories = await _connection.QueryStringsAsync(
            "SELECT category FROM Product WHERE category = 'Electronics' ORDER BY price");

        Assert.Equal(2, categories.Count);
        Assert.Equal("Electronics", categories[0]);
    }

    #endregion

    #region Virtual Column Querying Tests (raw SQL seeks over the generated columns)

    [Fact]
    public async Task VirtualColumn_EqualitySeek_ReturnsMatchingDocuments()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget", Category = "Electronics", Price = 29.99m });
        await _store.UpsertAsync("p2", new Product { Name = "Gadget", Category = "Toys", Price = 49.99m });
        await _store.UpsertAsync("p3", new Product { Name = "Tool", Category = "Hardware", Price = 19.99m });

        await _store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);

        // Act
        var results = await LoadProductsAsync("category = @Category", ("Category", "Electronics"));

        // Assert
        Assert.Single(results);
        Assert.Equal("Widget", results[0].Name);
    }

    [Fact]
    public async Task VirtualColumn_RangeSeek_ReturnsMatchingDocuments()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Cheap", Category = "A", Price = 10.00m });
        await _store.UpsertAsync("p2", new Product { Name = "Medium", Category = "B", Price = 50.00m });
        await _store.UpsertAsync("p3", new Product { Name = "Expensive", Category = "C", Price = 100.00m });

        await _store.AddVirtualColumnAsync<Product>(p => p.Price, "price", createIndex: true, columnType: "REAL");

        // Act
        var results = await LoadProductsAsync("price > @Price", ("Price", 30.0));

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Name == "Medium");
        Assert.Contains(results, p => p.Name == "Expensive");
    }

    [Fact]
    public async Task VirtualColumn_LikeSeek_ReturnsMatchingDocuments()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget Pro", Category = "Electronics", Price = 29.99m });
        await _store.UpsertAsync("p2", new Product { Name = "Widget Basic", Category = "Electronics", Price = 19.99m });
        await _store.UpsertAsync("p3", new Product { Name = "Gadget", Category = "Toys", Price = 9.99m });

        await _store.AddVirtualColumnAsync<Product>(p => p.Name, "name_vc", createIndex: true);

        // Act
        var results = await LoadProductsAsync("name_vc LIKE @Pattern", ("Pattern", "Widget%"));

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.StartsWith("Widget", p.Name));
    }

    [Fact]
    public async Task VirtualColumn_MultipleColumnSeek_ReturnsMatchingDocuments()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        await _store.UpsertAsync("p1", new Product { Name = "Widget", Category = "Electronics", Price = 29.99m });
        await _store.UpsertAsync("p2", new Product { Name = "Gadget", Category = "Electronics", Price = 49.99m });
        await _store.UpsertAsync("p3", new Product { Name = "Tool", Category = "Hardware", Price = 19.99m });

        await _store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);
        await _store.AddVirtualColumnAsync<Product>(p => p.Price, "price", createIndex: true, columnType: "REAL");

        // Act
        var results = await LoadProductsAsync(
            "category = @Category AND price > @Price", ("Category", "Electronics"), ("Price", 30.0));

        // Assert
        Assert.Single(results);
        Assert.Equal("Gadget", results[0].Name);
    }

    #endregion

    #region Index Usage Verification

    [Fact]
    public async Task AddVirtualColumnAsync_IndexCanBeUsed_InRawQuery()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        for (int i = 0; i < 100; i++)
        {
            await _store.UpsertAsync($"p{i}", new Product
            {
                Name = $"Product {i}",
                Category = $"Category {i % 10}",
                Price = 10 + i
            });
        }

        await _store.AddVirtualColumnAsync<Product>(x => x.Category, "category", createIndex: true);

        // Act - Query using index
        var match = await _connection.QueryFirstStringAsync(
            "SELECT id FROM Product WHERE category = @Category",
            ("Category", "Category 5"));

        // Assert
        Assert.NotNull(match);
    }

    [Fact]
    public async Task VirtualColumn_IndexedSeek_ReturnsCorrectResults()
    {
        // Arrange
        await _store.CreateTableAsync<Product>();
        var expectedProducts = new List<string>();

        for (int i = 0; i < 50; i++)
        {
            var category = i % 5 == 0 ? "Target" : $"Other{i}";
            if (category == "Target") expectedProducts.Add($"Product {i}");

            await _store.UpsertAsync($"p{i}", new Product
            {
                Name = $"Product {i}",
                Category = category,
                Price = 10 + i
            });
        }

        await _store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);

        // Act
        var results = await LoadProductsAsync("category = @Category", ("Category", "Target"));

        // Assert
        Assert.Equal(expectedProducts.Count, results.Count);
        Assert.All(results, p => Assert.Equal("Target", p.Category));
    }

    #endregion

    #region Test Models

    private class Product
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    private class ProductWithMetadata
    {
        public string Name { get; set; } = string.Empty;
        public ProductMetadata Metadata { get; set; } = new();
    }

    private class ProductMetadata
    {
        public string Brand { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }

    #endregion
}
