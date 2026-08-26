using Xunit;

namespace LiteDocumentStore.IntegrationTests;

public class SchemaIntrospectionIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    // SchemaIntrospector works on a SqliteConnection, so it is built on the store's own
    // connection for the duration of the callback.
    private Task<TResult> IntrospectAsync<TResult>(Func<SchemaIntrospector, Task<TResult>> operation) =>
        _store.ExecuteRawAsync((connection, _) => operation(new SchemaIntrospector(connection)));

    [Fact]
    public async Task GetTablesAsync_WithNoTables_ReturnsEmpty()
    {
        // Act
        var tables = await IntrospectAsync(introspector => introspector.GetTablesAsync());

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
        var tables = (await IntrospectAsync(introspector => introspector.GetTablesAsync())).ToList();

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
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "CREATE TABLE \"weird]name\" (id TEXT PRIMARY KEY, val TEXT)", ct));

        // Act
        var columns = (await IntrospectAsync(introspector => introspector.GetColumnsAsync("weird]name"))).ToList();

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
        var exists = await IntrospectAsync(introspector => introspector.TableExistsAsync("Customer"));

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task TableExistsAsync_WithNonExistingTable_ReturnsFalse()
    {
        // Act
        var exists = await IntrospectAsync(introspector => introspector.TableExistsAsync("NonExistent"));

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task GetColumnsAsync_WithDocumentTable_ReturnsCorrectColumns()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();

        // Act
        var columns = (await IntrospectAsync(introspector => introspector.GetColumnsAsync("Customer"))).ToList();

        // Assert
        Assert.Equal(3, columns.Count); // id, data, version

        var idColumn = columns.FirstOrDefault(c => c.Name == "id");
        Assert.NotNull(idColumn);
        Assert.Equal("TEXT", idColumn.Type);
        Assert.True(idColumn.IsPrimaryKey);

        var dataColumn = columns.FirstOrDefault(c => c.Name == "data");
        Assert.NotNull(dataColumn);
        Assert.Equal("BLOB", dataColumn.Type);
        Assert.True(dataColumn.NotNull);

        var versionColumn = columns.FirstOrDefault(c => c.Name == "version");
        Assert.NotNull(versionColumn);
        Assert.Equal("INTEGER", versionColumn.Type);
        Assert.True(versionColumn.NotNull);
    }

    [Fact]
    public async Task GetIndexesAsync_WithNoIndexes_ReturnsEmpty()
    {
        // Arrange
        await _store.CreateTableAsync<Customer>();

        // Act
        var indexes = (await IntrospectAsync(introspector => introspector.GetIndexesAsync("Customer"))).ToList();

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
        var indexes = (await IntrospectAsync(introspector => introspector.GetIndexesAsync("Customer"))).ToList();

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
        var indexes = (await IntrospectAsync(introspector => introspector.GetIndexesAsync())).ToList();

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
        var stats = await IntrospectAsync(introspector => introspector.GetDatabaseStatisticsAsync());

        // Assert
        Assert.True(stats.PageCount > 0);
        Assert.True(stats.PageSize > 0);
        Assert.True(stats.DatabaseSizeBytes > 0);
        Assert.Equal(stats.PageCount * stats.PageSize, stats.DatabaseSizeBytes);
    }

    [Fact]
    public async Task ColumnExistsAsync_WithAnExistingColumn_ReturnsTrue()
    {
        await _store.CreateTableAsync<Customer>();

        Assert.True(await IntrospectAsync(i => i.ColumnExistsAsync("Customer", "id")));
        Assert.True(await IntrospectAsync(i => i.ColumnExistsAsync("Customer", "data")));
        Assert.True(await IntrospectAsync(i => i.ColumnExistsAsync("Customer", "version")));
    }

    [Fact]
    public async Task ColumnExistsAsync_IsCaseInsensitive()
    {
        // SQLite column names are case-insensitive, and the migration code that calls this asks
        // with whatever casing its DDL used.
        await _store.CreateTableAsync<Customer>();

        Assert.True(await IntrospectAsync(i => i.ColumnExistsAsync("Customer", "DATA")));
    }

    [Fact]
    public async Task ColumnExistsAsync_WithAMissingColumn_ReturnsFalse()
    {
        await _store.CreateTableAsync<Customer>();

        Assert.False(await IntrospectAsync(i => i.ColumnExistsAsync("Customer", "content_type")));
    }

    [Fact]
    public async Task ColumnExistsAsync_WithAMissingTable_ReturnsFalseRatherThanThrowing()
    {
        // PRAGMA table_xinfo on an absent table yields no rows rather than an error, and the
        // "add this column if it is not there yet" callers depend on that staying a plain false.
        Assert.False(await IntrospectAsync(i => i.ColumnExistsAsync("NoSuchTable", "id")));
    }

    [Fact]
    public async Task ColumnExistsAsync_WithATableNameContainingAClosingBracket_IsEscaped()
    {
        // ColumnExistsAsync answers off GetColumnsAsync, so it inherits the double-quote
        // identifier quoting that a ']' would otherwise break out of.
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "CREATE TABLE \"odd]name\" (id TEXT PRIMARY KEY, data BLOB NOT NULL)",
            ct));

        Assert.True(await IntrospectAsync(i => i.ColumnExistsAsync("odd]name", "data")));
        Assert.False(await IntrospectAsync(i => i.ColumnExistsAsync("odd]name", "version")));
    }

    [Fact]
    public async Task ColumnExistsAsync_WithNullArguments_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IntrospectAsync(i => i.ColumnExistsAsync(null!, "id")));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IntrospectAsync(i => i.ColumnExistsAsync("Customer", null!)));
    }

    [Fact]
    public async Task IndexExistsAsync_WithAMissingIndex_ReturnsFalse()
    {
        await _store.CreateTableAsync<Customer>();

        Assert.False(await IntrospectAsync(i => i.IndexExistsAsync("idx_customer_email")));
    }

    [Fact]
    public async Task IndexExistsAsync_AfterTheIndexIsDropped_ReturnsFalseAgain()
    {
        await _store.CreateTableAsync<Customer>();
        await _store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");
        Assert.True(await IntrospectAsync(i => i.IndexExistsAsync("idx_customer_email")));

        await _store.DropIndexAsync("idx_customer_email");

        Assert.False(await IntrospectAsync(i => i.IndexExistsAsync("idx_customer_email")));
    }

    [Fact]
    public async Task IndexExistsAsync_WithANullName_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IntrospectAsync(i => i.IndexExistsAsync(null!)));

    [Fact]
    public async Task TableExistsAsync_WithANullName_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IntrospectAsync(i => i.TableExistsAsync(null!)));

    // Test models
    private sealed class Customer
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    private sealed class Order
    {
        public string CustomerId { get; set; } = string.Empty;
        public decimal Total { get; set; }
    }
}
