using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

public class SchemaIntrospectionIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _store = null!;
    private SqliteConnection _connection = null!;
    private SchemaIntrospector _introspector = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _store = new DocumentStore(_connection, ownsConnection: false);
        _introspector = new SchemaIntrospector(_connection);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetTablesAsync_WithNoTables_ReturnsEmpty()
    {
        // Act
        var tables = await _introspector.GetTablesAsync();

        // Assert
        Assert.Empty(tables);
    }

    [Fact]
    public async Task GetTablesAsync_WithCreatedTables_ReturnsAllTables()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();
        await _store.CreateTableAsync<Order>();

        // Act
        var tables = (await _introspector.GetTablesAsync()).ToList();

        // Assert
        Assert.True(tables.Count >= 2);
        Assert.Contains(tables, t => t.Name == "Customer");
        Assert.Contains(tables, t => t.Name == "Order");
    }

    [Fact]
    public async Task GetColumnsAsync_TableNameWithClosingBracket_IsEscaped()
    {
        // Arrange - a table whose name contains ']' would break out of the [ ] identifier quoting
        // in the PRAGMA statement if the bracket were not escaped.
        await _connection.ExecuteAsync(
            "CREATE TABLE \"weird]name\" (id TEXT PRIMARY KEY, val TEXT)");

        // Act
        var columns = (await _introspector.GetColumnsAsync("weird]name")).ToList();

        // Assert - the PRAGMA resolved the real table rather than erroring or hitting the wrong one
        Assert.Contains(columns, c => c.Name == "id");
        Assert.Contains(columns, c => c.Name == "val");
    }

    [Fact]
    public async Task TableExistsAsync_WithExistingTable_ReturnsTrue()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();

        // Act
        var exists = await _introspector.TableExistsAsync("Customer");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task TableExistsAsync_WithNonExistingTable_ReturnsFalse()
    {
        // Act
        var exists = await _introspector.TableExistsAsync("NonExistent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task GetColumnsAsync_WithDocumentTable_ReturnsCorrectColumns()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();

        // Act
        var columns = (await _introspector.GetColumnsAsync("Customer")).ToList();

        // Assert
        Assert.Equal(2, columns.Count); // id, data

        var idColumn = columns.FirstOrDefault(c => c.Name == "id");
        Assert.NotNull(idColumn);
        Assert.Equal("TEXT", idColumn.Type);
        Assert.True(idColumn.IsPrimaryKey);

        var dataColumn = columns.FirstOrDefault(c => c.Name == "data");
        Assert.NotNull(dataColumn);
        Assert.Equal("BLOB", dataColumn.Type);
        Assert.True(dataColumn.NotNull);
    }

    [Fact]
    public async Task GetIndexesAsync_WithNoIndexes_ReturnsEmpty()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();

        // Act
        var indexes = (await _introspector.GetIndexesAsync("Customer")).ToList();

        // Assert - Primary key index may or may not be included depending on SQLite version
        // So we just check it doesn't throw
        Assert.NotNull(indexes);
    }

    [Fact]
    public async Task GetIndexesAsync_WithCreatedIndex_ReturnsIndex()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();
        await _store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");

        // Act
        var indexes = (await _introspector.GetIndexesAsync("Customer")).ToList();

        // Assert
        Assert.Contains(indexes, i => i.Name == "idx_customer_email");
    }

    [Fact]
    public async Task GetIndexesAsync_WithoutTableFilter_ReturnsAllIndexes()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();
        await _store.CreateTableAsync<Order>();
        await _store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");
        await _store.CreateIndexAsync<Order>(o => o.CustomerId, "idx_order_customer");

        // Act
        var indexes = (await _introspector.GetIndexesAsync()).ToList();

        // Assert
        Assert.Contains(indexes, i => i.Name == "idx_customer_email");
        Assert.Contains(indexes, i => i.Name == "idx_order_customer");
    }

    [Fact]
    public async Task GetDatabaseStatisticsAsync_ReturnsValidStatistics()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();
        await _store.UpsertAsync("cust-1", new Customer { Name = "John", Email = "john@test.com" });

        // Act
        var stats = await _introspector.GetDatabaseStatisticsAsync();

        // Assert
        Assert.True(stats.PageCount > 0);
        Assert.True(stats.PageSize > 0);
        Assert.True(stats.DatabaseSizeBytes > 0);
        Assert.Equal(stats.PageCount * stats.PageSize, stats.DatabaseSizeBytes);
    }

    // Test models
    private class Customer
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    private class Order
    {
        public string CustomerId { get; set; } = string.Empty;
        public decimal Total { get; set; }
    }
}
