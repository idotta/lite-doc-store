using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The readable half of a derived index name is a fold and still maps distinct inputs onto one
/// spelling; the digest each derived name carries is what keeps the three families apart, which
/// is what these tests pin. The <c>sqlite_master</c> pre-check is not retired by that — a name a
/// caller passed, or one a consumer's own SQL created, can still be held by a different
/// definition, and turning that into a silently skipped creation would leave every query over the
/// losing path on a table scan. So the guard is pinned here too, alongside the idempotent
/// re-creation it must not refuse and the byte-identical round trip that keeps its comparison
/// honest.
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

    private string DerivedName(string jsonPath) => DocumentOperations.GenerateIndexName(Table, jsonPath);

    private string DerivedCompositeName(params string[] jsonPaths) =>
        DocumentOperations.GenerateCompositeIndexName(Table, jsonPaths);

    private string DerivedColumnName(string columnName) =>
        DocumentOperations.GenerateColumnIndexName(Table, columnName);

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
    public async Task CreateIndexAsync_WhenAVirtualColumnIndexCoversTheSameMember_CreatesItsOwnIndex()
    {
        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        await _store.CreateIndexAsync<Member>(x => x.Email!);

        var columnIndex = DerivedColumnName("Email");
        var expressionIndex = DerivedName("$.Email");

        Assert.NotEqual(columnIndex, expressionIndex);
        Assert.Contains("([Email])", await IndexDdlAsync(columnIndex), StringComparison.Ordinal);
        Assert.Contains(
            "json_extract(data, '$.Email')",
            await IndexDdlAsync(expressionIndex),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WhenAnExpressionIndexCoversTheSameMember_AddsItsOwnIndex()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);

        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        Assert.Equal(1, await ColumnCountAsync("Email"));
        Assert.Contains(
            "([Email])",
            await IndexDdlAsync(DerivedColumnName("Email")),
            StringComparison.Ordinal);
        Assert.Contains(
            "json_extract(data, '$.Email')",
            await IndexDdlAsync(DerivedName("$.Email")),
            StringComparison.Ordinal);
    }

    // --- Family 1: a dotted path against an underscored one -------------------------------

    [Fact]
    public async Task CreateIndexAsync_WhenADottedAndAnUnderscoredPathFlattenAlike_CreatesTwoIndexes()
    {
        await _store.CreateIndexAsync<Member>("$.A.B");
        await _store.CreateIndexAsync<Member>("$.A_B");

        var dotted = DerivedName("$.A.B");
        var underscored = DerivedName("$.A_B");

        Assert.NotEqual(dotted, underscored);
        Assert.Contains("json_extract(data, '$.A.B')", await IndexDdlAsync(dotted), StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.A_B')", await IndexDdlAsync(underscored), StringComparison.Ordinal);
    }

    // --- Family 2: two composite paths against one --------------------------------------

    [Fact]
    public async Task CreateCompositeIndexAsync_WhenTwoPathListsFlattenAlike_CreatesTwoIndexes()
    {
        await _store.CreateCompositeIndexAsync<Member>(["$.A", "$.B"]);
        await _store.CreateCompositeIndexAsync<Member>(["$.A.B"]);

        var pair = DerivedCompositeName("$.A", "$.B");
        var single = DerivedCompositeName("$.A.B");

        Assert.NotEqual(pair, single);
        Assert.Contains(
            "json_extract(data, '$.A'), json_extract(data, '$.B')",
            await IndexDdlAsync(pair),
            StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.A.B')", await IndexDdlAsync(single), StringComparison.Ordinal);
    }

    // --- The guard the digest does not retire: a name the caller chose --------------------

    [Fact]
    public async Task CreateIndexAsync_WhenAnExplicitNameHoldsADifferentDefinition_Throws()
    {
        await _store.CreateIndexAsync<Member>("$.Age", "idx_members_pinned");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateIndexAsync<Member>("$.Email", "idx_members_pinned"));

        Assert.Contains("idx_members_pinned", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Age')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Email')", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WhenAnExplicitNameHoldsADifferentDefinition_Throws()
    {
        await _store.CreateCompositeIndexAsync<Member>(["$.A", "$.B"], "idx_members_pair_pinned");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CreateCompositeIndexAsync<Member>(["$.A.B"], "idx_members_pair_pinned"));

        Assert.Contains("idx_members_pair_pinned", exception.Message, StringComparison.Ordinal);
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
        var name = DerivedName("$.Email");

        await _store.CreateIndexAsync<Member>(x => x.Email!, null, options);
        var before = await IndexDdlAsync(name);

        await _store.CreateIndexAsync<Member>(x => x.Email!, null, options);

        Assert.Equal(1, await IndexCountAsync(name));
        Assert.Equal(before, await IndexDdlAsync(name));
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WhenCalledTwiceIdentically_LeavesTheOneIndexUntouched()
    {
        await _store.CreateCompositeIndexAsync<Member>([x => x.Email!, x => x.Age]);
        var name = DerivedCompositeName("$.Email", "$.Age");
        var before = await IndexDdlAsync(name);

        await _store.CreateCompositeIndexAsync<Member>([x => x.Email!, x => x.Age]);

        Assert.Equal(1, await IndexCountAsync(name));
        Assert.Equal(before, await IndexDdlAsync(name));
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WhenCalledTwiceIdentically_LeavesTheOneIndexUntouched()
    {
        var name = DerivedColumnName("Email");

        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);
        var before = await IndexDdlAsync(name);

        await _store.AddVirtualColumnAsync<Member>("$.Email", "Email", createIndex: true);

        Assert.Equal(1, await IndexCountAsync(name));
        Assert.Equal(before, await IndexDdlAsync(name));
    }

    // --- AddVirtualColumnAsync refuses before it commits anything -------------------------

    private Task<long> ColumnCountAsync(string columnName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM pragma_table_xinfo('{Table}') WHERE name = @Name",
            ct,
            ("Name", columnName)));

    [Fact]
    public async Task AddVirtualColumnAsync_WhenAnotherIndexHoldsTheDerivedName_AddsNoColumn()
    {
        // The ALTER commits immediately outside an ambient transaction, so refusing the index
        // after it would leave the call half applied — the column added and the call failed. The
        // name is claimed explicitly because no derivation produces it a second time.
        await _store.CreateIndexAsync<Member>("$.Age", DerivedColumnName("Email"));

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
        await _store.CreateIndexAsync<Member>("$.Age", DerivedColumnName("Email"));

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
                Table, DerivedName("$.Email"), "$.Email", null, ifNotExists: false),
            await IndexDdlAsync(DerivedName("$.Email")));
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
                Table, DerivedColumnName("Email"), "Email", ifNotExists: false),
            await IndexDdlAsync(DerivedColumnName("Email")));
    }
}
