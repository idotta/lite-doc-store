using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Who a rejection blames, measured end to end against real SQLite. Nothing here changes which
/// inputs are refused — only the <c>ParamName</c> and the message, which used to name a parameter
/// the caller never passed: a private <c>operation</c>, the generator's own <c>operations</c> and
/// <c>tableName</c>, or, for a table name a custom convention returned, the caller's own perfectly
/// valid <c>jsonPath</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ParameterAttributionIntegrationTests : IAsyncLifetime
{
    private sealed record Gadget(string Id, string Name);

    /// <summary>
    /// Returns a name the identifier rule refuses. Only a custom convention can produce one —
    /// <see cref="DefaultTableNamingConvention"/> throws <see cref="NotSupportedException"/>
    /// rather than folding a type into a name like this.
    /// </summary>
    private sealed class NonIdentifierConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => "bad-name";

        public string GetTableName(Type type) => "bad-name";
    }

    private IDocumentStore _store = null!;
    private IDocumentStore _badlyNamedStore = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Gadget>();
        await _store.UpsertAsync("g1", new Gadget("g1", "Anvil"));

        var options = DocumentStoreOptions.ForInMemory();
        options.TableNamingConvention = new NonIdentifierConvention();
        _badlyNamedStore = await new DocumentStoreFactory().CreateAsync(options);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _badlyNamedStore.DisposeAsync();
    }

    // --- The patch caps, which only the generator enforces ---------------------------------
    //
    // DocumentPatch<T> counts nothing, so GeneratePatchSql is the first and only validator of
    // both caps and is reached straight from PatchAsync. It used to report its own parameter,
    // 'operations'; the caller passed 'patch'. These are the call-site pins — the unit tests can
    // only assert that the generator reports the name it is handed.

    [Fact]
    public async Task PatchAsync_PastTheSetCap_BlamesThePatchParameter()
    {
        var patch = DocumentPatch<Gadget>.Set("$.f0", 0);
        for (var i = 1; i <= SqlGenerator.MaxPatchSetOperations; i++)
        {
            patch = patch.AndSet($"$.f{i}", i);
        }

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.PatchAsync("g1", patch));

        Assert.Equal("patch", exception.ParamName);
    }

    [Fact]
    public async Task PatchWithVersionAsync_PastTheRemoveCap_BlamesThePatchParameter()
    {
        var patch = DocumentPatch<Gadget>.Remove("$.f0");
        for (var i = 1; i <= SqlGenerator.MaxPatchRemoveOperations; i++)
        {
            patch = patch.AndRemove($"$.f{i}");
        }

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.PatchWithVersionAsync("g1", patch, 1));

        Assert.Equal("patch", exception.ParamName);
    }

    // --- A table name the configured convention produced -----------------------------------
    //
    // The name has no caller parameter behind it, so it is refused where it is produced, by the
    // guard every store wraps its convention in, and reported against the convention. Every entry
    // point below used to report an argument the caller did pass and that was not at fault.

    private static void AssertBlamesTheConvention(Exception? exception)
    {
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(nameof(NonIdentifierConvention), invalid.Message, StringComparison.Ordinal);
        Assert.Contains("bad-name", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexAsync_UnderANonIdentifierTableName_BlamesTheConventionNotThePath() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.CreateIndexAsync<Gadget>("$.Name")));

    [Fact]
    public async Task CreateCompositeIndexAsync_UnderANonIdentifierTableName_BlamesTheConvention() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.CreateCompositeIndexAsync<Gadget>(["$.Name", "$.Id"])));

    [Fact]
    public async Task DropIndexAsync_UnderANonIdentifierTableName_BlamesTheConventionNotTheExpression() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.DropIndexAsync<Gadget>(x => x.Name)));

    // The gap PR #99 documented rather than closed: AddVirtualColumnAsync's hoisted checks
    // deliberately left tableName to the generator, which an existing column short-circuits past,
    // so the same call was an ArgumentException naming 'tableName' on one branch and a silent
    // no-op on the other. Refusing the name where the convention produces it closes both branches
    // — the call cannot reach the column check at all.

    [Fact]
    public async Task AddVirtualColumnAsync_UnderANonIdentifierTableName_BlamesTheConvention() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.AddVirtualColumnAsync<Gadget>("$.Name", "name_col", createIndex: false)));

    [Fact]
    public async Task AddVirtualColumnAsync_WithAnIndex_UnderANonIdentifierTableName_BlamesTheConvention() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.AddVirtualColumnAsync<Gadget>("$.Name", "name_col", createIndex: true)));

    // The guard sits at the one point every operation passes, so the refusal is not specific to
    // the DDL: the store never opens a table under a name it cannot write into a statement.

    [Fact]
    public async Task CreateTableAsync_UnderANonIdentifierTableName_BlamesTheConvention() =>
        AssertBlamesTheConvention(await Record.ExceptionAsync(
            () => _badlyNamedStore.CreateTableAsync<Gadget>()));

    // A valid custom name is untouched — the screening refuses a shape, not a custom convention.

    [Fact]
    public async Task AValidCustomTableName_StillDerivesItsIndexName()
    {
        await _store.CreateIndexAsync<Gadget>("$.Name");

        var count = await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", $"idx_{_store.GetTableName<Gadget>()}_Name")));

        Assert.Equal(1, count);
    }
}
