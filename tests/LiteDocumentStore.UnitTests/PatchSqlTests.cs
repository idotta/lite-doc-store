using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The <see cref="DocumentPatch{T}"/> builder's validation, and the SQL
/// <see cref="SqlGenerator"/> emits for it: the nesting of <c>jsonb_set</c> inside
/// <c>jsonb_remove</c>, the left-to-right <c>@p0..@pN</c> binding, the <c>json(...)</c> wrapper
/// for the types SQLite cannot store as themselves, and the validation that still happens at
/// generation time when the builder is bypassed.
/// </summary>
[Trait("Category", "Unit")]
public class PatchSqlTests
{
    private sealed record Person(string Name);

    private const string Table = "Person";

    private static readonly PatchOperation[] NoOperations = [];

    private static PatchOperation Set(string jsonPath, object? value, bool asJson = false) =>
        new(jsonPath, PatchOperationKind.Set, value, asJson);

    private static PatchOperation Remove(string jsonPath) =>
        new(jsonPath, PatchOperationKind.Remove, null, AsJson: false);

    private static GeneratedQuery Generate(params PatchOperation[] operations) =>
        SqlGenerator.GeneratePatchSql(Table, operations, versioned: false);

    // --- Statement shape ---------------------------------------------------------------

    [Fact]
    public void GeneratePatchSql_WithOneSet_UpdatesThroughJsonbSet()
    {
        var patch = Generate(Set("$.Email", "a@b.c"));

        Assert.Equal(
            "UPDATE [Person] SET data = jsonb_set(data, '$.Email', @p0), version = version + 1 " +
            "WHERE id = @Id RETURNING version",
            patch.Sql);
        Assert.Equal("a@b.c", Assert.Single(patch.ParameterValues));
    }

    [Fact]
    public void GeneratePatchSql_WithOneRemove_UpdatesThroughJsonbRemoveAndBindsNothing()
    {
        var patch = Generate(Remove("$.Nickname"));

        Assert.Equal(
            "UPDATE [Person] SET data = jsonb_remove(data, '$.Nickname'), version = version + 1 " +
            "WHERE id = @Id RETURNING version",
            patch.Sql);
        Assert.Empty(patch.ParameterValues);
    }

    [Fact]
    public void GeneratePatchSql_WithSetsAndRemoves_AppliesTheSetsInsideTheRemoves()
    {
        var patch = Generate(
            Set("$.Email", "a@b.c"),
            Remove("$.Nickname"),
            Set("$.Age", 42),
            Remove("$.Alias"));

        Assert.Equal(
            "UPDATE [Person] SET data = jsonb_remove(jsonb_set(data, '$.Email', @p0, '$.Age', @p1), " +
            "'$.Nickname', '$.Alias'), version = version + 1 WHERE id = @Id RETURNING version",
            patch.Sql);
        Assert.Equal<object?>(["a@b.c", 42], patch.ParameterValues);
    }

    [Fact]
    public void GeneratePatchSql_WhenVersioned_GuardsOnTheExpectedVersion()
    {
        var patch = SqlGenerator.GeneratePatchSql(Table, [Set("$.Email", "a@b.c")], versioned: true);

        Assert.Equal(
            "UPDATE [Person] SET data = jsonb_set(data, '$.Email', @p0), version = version + 1 " +
            "WHERE id = @Id AND version = @ExpectedVersion RETURNING version",
            patch.Sql);
    }

    // json_set / json_remove return text, which would de-binary the data column.
    [Fact]
    public void GeneratePatchSql_NeverUsesTheTextReturningJsonFunctions()
    {
        var patch = Generate(Set("$.Email", "a@b.c"), Remove("$.Nickname"));

        Assert.DoesNotContain("json_set", patch.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("json_remove", patch.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratePatchSql_WithAJsonValue_WrapsThatParameterOnly()
    {
        var patch = Generate(Set("$.Active", "true", asJson: true), Set("$.Name", "Ada"));

        Assert.Equal(
            "UPDATE [Person] SET data = jsonb_set(data, '$.Active', json(@p0), '$.Name', @p1), " +
            "version = version + 1 WHERE id = @Id RETURNING version",
            patch.Sql);
        Assert.Equal<object?>(["true", "Ada"], patch.ParameterValues);
    }

    // --- Generation-time validation ------------------------------------------------------

    [Fact]
    public void GeneratePatchSql_WithNoOperations_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.GeneratePatchSql(Table, NoOperations, versioned: false));

        Assert.Equal("operations", exception.ParamName);
    }

    [Fact]
    public void GeneratePatchSql_WithAnInjectedTableName_Throws() =>
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GeneratePatchSql("Person]; DROP TABLE Person; --", [Set("$.A", 1)], versioned: false));

    [Fact]
    public void GeneratePatchSql_WithAMalformedPath_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Generate(Set("$.A'); DROP TABLE Person; --", 1)));

        Assert.Equal("operations", exception.ParamName);
    }

    private static PatchOperation[] Sets(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Set($"$.F{i}", i))];

    private static PatchOperation[] Removes(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Remove($"$.F{i}"))];

    // SQLITE_MAX_FUNCTION_ARG, not SQLITE_MAX_VARIABLE_NUMBER, is the budget a patch runs into:
    // a set spends two function arguments but binds one parameter, and a remove binds none, so
    // the parameter cap is unreachable from here.
    [Fact]
    public void GeneratePatchSql_AtTheSetCap_StaysWithinSqlitesFunctionArgumentBudget()
    {
        var patch = Generate(Sets(SqlGenerator.MaxPatchSetOperations));

        Assert.Equal(SqlGenerator.MaxPatchSetOperations, patch.ParameterValues.Count);
        Assert.InRange(
            (SqlGenerator.MaxPatchSetOperations * 2) + 1, 0, SqlGenerator.MaxJsonFunctionArguments);
    }

    [Fact]
    public void GeneratePatchSql_OneSetBeyondTheCap_ThrowsRatherThanFailingInSqlite()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Generate(Sets(SqlGenerator.MaxPatchSetOperations + 1)));

        Assert.Equal("operations", exception.ParamName);
        Assert.Contains(
            SqlGenerator.MaxPatchSetOperations.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratePatchSql_AtTheRemoveCap_BindsNothingAndGenerates()
    {
        var patch = Generate(Removes(SqlGenerator.MaxPatchRemoveOperations));

        Assert.Empty(patch.ParameterValues);
    }

    // A remove binds no parameter, so the parameter cap never sees a remove-only patch at all.
    [Fact]
    public void GeneratePatchSql_OneRemoveBeyondTheCap_ThrowsRatherThanFailingInSqlite()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Generate(Removes(SqlGenerator.MaxPatchRemoveOperations + 1)));

        Assert.Equal("operations", exception.ParamName);
        Assert.Contains(
            SqlGenerator.MaxPatchRemoveOperations.ToString(), exception.Message, StringComparison.Ordinal);
    }

    // The two budgets are independent: the nested jsonb_set counts as one argument to
    // jsonb_remove, so a full set list does not eat into the remove list.
    [Fact]
    public void GeneratePatchSql_AtBothCapsTogether_Generates()
    {
        var patch = Generate([
            .. Sets(SqlGenerator.MaxPatchSetOperations),
            .. Removes(SqlGenerator.MaxPatchRemoveOperations).Select(o => Remove($"$.R{o.JsonPath[3..]}"))
        ]);

        Assert.Equal(SqlGenerator.MaxPatchSetOperations, patch.ParameterValues.Count);
    }

    // --- Builder -------------------------------------------------------------------------

    [Fact]
    public void Set_ThenAndRemove_KeepsBothOperationsInCallOrder()
    {
        var patch = DocumentPatch<Person>.Set("$.Email", "a@b.c").AndRemove("$.Nickname");

        Assert.Collection(
            patch.Operations,
            first => Assert.Equal(Set("$.Email", "a@b.c"), first),
            second => Assert.Equal(Remove("$.Nickname"), second));
    }

    [Fact]
    public void AndSet_LeavesTheOriginalPatchUntouched()
    {
        var original = DocumentPatch<Person>.Set("$.Email", "a@b.c");

        _ = original.AndSet("$.Name", "Ada");

        Assert.Single(original.Operations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$.A'); DROP TABLE Person; --")]
    public void Set_WithABadPath_ThrowsAtTheCallSite(string jsonPath) =>
        Assert.Throws<ArgumentException>(() => DocumentPatch<Person>.Set(jsonPath, 1));

    [Fact]
    public void Set_WithAnUnsupportedValueType_ThrowsAtTheCallSite()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentPatch<Person>.Set("$.Address", new Person("Ada")));

        Assert.Equal("value", exception.ParamName);
    }

    // System.Text.Json refuses to write these, so no stored document can hold one; NaN would
    // otherwise fail at ADO bind time and infinity would store SQLite's 9e999.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Set_WithANonFiniteDouble_ThrowsAtTheCallSite(double value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentPatch<Person>.Set("$.Ratio", value));

        Assert.Equal("value", exception.ParamName);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Set_WithANonFiniteFloat_ThrowsAtTheCallSite(float value) =>
        Assert.Throws<ArgumentException>(() => DocumentPatch<Person>.Set("$.Ratio", value));

    [Fact]
    public void Where_WithANonFiniteDouble_ThrowsAtTheCallSiteToo() =>
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<Person>.Where("$.Ratio", QueryOperator.Equal, double.NaN));

    [Fact]
    public void Set_WithNull_StoresJsonNullRatherThanThrowing()
    {
        var operation = Assert.Single(DocumentPatch<Person>.Set("$.Nickname", null).Operations);

        Assert.Equal(Set("$.Nickname", null), operation);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Set_WithABool_SendsJsonTextBecauseSqliteHasNoBoolean(bool value, string expected)
    {
        var operation = Assert.Single(DocumentPatch<Person>.Set("$.Active", value).Operations);

        Assert.Equal(Set("$.Active", expected, asJson: true), operation);
    }

    // Both would round through a REAL and lose digits if bound as themselves.
    [Fact]
    public void Set_WithADecimal_SendsItsExactJsonText()
    {
        var operation = Assert.Single(DocumentPatch<Person>.Set("$.Price", 10.05m).Operations);

        Assert.Equal(Set("$.Price", "10.05", asJson: true), operation);
    }

    [Fact]
    public void Set_WithAUlongPastLongMaxValue_SendsItsExactJsonText()
    {
        var operation = Assert.Single(
            DocumentPatch<Person>.Set("$.Big", ulong.MaxValue).Operations);

        Assert.Equal(Set("$.Big", "18446744073709551615", asJson: true), operation);
    }

    // Everything else is normalized exactly as DocumentQuery<T> binds it, so a patched field
    // still matches a query over the same value.
    public static TheoryData<object, object> NormalizedValues => new()
    {
        { new DateTime(2024, 3, 1), "2024-03-01T00:00:00" },
        { new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), "2024-03-01T00:00:00+00:00" },
        { Guid.Parse("2f9d1c8e-0000-0000-0000-000000000000"), "2f9d1c8e-0000-0000-0000-000000000000" },
        { new byte[] { 1, 2, 3 }, "AQID" },
        { 0.1f, 0.1d },
        { 42, 42 },
        { "Ada", "Ada" }
    };

    [Theory]
    [MemberData(nameof(NormalizedValues))]
    public void Set_NormalizesAValueTheWayADocumentQueryBindsIt(object value, object expected)
    {
        var operation = Assert.Single(DocumentPatch<Person>.Set("$.Field", value).Operations);

        Assert.Equal(Set("$.Field", expected), operation);
    }

    [Theory]
    [InlineData("$.Email")]
    [InlineData("$.Nickname")]
    public void AndSet_OnAPathThePatchAlreadyTouches_Throws(string jsonPath)
    {
        var patch = DocumentPatch<Person>.Set("$.Email", "a@b.c").AndRemove("$.Nickname");

        var exception = Assert.Throws<ArgumentException>(() => patch.AndSet(jsonPath, "x"));

        Assert.Contains(jsonPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AndRemove_OnAPathThePatchAlreadySets_Throws()
    {
        var patch = DocumentPatch<Person>.Set("$.Email", "a@b.c");

        Assert.Throws<ArgumentException>(() => patch.AndRemove("$.Email"));
    }
}
