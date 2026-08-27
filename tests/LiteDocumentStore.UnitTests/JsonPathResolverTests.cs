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

    [Fact]
    public void Resolve_WithASerializedNameThePathGrammarCannotExpress_ThrowsNamingTheMember()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve<Customer>(x => x.Display, Reflection()));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("full-name", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Display", exception.Message, StringComparison.Ordinal);
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
