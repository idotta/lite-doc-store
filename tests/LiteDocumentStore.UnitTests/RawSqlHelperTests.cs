using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the three raw-SQL helpers on <see cref="IDocumentOperations"/> —
/// <c>GetTableName</c>, <c>SerializeDocument</c> and <c>DeserializeDocument</c> — which let a
/// caller build SQL for <c>ExecuteRawAsync</c> without hardcoding a table name or hand-rolling
/// System.Text.Json.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RawSqlHelperTests
{
    private sealed record Doc(string Name, int Value);

    private sealed record OtherDoc(string Label);

    /// <summary>
    /// A convention that is deliberately nothing like the default one, so a test using it fails
    /// if <c>GetTableName</c> re-implements the default rule instead of delegating.
    /// </summary>
    private sealed class PrefixedTableNamingConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => GetTableName(typeof(T));

        public string GetTableName(Type type) => $"tbl_{type.Name.ToLowerInvariant()}_docs";
    }

    private static Task<IDocumentStore> CreateStoreAsync(ITableNamingConvention? convention = null)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.TableNamingConvention = convention;

        return new DocumentStoreFactory().CreateAsync(options);
    }

    [Fact]
    public async Task GetTableName_WithTheDefaultConvention_MatchesThatConventionsOutput()
    {
        await using var store = await CreateStoreAsync();
        var convention = new DefaultTableNamingConvention();

        Assert.Equal(convention.GetTableName<Doc>(), store.GetTableName<Doc>());
        Assert.Equal(convention.GetTableName<OtherDoc>(), store.GetTableName<OtherDoc>());
    }

    [Fact]
    public async Task GetTableName_WithACustomConvention_UsesTheConfiguredConvention()
    {
        var convention = new PrefixedTableNamingConvention();
        await using var store = await CreateStoreAsync(convention);

        var tableName = store.GetTableName<Doc>();

        // The whole point of the API: the caller must not have to know the naming rule.
        Assert.Equal(convention.GetTableName<Doc>(), tableName);
        Assert.Equal("tbl_doc_docs", tableName);
        Assert.NotEqual(new DefaultTableNamingConvention().GetTableName<Doc>(), tableName);
    }

    [Fact]
    public async Task SerializeDocument_WithADocument_ProducesUtf8JsonBytes()
    {
        await using var store = await CreateStoreAsync();

        var bytes = store.SerializeDocument(new Doc("Ada", 36));

        Assert.Equal("{\"Name\":\"Ada\",\"Value\":36}", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SerializeDocument_WithNull_ThrowsArgumentNullException()
    {
        await using var store = await CreateStoreAsync();

        Assert.Throws<ArgumentNullException>(() => store.SerializeDocument<Doc>(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DeserializeDocument_WithNullOrEmptyJson_ReturnsDefault(string? json)
    {
        await using var store = await CreateStoreAsync();

        var document = store.DeserializeDocument<Doc>(json);

        Assert.Null(document);
    }

    [Fact]
    public async Task DeserializeDocument_WithMalformedJson_ThrowsSerializationException()
    {
        await using var store = await CreateStoreAsync();

        var ex = Assert.Throws<SerializationException>(() => store.DeserializeDocument<Doc>("{not json"));

        Assert.Equal(typeof(Doc), ex.TargetType);
    }

    [Fact]
    public async Task SerializeAndDeserializeDocument_RoundTripsThroughTheStoresOwnOptions()
    {
        await using var store = await CreateStoreAsync();
        var original = new Doc("Ada", 36);

        var json = System.Text.Encoding.UTF8.GetString(store.SerializeDocument(original));
        var roundTripped = store.DeserializeDocument<Doc>(json);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public async Task GetTableName_AfterDispose_ThrowsObjectDisposedException()
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => store.GetTableName<Doc>());
    }

    [Fact]
    public async Task SerializeDocument_AfterDispose_ThrowsObjectDisposedException()
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => store.SerializeDocument(new Doc("Ada", 36)));
    }

    [Fact]
    public async Task DeserializeDocument_AfterDispose_ThrowsObjectDisposedException()
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            store.DeserializeDocument<Doc>("{\"Name\":\"Ada\",\"Value\":36}"));
    }
}
