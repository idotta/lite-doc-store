using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The path a property-access expression resolves to, which every index and virtual-column
/// statement is built from. It must name the key the store's own serializer writes: an index over
/// a path no document carries is NULL in every row, and SQLite treats each NULL in a unique index
/// as distinct, so a declared UNIQUE constraint would silently enforce nothing.
/// </summary>
[Trait("Category", "Unit")]
public class JsonPathResolverTests
{
    private sealed class Address
    {
        public string City { get; set; } = "";
    }

    private sealed class Customer
    {
        [JsonPropertyName("email_address")]
        public string Email { get; set; } = "";

        public string Name { get; set; } = "";

        public int Age { get; set; }

        public Address Home { get; set; } = new();

        [JsonIgnore]
        public string Secret { get; set; } = "";

        [JsonPropertyName("full-name")]
        public string Display { get; set; } = "";

        // The two shapes the widened grammar still cannot express: '.' and '[' are structural in a
        // path, and an apostrophe would close the SQL literal the path is written into.
        [JsonPropertyName("a.b")]
        public string Dotted { get; set; } = "";

        [JsonPropertyName("a'b")]
        public string Quoted { get; set; } = "";

        // Not a Tier 2 shape: a NUL truncates the SQL statement and no quoting form can address it.
        [JsonPropertyName("nul\0name")]
        public string Nul { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, object>? Extra { get; set; }
    }

    private class Base
    {
        public string Name { get; set; } = "";
    }

    private sealed class Derived : Base
    {
        public int Age { get; set; }
    }

    private sealed class Unregistered
    {
        public string Value { get; set; } = "";
    }

    private static JsonSerializerOptions Reflection() =>
        new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private sealed class Person
    {
        public string FullName { get; set; } = "";
    }

    private static JsonSerializerOptions KebabCase() =>
        new()
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower
        };

    private static JsonSerializerOptions CamelCase() =>
        new()
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private static string Resolve<T>(Expression<Func<T, object>> expression, JsonSerializerOptions options) =>
        JsonPathResolver.Resolve(expression, options, "jsonPath");

    [Fact]
    public void Resolve_WithDefaultSerialization_KeepsTheClrMemberName()
    {
        Assert.Equal("$.Name", Resolve<Customer>(x => x.Name, Reflection()));
    }

    [Fact]
    public void Resolve_WithAJsonPropertyName_NamesTheSerializedKey()
    {
        Assert.Equal("$.email_address", Resolve<Customer>(x => x.Email, Reflection()));
    }

    [Fact]
    public void Resolve_WithANamingPolicy_NamesTheSerializedKey()
    {
        Assert.Equal("$.name", Resolve<Customer>(x => x.Name, CamelCase()));
    }

    [Fact]
    public void Resolve_WithAJsonPropertyName_WinsOverTheNamingPolicy()
    {
        // The attribute is the name STJ writes; the policy does not re-case it.
        Assert.Equal("$.email_address", Resolve<Customer>(x => x.Email, CamelCase()));
    }

    [Fact]
    public void Resolve_WithABoxedValueType_UnwrapsTheConvert()
    {
        Assert.Equal("$.age", Resolve<Customer>(x => x.Age, CamelCase()));
    }

    [Fact]
    public void Resolve_WithANestedPath_ResolvesEverySegment()
    {
        Assert.Equal("$.home.city", Resolve<Customer>(x => x.Home.City, CamelCase()));
    }

    [Fact]
    public void Resolve_MatchesWhatTheSerializerActuallyWrites()
    {
        var options = CamelCase();
        var json = JsonSerializer.Serialize(
            new Customer { Email = "a@b", Name = "n", Home = new Address { City = "Boston" } },
            options.GetTypeInfo(typeof(Customer)));

        using var document = JsonDocument.Parse(json);

        // The resolved segments are the keys present in the document, which is the whole contract.
        Assert.True(document.RootElement.TryGetProperty(
            Resolve<Customer>(x => x.Email, options)[2..],
            out _));
        Assert.True(document.RootElement.TryGetProperty(
            Resolve<Customer>(x => x.Name, options)[2..],
            out _));
    }

    [Fact]
    public void Resolve_WithAnIgnoredMember_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Secret, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("not serialized", exception.Message, StringComparison.Ordinal);
    }

    // The member rule the resolver re-checks against refuses only an apostrophe and U+0000; every
    // other serialized name is rendered, quoted when it has to be. A kebab-cased name is the mundane
    // case and used to be refused: JsonNamingPolicy.KebabCaseLower turns "FullName" into
    // "full-name", so a store on the BCL's own policy could build no index, query or patch path at
    // all.

    [Fact]
    public void Resolve_WithAKebabCasedSerializedName_ResolvesIt()
    {
        Assert.Equal("$.full-name", Resolve<Customer>(x => x.Display, Reflection()));
        Assert.Equal("$.full-name", Resolve<Person>(x => x.FullName, KebabCase()));
    }

    // A [JsonPropertyName("a.b")] is how a consumer reaches Tier 2 without writing a path at all,
    // and the rendering it gets must be the one SqlGenerator would produce for the same key written
    // as a string — otherwise the index and the query name different keys.
    [Fact]
    public void Resolve_WithASerializedNameCarryingAStructuralCharacter_RendersTheQuotedForm()
    {
        Assert.Equal("$.\"a.b\"", Resolve<Customer>(x => x.Dotted, Reflection()));
        Assert.Equal(
            "$.\"a.b\"",
            SqlGenerator.ValidateJsonPath(Resolve<Customer>(x => x.Dotted, Reflection()), "jsonPath"));
    }

    // The injection boundary, reached through the expression overload rather than a string path.
    [Fact]
    public void Resolve_WithASerializedNameCarryingAnApostrophe_ThrowsNamingTheMember()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Quoted, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("a'b", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Quoted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("apostrophe", exception.Message, StringComparison.Ordinal);
    }

    // U+0000 terminates the SQL string sqlite3_prepare reads, truncating the whole statement. The
    // old identifier-shaped member rule rejected it; the widened rule must keep rejecting it, and
    // permanently — quoting cannot rescue it either, so no spelling of a path reaches such a key.
    [Fact]
    public void Resolve_WithASerializedNameCarryingANul_ThrowsNamingTheMember()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Nul, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("Nul", exception.Message, StringComparison.Ordinal);
        Assert.Contains("U+0000", exception.Message, StringComparison.Ordinal);
    }

    // The recovery advice has to split by reason, because only one of these is reachable by nothing.
    // An apostrophe only breaks the interpolated literal - measured, a bound '$.a''b' reads its own
    // key, so ExecuteRawAsync really does reach it, and so does a member that needs the $."quoted"
    // form while also carrying a '"', which is refused for what the declared minimum engine does
    // with the quotes rather than for the character. A U+0000 member is reachable by nothing, so
    // sending the caller to raw SQL would be false advice. A '.' or a '[' is no longer a fault at
    // all: AppendCanonicalMember writes such a member quoted.
    [Fact]
    public void Resolve_WithANulName_DoesNotPointTheCallerAtRawSql()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Nul, Reflection()));

        Assert.Contains("No JSON path can address it", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteRawAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithAnApostropheName_KeepsTheRawSqlPointer()
    {
        AssertKeepsTheRawSqlPointer(Assert.Throws<ArgumentException>(
            () => Resolve<Customer>(x => x.Quoted, Reflection())));
    }

    private static void AssertKeepsTheRawSqlPointer(ArgumentException exception)
    {
        Assert.Contains("Index it through ExecuteRawAsync", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No JSON path can address it", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithNoMetadataForTheType_ThrowsNamingTheType()
    {
        // A resolver that knows nothing is the shape an AOT context takes for an unregistered type.
        var options = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };

        var exception = Assert.Throws<ArgumentException>(() => Resolve<Unregistered>(x => x.Value, options));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("Unregistered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithSomethingOtherThanAPropertyAccess_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Resolve<Customer>(x => x.Name.Length.ToString(), Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    /// <summary>
    /// Extension data has a getter and a metadata name, so the serialized check alone lets it
    /// through — but its entries are written into the containing object, never under the member's
    /// own name, so an index over that name is the vacuous one this whole file exists to prevent.
    /// </summary>
    [Fact]
    public void Resolve_WithAnExtensionDataMember_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Extra!, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("JsonExtensionData", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithAnExtensionDataMember_DoesNotNameAKeyTheDocumentCarries()
    {
        var options = Reflection();
        var json = JsonSerializer.Serialize(
            new Customer { Name = "n", Extra = new Dictionary<string, object> { ["k"] = "v" } },
            options.GetTypeInfo(typeof(Customer)));

        // The premise of the rejection: the entries land beside Name, not under Extra.
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("Extra", out _));
        Assert.True(document.RootElement.TryGetProperty("k", out _));
    }

    [Theory]
    [InlineData("a captured local")]
    [InlineData("a static")]
    public void Resolve_WithAChainNotRootedAtTheParameter_ThrowsNamingTheParameter(string shape)
    {
        var captured = new Customer();
        Expression<Func<Customer, object>> expression = shape == "a captured local"
            ? x => captured.Name
            : x => Shared.Name;

        var exception = Assert.Throws<ArgumentException>(() => Resolve(expression, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);

        // Without the root check this reports the compiler-generated closure class instead.
        Assert.Contains("rooted at the lambda parameter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithACastToABaseType_StillResolves()
    {
        // The Convert the cast inserts sits mid-chain, where only the root check would see it.
        Assert.Equal("$.Name", Resolve<Derived>(x => ((Base)x).Name, Reflection()));
    }

    private static readonly Customer Shared = new();

    // --- Path -> declared type -------------------------------------------------------------

    private sealed class Clock
    {
        public DateTime At { get; set; }

        public DateTimeOffset? Seen { get; set; }

        public Clock? Inner { get; set; }

        public List<DateTime> History { get; set; } = [];

        public Dictionary<string, DateTime> ByName { get; set; } = [];

        [JsonPropertyName("when")]
        public DateTime Renamed { get; set; }

        public string Label { get; set; } = "";
    }

    [Theory]
    [InlineData("$", typeof(Clock))]
    [InlineData("$.At", typeof(DateTime))]
    [InlineData("$.Seen", typeof(DateTimeOffset?))]
    [InlineData("$.Inner.At", typeof(DateTime))]
    [InlineData("$.Inner.Inner.Seen", typeof(DateTimeOffset?))]
    [InlineData("$.History[2]", typeof(DateTime))]
    [InlineData("$.ByName.first", typeof(DateTime))]
    [InlineData("$.when", typeof(DateTime))]
    [InlineData("$.Label", typeof(string))]
    public void ResolvePathType_WithAPathTheMetadataDescribes_ReturnsTheDeclaredType(string path, Type expected)
    {
        Assert.Equal(expected, JsonPathResolver.ResolvePathType(typeof(Clock), path, Reflection()));
    }

    [Theory]
    // The CLR name, where the serializer writes "when".
    [InlineData("$.Renamed")]
    [InlineData("$.Missing")]
    [InlineData("$.Label.Length")]
    [InlineData("$.At[0]")]
    [InlineData("$.History.Count")]
    [InlineData("$.Inner.Missing.At")]
    public void ResolvePathType_WithAPathTheMetadataDoesNotDescribe_ReturnsNull(string path)
    {
        Assert.Null(JsonPathResolver.ResolvePathType(typeof(Clock), path, Reflection()));
    }

    [Fact]
    public void ResolvePathType_UnderANamingPolicy_MatchesTheSerializedName()
    {
        Assert.Equal(typeof(DateTime), JsonPathResolver.ResolvePathType(typeof(Clock), "$.at", CamelCase()));
        Assert.Null(JsonPathResolver.ResolvePathType(typeof(Clock), "$.At", CamelCase()));
    }

    [Fact]
    public void ResolvePathType_WithAQuotedMember_MatchesTheUnquotedName()
    {
        Assert.Equal(typeof(DateTime), JsonPathResolver.ResolvePathType(typeof(Clock), "$.\"At\"", Reflection()));
    }

    private sealed class EpochMillisConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.UnixEpoch.AddMilliseconds(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((long)(value - DateTime.UnixEpoch).TotalMilliseconds);
    }

    private sealed class ConvertedClock
    {
        [JsonConverter(typeof(EpochMillisConverter))]
        public DateTime At { get; set; }

        public DateTime Plain { get; set; }
    }

    [Fact]
    public void ResolvePathType_WhenThePropertyCarriesAConverter_ReturnsNull()
    {
        // The converter writes a number; the declared DateTime says nothing about the stored shape.
        Assert.Null(JsonPathResolver.ResolvePathType(typeof(ConvertedClock), "$.At", Reflection()));
        Assert.Equal(typeof(DateTime), JsonPathResolver.ResolvePathType(typeof(ConvertedClock), "$.Plain", Reflection()));
    }

    [Theory]
    [InlineData("$.At")]
    [InlineData("$.Seen")]
    [InlineData("$.Inner.At")]
    [InlineData("$.History[0]")]
    [InlineData("$.ByName.first")]
    public void ResolvePathType_WhenTheOptionsCarryAConverterForTheLeafType_ReturnsNull(string path)
    {
        var options = Reflection();
        options.Converters.Add(new EpochMillisConverter());
        options.Converters.Add(new OffsetAsTextConverter());

        Assert.Null(JsonPathResolver.ResolvePathType(typeof(Clock), path, options));
        Assert.Equal(typeof(string), JsonPathResolver.ResolvePathType(typeof(Clock), "$.Label", options));
    }

    private sealed class OffsetAsTextConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ResolvePathType_WhenTheOptionsHaveNoMetadataForTheType_ReturnsNull()
    {
        var empty = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };

        Assert.Null(JsonPathResolver.ResolvePathType(typeof(Clock), "$.At", empty));
    }
}
