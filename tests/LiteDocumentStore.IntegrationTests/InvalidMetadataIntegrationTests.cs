using System.Text.Json.Serialization;
using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// A document type whose JSON contract is invalid makes System.Text.Json raise
/// <see cref="InvalidOperationException"/> out of <c>GetTypeInfo</c>. Through the store that used
/// to escape the serialization funnel raw; it is a <see cref="DocumentSerializationException"/> now.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InvalidMetadataIntegrationTests : IDisposable
{
    private readonly IDocumentStore _store = new DocumentStoreFactory().Create(DocumentStoreOptions.ForInMemory());

    public sealed class CollidingNames
    {
        [JsonPropertyName("x")] public string One { get; set; } = string.Empty;
        [JsonPropertyName("x")] public string Two { get; set; } = string.Empty;
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task StorePaths_WithCollidingJsonPropertyNames_ThrowDocumentSerialization()
    {
        await _store.CreateTableAsync<CollidingNames>();

        var upsert = await Assert.ThrowsAsync<DocumentSerializationException>(
            () => _store.UpsertAsync("a", new CollidingNames()));
        var serialize = Assert.Throws<DocumentSerializationException>(
            () => _store.SerializeDocument(new CollidingNames()));
        var deserialize = Assert.Throws<DocumentSerializationException>(
            () => _store.DeserializeDocument<CollidingNames>("""{"x":"n"}"""));

        foreach (var ex in new[] { upsert, serialize, deserialize })
        {
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Contains("type metadata is invalid", ex.Message, StringComparison.Ordinal);
        }
    }
}
