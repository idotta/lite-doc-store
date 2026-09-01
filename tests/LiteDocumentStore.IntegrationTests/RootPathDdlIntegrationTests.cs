using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The document root <c>$</c> against the projecting DDL, over real SQLite: every string-path
/// form rejects it, the rejection names the path parameter rather than an index name the caller
/// never passed, and nothing lands in the schema. An indexer at the root stays legal.
/// </summary>
/// <remarks>
/// Before this guard, an explicitly named index and a virtual column over <c>$</c> both
/// succeeded — <c>json_extract(data, '$')</c> keys on (or duplicates) the whole serialized
/// document — while the auto-named forms failed for the wrong reason: the derived name
/// <c>idx_Member_$</c> was rejected by the identifier validator and reported against
/// <c>indexName</c>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RootPathDdlIntegrationTests : IAsyncLifetime
{
    private sealed record Member(string Id, string? Email);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Member>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private Task<int> IndexCountAsync() =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL", ct));

    private Task<int> ColumnCountAsync() =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM pragma_table_xinfo('{_store.GetTableName<Member>()}')", ct));

    // --- Single-path index ------------------------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithTheDocumentRootAndAnExplicitName_ThrowsAndCreatesNothing()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$", "idx_whole_document"));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    [Fact]
    public async Task CreateIndexAsync_WithTheDocumentRootAndNoName_ThrowsAgainstThePathNotTheIndexName()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$"));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    [Fact]
    public async Task CreateIndexAsync_WithTheDocumentRootAndOptions_Throws()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$", "idx_whole_document", new IndexOptions { Unique = true }));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    // --- Composite index --------------------------------------------------------------------

    [Fact]
    public async Task CreateCompositeIndexAsync_WithTheDocumentRootAndAnExplicitName_ThrowsAndCreatesNothing()
    {
        // The root sits second: every component is validated, not only the first.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Member>(["$.Email", "$"], "idx_composite_root"));

        Assert.Equal("jsonPaths", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WithTheDocumentRootAndNoName_ThrowsAgainstThePathsNotTheIndexName()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Member>(["$.Email", "$"]));

        Assert.Equal("jsonPaths", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WithTheDocumentRootAndOptions_Throws()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Member>(
                ["$.Email", "$"],
                "idx_composite_root",
                new IndexOptions { Unique = true }));

        Assert.Equal("jsonPaths", exception.ParamName);
        Assert.Equal(0, await IndexCountAsync());
    }

    // --- Virtual column ---------------------------------------------------------------------

    [Fact]
    public async Task AddVirtualColumnAsync_WithTheDocumentRoot_ThrowsAndAddsNoColumn()
    {
        var before = await ColumnCountAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Member>("$", "vc_root"));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Equal(before, await ColumnCountAsync());
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithTheDocumentRootOverAnExistingColumn_StillThrows()
    {
        // An existing column short-circuits past the generator, so only the call-site guard
        // rejects the root here: without it the call is a silent no-op.
        await _store.AddVirtualColumnAsync<Member>("$.Email", "vc_email");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Member>("$", "vc_email"));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    // --- An indexer at the root still reaches into the document -----------------------------

    [Fact]
    public async Task CreateIndexAsync_WithAnIndexerAtTheRootAndAnExplicitName_CreatesTheIndex()
    {
        // Explicitly named: auto-naming an indexer stays rejected, by RequireDerivableName.
        await _store.CreateIndexAsync<Member>("$[0]", "idx_first_element");

        var sql = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", "idx_first_element")));

        Assert.Contains("json_extract(data, '$[0]')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithAnIndexerAtTheRoot_AddsTheColumn()
    {
        await _store.AddVirtualColumnAsync<Member>("$[0]", "vc_first");

        var exists = await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM pragma_table_xinfo('{_store.GetTableName<Member>()}') " +
            "WHERE name = 'vc_first'", ct));

        Assert.Equal(1, exists);
    }
}
