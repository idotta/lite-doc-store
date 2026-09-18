using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The derived index-name scheme is not injective, and the <c>sqlite_master</c> pre-check used
/// to turn every residual collision into a silently skipped creation — leaving the losing path
/// unindexed and every query over it on a table scan. These tests pin the three collision
/// families now being loud, that an identical re-creation is still the idempotent no-op it
/// always was, and that the definition a store creates is byte-identical to the comparison form
/// the generators emit, which is what keeps the comparison honest.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexNameCollisionIntegrationTests : IAsyncLifetime
{
    private sealed record Member(string Id, string? Email, int Age);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Member>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private string Table => _store.GetTableName<Member>();

    private Task<string?> IndexDdlAsync(string indexName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

    private Task<long> IndexCountAsync(string indexName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

    // --- Family 3: a virtual column's index against the expression index for the same member -

    [Fact]
    public async Task CreateIndexAsync_WhenAVirtualColumnIndexHoldsTheDerivedName_Throws()
    {
        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateIndexAsync<Member>(x => x.Email!));

        Assert.Contains($"idx_{Table}_Email", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"ON [{Table}] ([Email])", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Email')", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WhenAnExpressionIndexHoldsTheDerivedName_Throws()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true));

        Assert.Contains($"idx_{Table}_Email", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Email')", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"ON [{Table}] ([Email])", exception.Message, StringComparison.Ordinal);
    }

    // --- Family 1: a dotted path against an underscored one -------------------------------

    [Fact]
    public async Task CreateIndexAsync_WhenADottedAndAnUnderscoredPathDeriveOneName_Throws()
    {
        await _store.CreateIndexAsync<Member>("$.A.B");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateIndexAsync<Member>("$.A_B"));

        Assert.Contains($"idx_{Table}_A_B", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.A.B')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.A_B')", exception.Message, StringComparison.Ordinal);
    }

    // --- Family 2: two composite paths against one --------------------------------------

    [Fact]
    public async Task CreateCompositeIndexAsync_WhenTwoPathListsDeriveOneName_Throws()
    {
        await _store.CreateCompositeIndexAsync<Member>(["$.A", "$.B"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateCompositeIndexAsync<Member>(["$.A.B"]));

        Assert.Contains($"idx_{Table}_composite_A_B", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "json_extract(data, '$.A'), json_extract(data, '$.B')",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.A.B')", exception.Message, StringComparison.Ordinal);
    }

    // --- Idempotence: the guard compares, it does not reject every existing name -----------

    [Fact]
    public async Task CreateIndexAsync_WhenCalledTwiceIdentically_LeavesTheOneIndexUntouched()
    {
        var options = new IndexOptions { Unique = true, Collation = "NOCASE" };

        await _store.CreateIndexAsync<Member>(x => x.Email!, null, options);
        var before = await IndexDdlAsync($"idx_{Table}_Email");

        await _store.CreateIndexAsync<Member>(x => x.Email!, null, options);

        Assert.Equal(1, await IndexCountAsync($"idx_{Table}_Email"));
        Assert.Equal(before, await IndexDdlAsync($"idx_{Table}_Email"));
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WhenCalledTwiceIdentically_LeavesTheOneIndexUntouched()
    {
        await _store.CreateCompositeIndexAsync<Member>([x => x.Email!, x => x.Age]);
        var name = $"idx_{Table}_composite_Email_Age";
        var before = await IndexDdlAsync(name);

        await _store.CreateCompositeIndexAsync<Member>([x => x.Email!, x => x.Age]);

        Assert.Equal(1, await IndexCountAsync(name));
        Assert.Equal(before, await IndexDdlAsync(name));
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WhenCalledTwiceIdentically_LeavesTheOneIndexUntouched()
    {
        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);
        var before = await IndexDdlAsync($"idx_{Table}_Email");

        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        Assert.Equal(1, await IndexCountAsync($"idx_{Table}_Email"));
        Assert.Equal(before, await IndexDdlAsync($"idx_{Table}_Email"));
    }

    // --- AddVirtualColumnAsync refuses before it commits anything -------------------------

    private Task<long> ColumnCountAsync(string columnName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM pragma_table_xinfo('{Table}') WHERE name = @Name",
            ct,
            ("Name", columnName)));

    [Fact]
    public async Task AddVirtualColumnAsync_WhenAnExpressionIndexHoldsTheName_AddsNoColumn()
    {
        // The ALTER commits immediately outside an ambient transaction, so refusing the index
        // after it would leave the call half applied — the column added and the call failed.
        await _store.CreateIndexAsync<Member>(x => x.Email!);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true));

        Assert.Equal(0, await ColumnCountAsync("Email"));
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WhenTheColumnExistsAndAnIndexHoldsTheName_Throws()
    {
        // The existing-column short-circuit skips the ALTER, so this path never wrote anything
        // either way — it must still refuse, and refuse identically.
        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: false);
        await _store.DropIndexAsync($"idx_{Table}_Email");
        await _store.CreateIndexAsync<Member>("$.Age", $"idx_{Table}_Email");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true));

        Assert.Equal(1, await ColumnCountAsync("Email"));
        Assert.Contains("json_extract(data, '$.Age')", exception.Message, StringComparison.Ordinal);
    }

    // --- The index SQLite made for itself, which has no CREATE statement to compare -------

    [Fact]
    public async Task CreateIndexAsync_WhenAnInternalIndexHoldsTheName_Throws()
    {
        // The store's own DDL declares `id TEXT PRIMARY KEY`, so SQLite creates an
        // sqlite_autoindex_* for it — the one shape whose sqlite_master.sql is NULL. There is
        // nothing to compare against and the name cannot be recreated, so it is a mismatch.
        var internalIndex = await _store.ExecuteRawAsync((connection, ct) =>
            connection.QueryFirstStringAsync(
                "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NULL AND tbl_name = @Table",
                ct,
                ("Table", Table)));

        Assert.NotNull(internalIndex);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateIndexAsync<Member>("$.Email", internalIndex));

        Assert.Contains(internalIndex, exception.Message, StringComparison.Ordinal);
        Assert.Contains("<an internal index with no CREATE statement>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Email')", exception.Message, StringComparison.Ordinal);
    }

    // --- The round trip that keeps the comparison honest ----------------------------------

    [Fact]
    public async Task CreateIndexAsync_StoresExactlyTheGeneratorsComparisonForm()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);

        Assert.Equal(
            SqlGenerator.GenerateCreateJsonIndexSql(
                Table, $"idx_{Table}_Email", "$.Email", null, ifNotExists: false),
            await IndexDdlAsync($"idx_{Table}_Email"));
    }

    [Fact]
    public async Task CreateIndexAsync_WithEveryOption_StoresExactlyTheGeneratorsComparisonForm()
    {
        var options = new IndexOptions
        {
            Unique = true,
            Collation = "NOCASE",
            Descending = true,
            Filter = IndexFilter.IsNotNull("$.Email"),
        };

        await _store.CreateIndexAsync<Member>(x => x.Email!, "idx_members_email_live", options);

        Assert.Equal(
            SqlGenerator.GenerateCreateJsonIndexSql(
                Table, "idx_members_email_live", "$.Email", options, ifNotExists: false),
            await IndexDdlAsync("idx_members_email_live"));
    }

    [Fact]
    public async Task AddVirtualColumnAsync_StoresExactlyTheGeneratorsComparisonForm()
    {
        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        Assert.Equal(
            SqlGenerator.GenerateCreateColumnIndexSql(
                Table, $"idx_{Table}_Email", "Email", ifNotExists: false),
            await IndexDdlAsync($"idx_{Table}_Email"));
    }
}
