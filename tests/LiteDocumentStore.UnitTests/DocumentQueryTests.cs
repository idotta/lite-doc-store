using System.Text.Json;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The <see cref="DocumentQuery{T}"/> builder: what each method records, that every method
/// returns a new instance, and the up-front argument validation.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentQueryTests
{
    private const string Path = "$.Email";

    /// <summary>
    /// Each supported CLR value and the value the predicate must actually bind — the text or
    /// number System.Text.Json wrote into the document. Measured against real SQLite; anything
    /// left unnormalized here binds as itself.
    /// </summary>
    public static TheoryData<object, object> SupportedValues => new()
    {
        { "text", "text" },
        { true, true },
        { (byte)7, (byte)7 },
        { (sbyte)-8, (sbyte)-8 },
        { (short)300, (short)300 },
        { (ushort)400, (ushort)400 },
        { 42, 42 },
        { 4000000000u, 4000000000u },
        { 42L, 42L },
        { 42UL, 42UL },
        { ulong.MaxValue, (double)ulong.MaxValue },
        { 1.5d, 1.5d },
        { 0.1f, 0.1d },
        { 1.5m, 1.5d },
        { new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc), "2026-08-23T10:00:00Z" },
        { new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero), "2026-08-23T10:00:00+00:00" },
        { Guid.Parse("11111111-1111-1111-1111-111111111111"), "11111111-1111-1111-1111-111111111111" },
        { new byte[] { 1, 2, 3 }, "AQID" }
    };

    // --- What the builder records ---------------------------------------------------

    [Fact]
    public void All_WithNothingElse_ProducesAnEmptyQuery()
    {
        var query = DocumentQuery<QueryDocument>.All();

        Assert.Empty(query.Predicates);
        Assert.Empty(query.Orderings);
        Assert.Null(query.SkipCount);
        Assert.Null(query.TakeCount);
    }

    [Fact]
    public void Where_WithAComparison_RecordsThePredicate()
    {
        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.GreaterThan, 42);

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(Path, predicate.JsonPath);
        Assert.Equal(QueryOperator.GreaterThan, predicate.Operator);
        Assert.Equal(42, predicate.Value);
        Assert.Empty(predicate.Values);
    }

    [Fact]
    public void And_WithSeveralCalls_AccumulatesPredicatesInCallOrder()
    {
        var query = DocumentQuery<QueryDocument>
            .Where("$.A", QueryOperator.Equal, 1)
            .And("$.B", QueryOperator.LessThan, 2)
            .And("$.C", QueryOperator.Like, "x%");

        Assert.Collection(
            query.Predicates,
            p => Assert.Equal("$.A", p.JsonPath),
            p => Assert.Equal("$.B", p.JsonPath),
            p => Assert.Equal("$.C", p.JsonPath));
    }

    [Fact]
    public void WhereIsNull_WithAPath_RecordsAValuelessPredicate()
    {
        var query = DocumentQuery<QueryDocument>.WhereIsNull(Path);

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(QueryOperator.IsNull, predicate.Operator);
        Assert.Null(predicate.Value);
        Assert.Empty(predicate.Values);
    }

    [Fact]
    public void WhereIsNotNull_WithAPath_RecordsAnIsNotNullPredicate()
    {
        var query = DocumentQuery<QueryDocument>.WhereIsNotNull(Path);

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(QueryOperator.IsNotNull, predicate.Operator);
        Assert.Null(predicate.Value);
    }

    [Fact]
    public void AndIsNull_WithAPath_AddsAnIsNullPredicate()
    {
        var query = DocumentQuery<QueryDocument>.Where("$.A", QueryOperator.Equal, 1).AndIsNull("$.B");

        Assert.Equal(2, query.Predicates.Count);
        Assert.Equal(QueryOperator.IsNull, query.Predicates[1].Operator);
    }

    [Fact]
    public void AndIsNotNull_WithAPath_AddsAnIsNotNullPredicate()
    {
        var query = DocumentQuery<QueryDocument>.WhereIsNull("$.A").AndIsNotNull("$.B");

        Assert.Collection(
            query.Predicates,
            p => Assert.Equal(QueryOperator.IsNull, p.Operator),
            p => Assert.Equal(QueryOperator.IsNotNull, p.Operator));
    }

    [Fact]
    public void WhereIn_WithValues_RecordsThemInOrder()
    {
        var query = DocumentQuery<QueryDocument>.WhereIn(Path, ["a", "b", "c"]);

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(QueryOperator.In, predicate.Operator);
        Assert.Null(predicate.Value);
        Assert.Collection(
            predicate.Values,
            v => Assert.Equal("a", v),
            v => Assert.Equal("b", v),
            v => Assert.Equal("c", v));
    }

    [Fact]
    public void AndIn_WithValues_AddsAnInPredicate()
    {
        var query = DocumentQuery<QueryDocument>.Where("$.A", QueryOperator.Equal, 1).AndIn("$.B", [1, 2]);

        Assert.Equal(2, query.Predicates.Count);
        Assert.Equal(QueryOperator.In, query.Predicates[1].Operator);
        Assert.Equal(2, query.Predicates[1].Values.Count);
    }

    [Fact]
    public void WhereArrayContains_WithAValue_RecordsAnArrayContainsPredicate()
    {
        var query = DocumentQuery<QueryDocument>.WhereArrayContains("$.Tags", "admin");

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(QueryOperator.ArrayContains, predicate.Operator);
        Assert.Equal("admin", predicate.Value);
    }

    [Fact]
    public void AndArrayContains_WithAValue_AddsAnArrayContainsPredicate()
    {
        var query = DocumentQuery<QueryDocument>.Where("$.A", QueryOperator.Equal, 1)
            .AndArrayContains("$.Tags", 7);

        Assert.Equal(2, query.Predicates.Count);
        Assert.Equal(QueryOperator.ArrayContains, query.Predicates[1].Operator);
        Assert.Equal(7, query.Predicates[1].Value);
    }

    [Fact]
    public void OrderBy_WithSeveralCalls_AccumulatesOrderingsInCallOrder()
    {
        var query = DocumentQuery<QueryDocument>.All()
            .OrderBy("$.Name")
            .OrderBy("$.Age", descending: true);

        Assert.Collection(
            query.Orderings,
            o =>
            {
                Assert.Equal("$.Name", o.JsonPath);
                Assert.False(o.Descending);
            },
            o =>
            {
                Assert.Equal("$.Age", o.JsonPath);
                Assert.True(o.Descending);
            });
    }

    [Fact]
    public void SkipAndTake_WithPositiveValues_RecordTheOffsetAndLimit()
    {
        var query = DocumentQuery<QueryDocument>.All().Skip(5).Take(10);

        Assert.Equal(5, query.SkipCount);
        Assert.Equal(10, query.TakeCount);
    }

    // --- Immutability -----------------------------------------------------------------

    [Fact]
    public void And_OnAnExistingQuery_ReturnsANewInstanceAndLeavesTheOriginalUnchanged()
    {
        var original = DocumentQuery<QueryDocument>.Where("$.A", QueryOperator.Equal, 1);

        var derived = original.And("$.B", QueryOperator.Equal, 2);

        Assert.NotSame(original, derived);
        Assert.Single(original.Predicates);
        Assert.Equal(2, derived.Predicates.Count);
    }

    [Fact]
    public void OrderBy_OnAnExistingQuery_ReturnsANewInstanceAndLeavesTheOriginalUnchanged()
    {
        var original = DocumentQuery<QueryDocument>.All().OrderBy("$.A");

        var derived = original.OrderBy("$.B");

        Assert.NotSame(original, derived);
        Assert.Single(original.Orderings);
        Assert.Equal(2, derived.Orderings.Count);
    }

    [Fact]
    public void Skip_OnAnExistingQuery_ReturnsANewInstanceAndLeavesTheOriginalUnchanged()
    {
        var original = DocumentQuery<QueryDocument>.All();

        var derived = original.Skip(5);

        Assert.NotSame(original, derived);
        Assert.Null(original.SkipCount);
        Assert.Equal(5, derived.SkipCount);
    }

    [Fact]
    public void Take_OnAnExistingQuery_ReturnsANewInstanceAndLeavesTheOriginalUnchanged()
    {
        var original = DocumentQuery<QueryDocument>.All();

        var derived = original.Take(10);

        Assert.NotSame(original, derived);
        Assert.Null(original.TakeCount);
        Assert.Equal(10, derived.TakeCount);
    }

    // --- Validation -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Where_WithANullOrWhitespacePath_ThrowsArgumentException(string? jsonPath)
    {
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(jsonPath!, QueryOperator.Equal, 1));
    }

    [Theory]
    [InlineData("$.a'; DROP TABLE x--")]
    [InlineData("no-dollar")]
    [InlineData("$.a b")]
    public void Where_WithAMalformedPath_ThrowsArgumentException(string jsonPath)
    {
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(jsonPath, QueryOperator.Equal, 1));
    }

    [Fact]
    public void OrderBy_WithAMalformedPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DocumentQuery<QueryDocument>.All().OrderBy("$.a'--"));
    }

    [Fact]
    public void Where_WithAnUndefinedOperator_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(Path, (QueryOperator)99, 1));
    }

    [Theory]
    [InlineData(QueryOperator.IsNull)]
    [InlineData(QueryOperator.IsNotNull)]
    public void Where_WithAValueForAValuelessOperator_ThrowsArgumentException(QueryOperator op)
    {
        Assert.Throws<ArgumentException>(() => DocumentQuery<QueryDocument>.Where(Path, op, "x"));
    }

    [Fact]
    public void Where_WithTheInOperator_ThrowsArgumentExceptionPointingAtWhereIn()
    {
        var fromWhere = Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(Path, QueryOperator.In, "x"));
        var fromAnd = Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.All().And(Path, QueryOperator.In, "x"));

        Assert.Contains("WhereIn", fromWhere.Message, StringComparison.Ordinal);
        Assert.Contains("AndIn", fromAnd.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QueryOperator.Equal)]
    [InlineData(QueryOperator.NotEqual)]
    public void Where_WithNullForEquality_ThrowsArgumentExceptionNamingTheNullOperators(QueryOperator op)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(Path, op, null));

        Assert.Contains("IsNull", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IsNotNull", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QueryOperator.GreaterThan)]
    [InlineData(QueryOperator.LessThanOrEqual)]
    [InlineData(QueryOperator.Like)]
    [InlineData(QueryOperator.ArrayContains)]
    public void Where_WithNullForAValueOperator_ThrowsArgumentException(QueryOperator op)
    {
        Assert.Throws<ArgumentException>(() => DocumentQuery<QueryDocument>.Where(Path, op, null));
    }

    [Theory]
    [InlineData(QueryOperator.Like)]
    [InlineData(QueryOperator.Glob)]
    public void Where_WithANonStringPattern_ThrowsArgumentException(QueryOperator op)
    {
        Assert.Throws<ArgumentException>(() => DocumentQuery<QueryDocument>.Where(Path, op, 42));
    }

    [Fact]
    public void WhereIn_WithANullCollection_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentQuery<QueryDocument>.WhereIn(Path, null!));
    }

    [Fact]
    public void WhereIn_WithAnEmptyCollection_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DocumentQuery<QueryDocument>.WhereIn(Path, []));
    }

    [Fact]
    public void WhereIn_WithANullElement_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => DocumentQuery<QueryDocument>.WhereIn(Path, ["a", null]));
    }

    [Fact]
    public void Where_WithAnUnsupportedValueType_ThrowsArgumentExceptionNamingTheType()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, new QueryDocument()));

        Assert.Contains(typeof(QueryDocument).ToString(), exception.Message, StringComparison.Ordinal);
    }

    // --- Bound-value normalization --------------------------------------------------

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public void Where_WithASupportedValueType_RecordsTheSerializedRepresentation(
        object value,
        object expected)
    {
        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, value);

        Assert.Equal(expected, Assert.Single(query.Predicates).Value);
    }

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public void WhereIn_WithASupportedValueType_NormalizesEveryElement(object value, object expected)
    {
        var query = DocumentQuery<QueryDocument>.WhereIn(Path, ["untouched", value]);

        var predicate = Assert.Single(query.Predicates);
        Assert.Equal(["untouched", expected], predicate.Values);
    }

    [Theory]
    // System.Text.Json writes the kind into the text, so the bound value has to carry it too.
    [InlineData(DateTimeKind.Unspecified, "2026-08-23T10:00:00.1234567")]
    [InlineData(DateTimeKind.Utc, "2026-08-23T10:00:00.1234567Z")]
    public void Where_WithADateTime_BindsTheIsoTextForItsKind(DateTimeKind kind, string expected)
    {
        var value = new DateTime(2026, 8, 23, 10, 0, 0, kind).AddTicks(1234567);

        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, value);

        Assert.Equal(expected, Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void Where_WithALocalDateTime_BindsTheIsoTextWithTheLocalOffset()
    {
        var value = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Local);

        // The offset is machine-dependent, so the serializer itself is the oracle.
        var expected = JsonSerializer.Serialize(value).Trim('"');

        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, value);

        Assert.Equal(expected, Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void Where_WithADateTimeOffset_BindsTheIsoTextWithItsOffset()
    {
        var value = new DateTimeOffset(2026, 8, 23, 10, 0, 0, 123, TimeSpan.FromHours(-3));

        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, value);

        Assert.Equal("2026-08-23T10:00:00.123-03:00", Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void Where_WithAULongBelowLongMaxValue_LeavesItIntegral()
    {
        // Only past long.MaxValue does ADO wrap the value negative; below it the integer is exact.
        var query = DocumentQuery<QueryDocument>.Where(
            Path, QueryOperator.Equal, (ulong)long.MaxValue);

        Assert.Equal((ulong)long.MaxValue, Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void Where_WithAFloat_BindsTheDoubleItsShortestTextParsesTo()
    {
        // Widening 0.1f straight to double gives 0.100000001490116, not the 0.1 SQLite parsed.
        var query = DocumentQuery<QueryDocument>.Where(Path, QueryOperator.Equal, 1f / 3f);

        Assert.Equal(0.33333334d, Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void WhereArrayContains_WithADateTime_NormalizesTheElement()
    {
        var query = DocumentQuery<QueryDocument>.WhereArrayContains(
            Path, new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("2026-08-23T00:00:00Z", Assert.Single(query.Predicates).Value);
    }

    [Fact]
    public void Skip_WithANegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentQuery<QueryDocument>.All().Skip(-1));
    }

    [Fact]
    public void Take_WithANegativeLimit_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentQuery<QueryDocument>.All().Take(-1));
    }

    [Fact]
    public void Take_WithZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentQuery<QueryDocument>.All().Take(0));
    }
}

/// <summary>A type marker for the queries under test; nothing reflects over it.</summary>
internal sealed class QueryDocument
{
    public string? Email { get; set; }
}
