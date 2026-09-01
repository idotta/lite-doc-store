using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Two types with the same simple name, and a generic document type, against real SQLite.
/// </summary>
/// <remarks>
/// Measured before the fix, on this exact shape: both <c>Customer</c> types mapped to the table
/// <c>Customer</c>, the second <c>CreateTableAsync</c> silently no-opped through
/// <c>IF NOT EXISTS</c>, an upsert of one type overwrote the other type's row at the same id,
/// <c>GetAsync</c> handed back a fabricated document with <c>null</c> in a non-nullable member, and
/// <c>GetAllAsync&lt;T&gt;</c> returned the other type's documents. No exception anywhere — which is
/// why the default now folds the namespace, and why a store refuses a residual collision outright.
/// A generic type threw <c>Invalid SQL identifier 'Box`1'</c> against <c>tableName</c>, a parameter
/// no caller passed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TableNamingIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _store = null!;

    public async Task InitializeAsync() =>
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private sealed record Box<T>(T Value);

    [Fact]
    public async Task TwoTypesWithTheSameSimpleName_GetDistinctTablesAndDoNotOverwriteEachOther()
    {
        var firstTable = _store.GetTableName<NamingA.Customer>();
        var secondTable = _store.GetTableName<NamingB.Customer>();
        Assert.NotEqual(firstTable, secondTable);

        await _store.CreateTableAsync<NamingA.Customer>();
        await _store.CreateTableAsync<NamingB.Customer>();

        await _store.UpsertAsync("k1", new NamingA.Customer("k1", "ada@example.com"));
        await _store.UpsertAsync("k1", new NamingB.Customer("k1", 42));

        // Neither write touched the other's row, and neither read sees the other's documents.
        Assert.Equal(new NamingA.Customer("k1", "ada@example.com"), await _store.GetAsync<NamingA.Customer>("k1"));
        Assert.Equal(new NamingB.Customer("k1", 42), await _store.GetAsync<NamingB.Customer>("k1"));

        await _store.UpsertAsync("k2", new NamingB.Customer("k2", 7));
        Assert.Single(await _store.GetAllAsync<NamingA.Customer>());
        Assert.Equal(2, await _store.CountAsync<NamingB.Customer>());

        Assert.Equal(2, await TableCountAsync(firstTable, secondTable));
    }

    [Fact]
    public async Task AGenericDocumentType_RoundTrips()
    {
        var tableName = _store.GetTableName<Box<int>>();
        Assert.EndsWith("_Box_1_System_Int32", tableName, StringComparison.Ordinal);

        await _store.CreateTableAsync<Box<int>>();
        await _store.UpsertAsync("b1", new Box<int>(7));

        Assert.Equal(new Box<int>(7), await _store.GetAsync<Box<int>>("b1"));
        Assert.Equal(1, await TableCountAsync(tableName));
    }

    /// <summary>
    /// The store refuses the second type to claim a table name, whichever convention produced it —
    /// here a deliberately colliding one, since the default's own residual collisions are unit-tested.
    /// </summary>
    [Fact]
    public async Task AConventionThatCollides_IsRefusedRatherThanServed()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.TableNamingConvention = new OneTableConvention();
        await using var store = await new DocumentStoreFactory().CreateAsync(options);

        await store.CreateTableAsync<NamingA.Customer>();
        await store.UpsertAsync("k1", new NamingA.Customer("k1", "ada@example.com"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync("k1", new NamingB.Customer("k1", 42)));
        Assert.Contains("already mapped", exception.Message, StringComparison.Ordinal);

        // The first type's document is intact, which is what the refusal protects.
        Assert.Equal(new NamingA.Customer("k1", "ada@example.com"), await store.GetAsync<NamingA.Customer>("k1"));
    }

    [Fact]
    public async Task AConventionThatCollidesOnlyByCase_IsRefusedRatherThanServed()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.TableNamingConvention = new CaseCollidingConvention();
        await using var store = await new DocumentStoreFactory().CreateAsync(options);

        await store.CreateTableAsync<NamingA.Customer>();
        await store.UpsertAsync("k1", new NamingA.Customer("k1", "ada@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync("k1", new NamingB.Customer("k1", 42)));
    }

    private sealed class OneTableConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => "Shared";

        public string GetTableName(Type type) => "Shared";
    }

    private sealed class CaseCollidingConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => GetTableName(typeof(T));

        public string GetTableName(Type type) => type == typeof(NamingA.Customer) ? "Shared" : "shared";
    }

    private Task<int> TableCountAsync(params string[] names) =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (" +
            string.Join(", ", names.Select(name => $"'{name}'")) + ")",
            ct));
}
