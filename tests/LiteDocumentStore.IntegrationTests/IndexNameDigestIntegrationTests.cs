using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// <c>DropIndexAsync&lt;T&gt;(expression)</c> is handed a path, derives a name and drops whatever
/// holds it. While the readable fold was the whole name, an index over <c>$.A_B</c> and one over
/// <c>$.A.B</c> claimed the same name, so dropping by either path could remove the other's index
/// silently — the one collision the <c>sqlite_master</c> pre-check cannot close, because by then
/// only one index bears the name and the caller supplied a path, not a definition. These tests
/// run that defect end to end and check the derived names survive a round trip through SQLite,
/// so the digest suffix cannot quietly stop being a legal identifier.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexNameDigestIntegrationTests : IAsyncLifetime
{
    private sealed record Nested(string B);

    private sealed record Member(string Id, Nested A, string? A_B);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Member>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private string Table => _store.GetTableName<Member>();

    private Task<long> IndexCountAsync(string indexName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

    [Fact]
    public async Task DropIndexAsync_ByExpression_LeavesTheIndexOverTheUnderscoredPathAlone()
    {
        await _store.CreateIndexAsync<Member>("$.A_B");
        await _store.CreateIndexAsync<Member>(x => x.A.B);

        var underscored = DocumentOperations.GenerateIndexName(Table, "$.A_B");
        var dotted = DocumentOperations.GenerateIndexName(Table, "$.A.B");
        Assert.Equal(1, await IndexCountAsync(underscored));
        Assert.Equal(1, await IndexCountAsync(dotted));

        await _store.DropIndexAsync<Member>(x => x.A.B);

        Assert.Equal(0, await IndexCountAsync(dotted));
        Assert.Equal(1, await IndexCountAsync(underscored));
    }

    [Fact]
    public async Task DropIndexAsync_ByExpression_LeavesAGeneratedColumnsIndexAlone()
    {
        // The same silent wrong drop from the other side: the column is named after the member,
        // so its index and the expression index read alike.
        await _store.AddVirtualColumnAsync<Member>("$.A_B", "A_B", createIndex: true);
        await _store.CreateIndexAsync<Member>("$.A_B");

        var columnIndex = DocumentOperations.GenerateColumnIndexName(Table, "A_B");

        await _store.DropIndexAsync<Member>(x => x.A_B!);

        Assert.Equal(0, await IndexCountAsync(DocumentOperations.GenerateIndexName(Table, "$.A_B")));
        Assert.Equal(1, await IndexCountAsync(columnIndex));
    }

    [Fact]
    public async Task EveryDerivedName_RoundTripsThroughSqliteMaster()
    {
        await _store.CreateIndexAsync<Member>(x => x.A.B);
        await _store.CreateCompositeIndexAsync<Member>(["$.A.B", "$.A_B"]);
        await _store.AddVirtualColumnAsync<Member>("$.A_B", "A_B", createIndex: true);

        var names = new[]
        {
            DocumentOperations.GenerateIndexName(Table, "$.A.B"),
            DocumentOperations.GenerateCompositeIndexName(Table, ["$.A.B", "$.A_B"]),
            DocumentOperations.GenerateColumnIndexName(Table, "A_B"),
        };

        foreach (var name in names)
        {
            Assert.True(SqlGenerator.IsValidIdentifier(name), name);
            Assert.Equal(1, await IndexCountAsync(name));
        }

        Assert.Equal(3, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AutoNamingAPathWithAnEmbeddedDollarDot_IsRefusedAgainstThePath()
    {
        // The old fold stripped every "$." and quietly gave "$.a$.b" the name "$.ab" derives.
        // Stripping only the leading one puts a '$' in the readable half, which is no identifier,
        // so the path is refused the way every other non-derivable shape is.
        await _store.CreateIndexAsync<Member>("$.ab");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$.a$.b"));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("$.a$.b", exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit index name", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await IndexCountAsync(DocumentOperations.GenerateIndexName(Table, "$.ab")));
    }
}
