using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The exception contract of the serialization funnel. A resolver that does not cover the document
/// type is the shape a source-generated context produces when a <c>[JsonSerializable]</c> is
/// missing: <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> answers
/// <see cref="NotSupportedException"/>, which the read paths used to let through raw.
/// </summary>
[Trait("Category", "Unit")]
public sealed class JsonHelperTests
{
    private sealed class Doc
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CollidingDoc
    {
        [JsonPropertyName("x")] public string One { get; set; } = string.Empty;
        [JsonPropertyName("x")] public string Two { get; set; } = string.Empty;
    }

    private sealed class ThrowingResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException("resolver failed");
    }

    private static JsonSerializerOptions Covering() =>
        new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static JsonSerializerOptions Uncovering() =>
        new() { TypeInfoResolver = JsonTypeInfoResolver.Combine() };

    [Fact]
    public void SerializeToUtf8Bytes_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        var options = Uncovering();

        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.SerializeToUtf8Bytes(new Doc(), options));

        Assert.IsType<NotSupportedException>(ex.InnerException);
        Assert.Contains("JsonTypeInfo metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeString_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        var options = Uncovering();

        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<Doc>("""{"Name":"n"}""", options));

        Assert.IsType<NotSupportedException>(ex.InnerException);
        Assert.Contains("JsonTypeInfo metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeUtf8_WithAResolverThatDoesNotCoverTheType_ThrowsDocumentSerialization()
    {
        var options = Uncovering();

        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<Doc>("""{"Name":"n"}"""u8.ToArray().AsSpan(), options));

        Assert.IsType<NotSupportedException>(ex.InnerException);
        Assert.Contains("JsonTypeInfo metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_WithMalformedJson_StillReportsTheJsonFailure()
    {
        // The two catches are separate contracts: malformed JSON is not a metadata problem.
        var options = Covering();

        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<Doc>("{ not json", options));

        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void RoundTrip_WithACoveringResolver_Succeeds()
    {
        var options = Covering();

        var bytes = JsonHelper.SerializeToUtf8Bytes(new Doc { Name = "n" }, options);

        Assert.Equal("n", JsonHelper.Deserialize<Doc>(bytes.AsSpan(), options)?.Name);
        Assert.Equal("n", JsonHelper.Deserialize<Doc>("""{"Name":"n"}""", options)?.Name);
    }

    [Fact]
    public void SerializeToUtf8Bytes_WithCollidingJsonPropertyNames_ThrowsDocumentSerialization()
    {
        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.SerializeToUtf8Bytes(new CollidingDoc(), Covering()));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("type metadata is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeString_WithCollidingJsonPropertyNames_ThrowsDocumentSerialization()
    {
        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<CollidingDoc>("""{"x":"n"}""", Covering()));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("type metadata is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeUtf8_WithCollidingJsonPropertyNames_ThrowsDocumentSerialization()
    {
        var ex = Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<CollidingDoc>("""{"x":"n"}"""u8.ToArray().AsSpan(), Covering()));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("type metadata is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeMethods_WithAResolverThatThrows_ThrowDocumentSerialization()
    {
        // A second, resolver-side cause of the same InvalidOperationException.
        var options = new JsonSerializerOptions { TypeInfoResolver = new ThrowingResolver() };

        Assert.IsType<InvalidOperationException>(Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.SerializeToUtf8Bytes(new Doc(), options)).InnerException);
        Assert.IsType<InvalidOperationException>(Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<Doc>("""{"Name":"n"}""", options)).InnerException);
        Assert.IsType<InvalidOperationException>(Assert.Throws<DocumentSerializationException>(
            () => JsonHelper.Deserialize<Doc>("""{"Name":"n"}"""u8.ToArray().AsSpan(), options)).InnerException);
    }
}
