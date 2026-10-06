using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="ValueBinder"/>: a bound value takes the shape the serializer writes
/// at its path, and an enum the metadata cannot place is refused rather than guessed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ValueBinderTests
{
    private static JsonSerializerOptions Options(Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        configure?.Invoke(options);
        return options;
    }

    private static readonly JsonSerializerOptions StringEnums =
        Options(o => o.Converters.Add(new JsonStringEnumConverter()));

    private static object Bind<TRoot>(
        string path,
        object value,
        JsonSerializerOptions options,
        bool element = false,
        bool ranged = false) =>
        ValueBinder.BindQueryValue(
            typeof(TRoot), path, element, value, DocumentQuery<TRoot>.NormalizeBoundValue(value), ranged, options, "query");

    [Fact]
    public void BindQueryValue_WithAStringEnumConverterInTheOptions_BindsTheName()
    {
        Assert.Equal("Active", Bind<BinderOrder>("$.Status", BinderStatus.Active, StringEnums));
    }

    [Fact]
    public void BindQueryValue_WithDefaultOptions_BindsTheEnumAsItsNumber()
    {
        Assert.Equal(1L, Bind<BinderOrder>("$.Status", BinderStatus.Active, Options()));
    }

    [Fact]
    public void BindQueryValue_WithAConverterOnTheProperty_BindsThroughIt()
    {
        // Default options: only the property's own [JsonConverter] says the enum is a name.
        Assert.Equal("Active", Bind<BinderAttributedOrder>("$.Status", BinderStatus.Active, Options()));
    }

    [Fact]
    public void BindQueryValue_WithAConverterOnTheEnumType_BindsThroughIt()
    {
        Assert.Equal("Low", Bind<BinderTicket>("$.Priority", BinderPriority.Low, Options()));
    }

    [Fact]
    public void BindQueryValue_OnANullableEnumMember_BindsTheName()
    {
        Assert.Equal("Closed", Bind<BinderOrder>("$.Previous", BinderStatus.Closed, StringEnums));
    }

    [Fact]
    public void BindQueryValue_WithAPropertyConverterOnANullableMember_BindsThroughIt()
    {
        Assert.Equal("Closed", Bind<BinderAttributedOrder>("$.Previous", BinderStatus.Closed, Options()));
    }

    [Fact]
    public void BindQueryValue_WithACustomDateConverter_BindsWhatTheConverterWrites()
    {
        var at = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            Bind<BinderStamped>("$.At", at, Options()));
    }

    [Fact]
    public void BindQueryValue_WithNumbersWrittenAsStrings_BindsTheText()
    {
        var options = Options(o => o.NumberHandling = JsonNumberHandling.WriteAsString);

        Assert.Equal("5", Bind<BinderOrder>("$.Quantity", 5, options));
    }

    [Fact]
    public void BindQueryValue_UnderANamingPolicy_ResolvesTheSerializedName()
    {
        var options = Options(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.Converters.Add(new JsonStringEnumConverter());
        });

        Assert.Equal("Active", Bind<BinderOrder>("$.status", BinderStatus.Active, options));
    }

    [Theory]
    [InlineData("$.Status")]   // the CLR name, which a camelCase document does not carry
    [InlineData("$.Missing")]
    [InlineData("$.Extra.Status")]
    public void BindQueryValue_WithAnEnumOnAnUnresolvablePath_Throws(string path)
    {
        var options = Options(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var exception = Assert.Throws<ArgumentException>(
            () => Bind<BinderOrder>(path, BinderStatus.Active, options));

        Assert.Equal("query", exception.ParamName);
        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void BindQueryValue_WithAPrimitiveOnAnUnresolvablePath_FallsBackToTheNormalization()
    {
        var at = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal("2024-03-01T00:00:00", Bind<BinderOrder>("$.Missing", at, Options()));
        Assert.Equal(12.5d, Bind<BinderOrder>("$.Missing", 12.5m, Options()));
    }

    [Fact]
    public void BindQueryValue_WithAValueOfAnotherType_FallsBackToTheNormalization()
    {
        // An int compared with a long member keeps the builder's binding, as it always has.
        Assert.Equal(7, Bind<BinderOrder>("$.Total", 7, Options()));
    }

    [Fact]
    public void BindQueryValue_RangingOverAnEnumStoredAsItsName_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Bind<BinderOrder>("$.Status", BinderStatus.Active, StringEnums, ranged: true));

        Assert.Equal("query", exception.ParamName);
    }

    [Fact]
    public void BindQueryValue_RangingOverAnEnumStoredAsItsNumber_Binds()
    {
        Assert.Equal(1L, Bind<BinderOrder>("$.Status", BinderStatus.Active, Options(), ranged: true));
    }

    [Theory]
    [InlineData("$.Tags")]
    [InlineData("$.History")]
    public void BindQueryValue_ForArrayElements_BindsTheElementShape(string path)
    {
        Assert.Equal("Active", Bind<BinderOrder>(path, BinderStatus.Active, StringEnums, element: true));
    }

    [Fact]
    public void BindQueryValue_WhenTheConverterWritesAnObject_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Bind<BinderBoxed>("$.Status", BinderStatus.Active, Options()));

        Assert.Equal("query", exception.ParamName);
        Assert.Contains("object", exception.Message);
    }

    [Fact]
    public void BindQueryValue_WhenTheConverterThrows_ThrowsDocumentSerializationException()
    {
        Assert.Throws<DocumentSerializationException>(
            () => Bind<BinderFaulty>("$.Status", BinderStatus.Active, Options()));
    }

    [Theory]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    public void BindQueryValue_WithABool_BindsWhatJsonExtractYields(bool value, long expected)
    {
        Assert.Equal(expected, Bind<BinderOrder>("$.Paid", value, Options()));
    }

    [Fact]
    public void BindQueryValue_WithAStringTheSerializerEscapes_BindsTheUnescapedText()
    {
        Assert.Equal("<a & 'b'>", Bind<BinderOrder>("$.Note", "<a & 'b'>", Options()));
    }

    [Fact]
    public void BindQueryValue_WithASourceGeneratedContext_BindsTheName()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = BinderJsonContext.Default };

        Assert.Equal("Active", Bind<BinderOrder>("$.Status", BinderStatus.Active, options));
        Assert.Equal("Closed", Bind<BinderAttributedOrder>("$.Status", BinderStatus.Closed, options));
    }

    [Fact]
    public void BindPatchValue_OnAResolvedPath_BindsAStringAsItself()
    {
        var operation = Assert.Single(DocumentPatch<BinderOrder>.Set("$.Status", BinderStatus.Active).Operations);

        var bound = ValueBinder.BindPatchValue(typeof(BinderOrder), operation, StringEnums, "patch");

        Assert.Equal("Active", bound.Value);
        Assert.False(bound.AsJson);
    }

    [Fact]
    public void BindPatchValue_OnAResolvedPath_BindsAnIntegerAsItself()
    {
        var operation = Assert.Single(DocumentPatch<BinderOrder>.Set("$.Status", BinderStatus.Active).Operations);

        var bound = ValueBinder.BindPatchValue(typeof(BinderOrder), operation, Options(), "patch");

        Assert.Equal(1L, bound.Value);
        Assert.False(bound.AsJson);
    }

    [Theory]
    [InlineData("$.Paid", true, "true", true)]   // SQLite has no boolean: only json(...) writes true
    [InlineData("$.Quantity", 5, "5", false)]    // a quoted number is a JSON string, bound as itself
    public void BindPatchValue_BindsAsItselfOnlyWhatLandsUnchanged(
        string path, object value, string expectedValue, bool expectedAsJson)
    {
        var options = Options(o => o.NumberHandling = JsonNumberHandling.WriteAsString);
        var operation = Assert.Single(DocumentPatch<BinderOrder>.Set(path, value).Operations);

        var bound = ValueBinder.BindPatchValue(typeof(BinderOrder), operation, options, "patch");

        Assert.Equal(expectedValue, bound.Value);
        Assert.Equal(expectedAsJson, bound.AsJson);
    }

    [Fact]
    public void BindPatchValue_WithADecimal_KeepsItsExactTextAsJson()
    {
        var operation = Assert.Single(DocumentPatch<BinderPriced>.Set("$.Price", 12.50m).Operations);

        var bound = ValueBinder.BindPatchValue(typeof(BinderPriced), operation, Options(), "patch");

        Assert.Equal("12.50", bound.Value);
        Assert.True(bound.AsJson);
    }

    [Fact]
    public void BindQueryValue_WithAValueLargerThanTheRetainedBuffer_BindsItAndStaysUsable()
    {
        var large = new string('x', 10_000);

        Assert.Equal(large, Bind<BinderOrder>("$.Note", large, Options()));
        Assert.Equal("small", Bind<BinderOrder>("$.Note", "small", Options()));
    }

    [Fact]
    public void BindPatchValue_WithAnEnumOnAnUnresolvablePath_Throws()
    {
        var operation = Assert.Single(DocumentPatch<BinderOrder>.Set("$.Missing", BinderStatus.Active).Operations);

        var exception = Assert.Throws<ArgumentException>(
            () => ValueBinder.BindPatchValue(typeof(BinderOrder), operation, StringEnums, "patch"));

        Assert.Equal("patch", exception.ParamName);
    }

    [Fact]
    public void BindPatchValue_LeavesARemoveAndANullSetUnchanged()
    {
        var operations = DocumentPatch<BinderOrder>.Remove("$.Note").AndSet("$.Status", null).Operations;

        Assert.All(operations, operation =>
            Assert.Same(operation, ValueBinder.BindPatchValue(typeof(BinderOrder), operation, StringEnums, "patch")));
    }

    [Fact]
    public void BindPatchValue_OnAnUnresolvablePrimitive_KeepsTheBuilderNormalization()
    {
        var operation = Assert.Single(DocumentPatch<BinderOrder>.Set("$.Missing", 10.05m).Operations);

        Assert.Same(operation, ValueBinder.BindPatchValue(typeof(BinderOrder), operation, StringEnums, "patch"));
    }

    [Fact]
    public void DocumentQuery_AcceptsAnEnumValue_AndKeepsItRawForExecution()
    {
        var predicate = Assert.Single(
            DocumentQuery<BinderOrder>.Where("$.Status", QueryOperator.Equal, BinderStatus.Active).Predicates);

        Assert.Equal(BinderStatus.Active, predicate.RawValue);
    }

    [Fact]
    public void DocumentQuery_In_KeepsTheRawValuesForExecution()
    {
        var predicate = Assert.Single(
            DocumentQuery<BinderOrder>.WhereIn("$.Status", [BinderStatus.Active, BinderStatus.Closed]).Predicates);

        Assert.Equal([BinderStatus.Active, BinderStatus.Closed], predicate.RawValues!);
    }
}

public enum BinderStatus { Pending, Active, Closed }

[JsonConverter(typeof(JsonStringEnumConverter<BinderPriority>))]
public enum BinderPriority { Low, High }

public sealed class BinderOrder
{
    public BinderStatus Status { get; set; }
    public BinderStatus? Previous { get; set; }
    public int Quantity { get; set; }
    public long Total { get; set; }
    public bool Paid { get; set; }
    public string Note { get; set; } = "";
    public BinderStatus[] Tags { get; set; } = [];
    public List<BinderStatus> History { get; set; } = [];
    public Dictionary<string, object> Extra { get; set; } = [];
}

public sealed class BinderAttributedOrder
{
    [JsonConverter(typeof(JsonStringEnumConverter<BinderStatus>))]
    public BinderStatus Status { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<BinderStatus>))]
    public BinderStatus? Previous { get; set; }
}

public sealed class BinderPriced
{
    public decimal Price { get; set; }
}

public sealed class BinderTicket
{
    public BinderPriority Priority { get; set; }
}

public sealed class BinderStamped
{
    [JsonConverter(typeof(BinderEpochMillisConverter))]
    public DateTime At { get; set; }
}

public sealed class BinderBoxed
{
    [JsonConverter(typeof(BinderObjectWritingConverter))]
    public BinderStatus Status { get; set; }
}

public sealed class BinderFaulty
{
    [JsonConverter(typeof(BinderThrowingConverter))]
    public BinderStatus Status { get; set; }
}

public sealed class BinderEpochMillisConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64()).UtcDateTime;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds());
}

public sealed class BinderObjectWritingConverter : JsonConverter<BinderStatus>
{
    public override BinderStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, BinderStatus value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", (int)value);
        writer.WriteEndObject();
    }
}

public sealed class BinderThrowingConverter : JsonConverter<BinderStatus>
{
    public override BinderStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, BinderStatus value, JsonSerializerOptions options) =>
        throw new JsonException("refused");
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(BinderOrder))]
[JsonSerializable(typeof(BinderAttributedOrder))]
internal sealed partial class BinderJsonContext : JsonSerializerContext;
