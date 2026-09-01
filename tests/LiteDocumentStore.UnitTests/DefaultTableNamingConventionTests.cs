using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The on-disk table name every document operation is built from. The default folds the type's
/// namespace-qualified name, so two types with the same simple name no longer share one table —
/// which was silent cross-type overwriting rather than an error.
/// </summary>
/// <remarks>
/// The fold is collision-resistant, not injective, and deliberately so. The one collision family
/// reachable from ordinary C# is pinned below as documentation of that choice; a store's collision
/// guard is what turns it into a loud failure. See <see cref="DefaultTableNamingConvention"/>.
/// </remarks>
[Trait("Category", "Unit")]
public class DefaultTableNamingConventionTests
{
    private const string Self = "LiteDocumentStore_UnitTests_DefaultTableNamingConventionTests";

    private static readonly DefaultTableNamingConvention Convention = DefaultTableNamingConvention.Instance;

    private sealed record Nested(string Name);

    private sealed record Box<T>(T Value);

    private sealed record Pair<TFirst, TSecond>(TFirst First, TSecond Second);

    private sealed record Documentação(string Name);

    // --- Rendering -------------------------------------------------------------------------------

    [Fact]
    public void GetTableName_ForATypeInTheGlobalNamespace_KeepsTheBareName() =>
        Assert.Equal("GlobalNamingProbeDoc", Convention.GetTableName<GlobalNamingProbeDoc>());

    [Fact]
    public void GetTableName_ForANamespacedType_FoldsTheNamespace() =>
        Assert.Equal(
            "LiteDocumentStore_UnitTests_NamingProbeA_Customer",
            Convention.GetTableName<NamingProbeA.Customer>());

    [Fact]
    public void GetTableName_ForTwoTypesWithTheSameSimpleName_DiffersByNamespace() =>
        Assert.NotEqual(
            Convention.GetTableName<NamingProbeA.Customer>(),
            Convention.GetTableName<NamingProbeB.Customer>());

    [Fact]
    public void GetTableName_ForANestedType_FoldsTheDeclaringChain() =>
        Assert.Equal($"{Self}_Nested", Convention.GetTableName<Nested>());

    [Fact]
    public void GetTableName_ForAConstructedGeneric_AppendsTheArityThenEachArgument() =>
        Assert.Equal($"{Self}_Box_1_System_Int32", Convention.GetTableName<Box<int>>());

    [Fact]
    public void GetTableName_ForATwoArgumentGeneric_RendersBothArguments() =>
        Assert.Equal(
            $"{Self}_Pair_2_System_Int32_System_String",
            Convention.GetTableName<Pair<int, string>>());

    [Fact]
    public void GetTableName_ForANestedGenericArgument_RecursesThroughIt() =>
        Assert.Equal(
            $"{Self}_Box_1_{Self}_Box_1_System_Int32",
            Convention.GetTableName<Box<Box<int>>>());

    [Fact]
    public void GetTableName_ForTheSameNameAtTwoArities_Differs() =>
        Assert.NotEqual(
            Convention.GetTableName<Box<int>>(),
            Convention.GetTableName<Pair<int, int>>());

    [Fact]
    public void GetTableName_ForAGenericArgumentInAnotherNamespace_KeepsThemApart() =>
        Assert.NotEqual(
            Convention.GetTableName<Box<NamingProbeA.Customer>>(),
            Convention.GetTableName<Box<NamingProbeB.Customer>>());

    [Fact]
    public void GetTableName_ThroughTheGenericAndTypeOverloads_Agree()
    {
        Type nested = typeof(Nested);
        Assert.Equal(Convention.GetTableName<Nested>(), Convention.GetTableName(nested));
    }

    // --- The deliberately accepted collision -----------------------------------------------------

    /// <summary>
    /// Namespace <c>Naming_</c> with type <c>B</c> and namespace <c>Naming</c> with type <c>_B</c>
    /// fold to one name: the escape character would have to be the separator character, and no
    /// separator run-length disambiguates <c>2a + s + 2b</c> underscores.
    /// </summary>
    /// <remarks>
    /// Accepted rather than fixed: separating them needs a self-delimiting encoding — length-prefixed
    /// segments or reserved markers — whose output is not a name anyone would type into a SQL client,
    /// and readable tables are the point of a store that stays open to raw SQL. The store's collision
    /// guard makes this a throw instead; <c>TableNameCollisionGuardTests</c> pins that.
    /// </remarks>
    [Fact]
    public void GetTableName_WhenAnUnderscoreMeetsASeparator_CollidesByDesign() =>
        Assert.Equal(
            Convention.GetTableName<Naming_.B>(),
            Convention.GetTableName<Naming._B>());

    /// <summary>
    /// The arity says how many arguments follow, not where each one ends, so a namespaced argument's
    /// segments are indistinguishable from the boundary between arguments.
    /// </summary>
    /// <remarks>
    /// The collision this admits — <c>Pair&lt;N.X, Y&gt;</c> against <c>Pair&lt;N, X.Y&gt;</c> — is
    /// **not constructible from ordinary C#**: it needs one dotted name to be both a namespace and a
    /// type, which is CS0101 inside an assembly and CS0434 across two referenced ones. Reaching it
    /// takes <c>extern alias</c> or a reflectively obtained type. This test pins the rendering that
    /// admits it rather than the pair itself, so the reasoning survives in the suite.
    /// </remarks>
    [Fact]
    public void GetTableName_RendersGenericArgumentsWithoutDelimitingThem() =>
        Assert.Equal(
            $"{Self}_Pair_2_LiteDocumentStore_UnitTests_NamingProbeA_Customer_" +
            "LiteDocumentStore_UnitTests_NamingProbeB_Customer",
            Convention.GetTableName<Pair<NamingProbeA.Customer, NamingProbeB.Customer>>());

    // --- Rejections ------------------------------------------------------------------------------

    [Fact]
    public void GetTableName_WithANullType_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Convention.GetTableName(null!));

    [Fact]
    public void GetTableName_ForAnOpenGenericDefinition_Throws()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Convention.GetTableName(typeof(Box<>)));
        Assert.Contains("open generic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForAGenericParameter_Throws()
    {
        var parameter = typeof(Box<>).GetGenericArguments()[0];
        var exception = Assert.Throws<NotSupportedException>(() => Convention.GetTableName(parameter));
        Assert.Contains("generic parameter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForAnArray_Throws()
    {
        Type array = typeof(int[]);
        var exception = Assert.Throws<NotSupportedException>(() => Convention.GetTableName(array));
        Assert.Contains("array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForAPointer_Throws()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Convention.GetTableName(typeof(int).MakePointerType()));
        Assert.Contains("pointer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForAByRefType_Throws()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Convention.GetTableName(typeof(int).MakeByRefType()));
        Assert.Contains("by-ref", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForATypeNestedInAGeneric_Throws()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Convention.GetTableName<NamingProbeNesting.Outer<int>.Inner>());
        Assert.Contains("nested inside the generic type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTableName_ForANonAsciiName_Throws()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Convention.GetTableName<Documentação>());
        Assert.Contains("ASCII SQL identifier", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection reached through a generic argument names the type the call site passed as well as
    /// the offending one — the mis-attribution the early throw exists to avoid.
    /// </summary>
    [Fact]
    public void GetTableName_WhenAGenericArgumentIsUnsupported_NamesTheRequestedTypeToo()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Convention.GetTableName<Box<int[]>>());
        Assert.Contains("Box", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Int32[]", exception.Message, StringComparison.Ordinal);
    }
}
