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

    // The member rule the resolver re-checks against is one or more characters, none of which is an
    // apostrophe, a '.' or a '['. A kebab-cased name is the mundane case and used to be refused:
    // JsonNamingPolicy.KebabCaseLower turns "FullName" into "full-name", so a store on the BCL's own
    // policy could build no index, query or patch path at all.

    [Fact]
    public void Resolve_WithAKebabCasedSerializedName_ResolvesIt()
    {
        Assert.Equal("$.full-name", Resolve<Customer>(x => x.Display, Reflection()));
        Assert.Equal("$.full-name", Resolve<Person>(x => x.FullName, KebabCase()));
    }

    [Fact]
    public void Resolve_WithASerializedNameCarryingAStructuralCharacter_ThrowsNamingTheMember()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Dotted, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("a.b", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Dotted", exception.Message, StringComparison.Ordinal);
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
    // permanently — quoting cannot rescue it, so it is not deferred to C18 Tier 2.
    [Fact]
    public void Resolve_WithASerializedNameCarryingANul_ThrowsNamingTheMember()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Nul, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("Nul", exception.Message, StringComparison.Ordinal);
        Assert.Contains("U+0000", exception.Message, StringComparison.Ordinal);
    }

    // The recovery advice has to split by reason, because only one of these is this library's own
    // limitation. A '.' or a '[' needs the $."quoted" rendering that is deferred to C18 Tier 2, and
    // an apostrophe only breaks the interpolated literal - measured, a bound '$."a.b"' and '$.a''b'
    // each read their own key, so ExecuteRawAsync really does reach them. A U+0000 member is
    // reachable by nothing, so sending the caller to raw SQL would be false advice.
    [Fact]
    public void Resolve_WithANulName_DoesNotPointTheCallerAtRawSql()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Nul, Reflection()));

        Assert.Contains("No JSON path can address it", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteRawAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithADottedName_KeepsTheRawSqlPointer()
    {
        AssertKeepsTheRawSqlPointer(Assert.Throws<ArgumentException>(
            () => Resolve<Customer>(x => x.Dotted, Reflection())));
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
}
