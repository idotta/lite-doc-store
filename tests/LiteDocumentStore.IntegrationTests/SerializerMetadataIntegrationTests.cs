using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// A store whose <c>SerializerOptions</c> carry a resolver that does not cover the document type —
/// the shape a source-generated context produces when a <c>[JsonSerializable]</c> is missing.
/// Validation cannot see it (the resolver is present, and coverage is per type), so the failure
/// surfaces at the operation; it has to arrive as a typed
/// <see cref="DocumentSerializationException"/> rather than the raw
/// <see cref="NotSupportedException"/> the read paths used to let through.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SerializerMetadataIntegrationTests : IAsyncLifetime
{
    private sealed class Doc
    {
        public string Name { get; set; } = string.Empty;
    }

    private const string CacheName = "lds-serializer-metadata";

    // The seed store holds the shared-cache database alive: it dies with its last connection, and
    // its row is what the restricted store below reads back.
    private IDocumentStore _seed = null!;
    private IDocumentStore _restricted = null!;

    public async Task InitializeAsync()
    {
        var seedOptions = DocumentStoreOptions.ForSharedInMemory(CacheName);
        _seed = await new DocumentStoreFactory().CreateAsync(seedOptions);
        await _seed.CreateTableAsync<Doc>();
        await _seed.UpsertAsync("a", new Doc { Name = "n" });

        var restrictedOptions = DocumentStoreOptions.ForSharedInMemory(CacheName);
        restrictedOptions.SerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine()
        };

        _restricted = await new DocumentStoreFactory().CreateAsync(restrictedOptions);
    }

    public async Task DisposeAsync()
    {
        await _restricted.DisposeAsync();
        await _seed.DisposeAsync();
    }

    [Fact]
    public async Task GetAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        var ex = await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.GetAsync<Doc>("a"));

        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public async Task GetAllAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.GetAllAsync<Doc>());
    }

    [Fact]
    public async Task GetManyAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.GetManyAsync<Doc>(["a"]));
    }

    [Fact]
    public async Task GetWithVersionAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.GetWithVersionAsync<Doc>("a"));
    }

    [Fact]
    public async Task QueryAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.QueryAsync<Doc, string>("$.Name", "n"));
    }

    [Fact]
    public async Task UpsertAsync_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _restricted.UpsertAsync("b", new Doc { Name = "n" }));
    }

    [Fact]
    public void DeserializeDocument_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        Assert.Throws<DocumentSerializationException>(
            () => _restricted.DeserializeDocument<Doc>("""{"Name":"n"}"""));
    }

    [Fact]
    public async Task OperationsThatDoNotTouchThePayload_StillWork()
    {
        // The metadata gap is per type and per payload: counting and deleting never deserialize.
        Assert.Equal(1, await _restricted.CountAsync<Doc>());
        Assert.True(await _restricted.ExistsAsync<Doc>("a"));
    }
}
