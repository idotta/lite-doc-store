using System.Text.Json;
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
}
