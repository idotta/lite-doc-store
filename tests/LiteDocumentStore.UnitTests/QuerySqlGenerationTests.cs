using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The SQL <see cref="SqlGenerator"/> emits for a structured query: the exact statement shape,
/// the left-to-right <c>@p0..@pN</c> binding, and the validation that still happens at
/// generation time even when the builder is bypassed.
/// </summary>
[Trait("Category", "Unit")]
public class QuerySqlGenerationTests
{
    private const string Table = "Person";
    private const string SelectPrefix = "SELECT json(data) as data FROM [Person]";

    private static readonly QueryPredicate[] NoPredicates = [];
    private static readonly QueryOrdering[] NoOrderings = [];

    public static TheoryData<QueryOperator, string> ComparisonOperators => new()
    {
        { QueryOperator.Equal, "=" },
        { QueryOperator.NotEqual, "<>" },
        { QueryOperator.GreaterThan, ">" },
        { QueryOperator.GreaterThanOrEqual, ">=" },
        { QueryOperator.LessThan, "<" },
        { QueryOperator.LessThanOrEqual, "<=" },
        { QueryOperator.Like, "LIKE" },
        { QueryOperator.Glob, "GLOB" }
    };

    private static QueryPredicate Predicate(string jsonPath, QueryOperator op, object? value) =>
        new(jsonPath, op, value, NoValues());

    private static QueryPredicate InPredicate(string jsonPath, params object?[] values) =>
        new(jsonPath, QueryOperator.In, null, values);

    private static object?[] NoValues() => [];

    private static GeneratedQuery Generate(params QueryPredicate[] predicates) =>
        SqlGenerator.GenerateQuerySql(Table, predicates, NoOrderings, null, null);

    // --- Statement shape ---------------------------------------------------------------

    [Fact]
    public void GenerateQuerySql_WithNoPredicates_SelectsEveryDocument()
    {
        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, NoOrderings, null, null);

        Assert.Equal(SelectPrefix, query.Sql);
        Assert.Empty(query.ParameterValues);
    }

    [Theory]
    [MemberData(nameof(ComparisonOperators))]
    public void GenerateQuerySql_WithAComparison_EmitsTheSqlOperator(QueryOperator op, string symbol)
    {
        var query = Generate(Predicate("$.Age", op, 42));

        Assert.Equal($"{SelectPrefix} WHERE json_extract(data, '$.Age') {symbol} @p0", query.Sql);
        Assert.Equal(42, Assert.Single(query.ParameterValues));
    }

    [Fact]
    public void GenerateQuerySql_WithIsNull_EmitsIsNullAndBindsNothing()
    {
        var query = Generate(Predicate("$.DeletedAt", QueryOperator.IsNull, null));

        Assert.Equal($"{SelectPrefix} WHERE json_extract(data, '$.DeletedAt') IS NULL", query.Sql);
        Assert.Empty(query.ParameterValues);
    }

    [Fact]
    public void GenerateQuerySql_WithIsNotNull_EmitsIsNotNullAndBindsNothing()
    {
        var query = Generate(Predicate("$.DeletedAt", QueryOperator.IsNotNull, null));

        Assert.Equal($"{SelectPrefix} WHERE json_extract(data, '$.DeletedAt') IS NOT NULL", query.Sql);
        Assert.Empty(query.ParameterValues);
    }

    [Fact]
    public void GenerateQuerySql_WithIn_EmitsOneParameterPerValue()
    {
        var query = Generate(InPredicate("$.Status", "new", "open", "done"));

        Assert.Equal(
            $"{SelectPrefix} WHERE json_extract(data, '$.Status') IN (@p0, @p1, @p2)",
            query.Sql);
        Assert.Equal(3, query.ParameterValues.Count);
    }

    [Fact]
    public void GenerateQuerySql_WithArrayContains_EmitsAJsonEachExists()
    {
        var query = Generate(Predicate("$.Tags", QueryOperator.ArrayContains, "admin"));

        Assert.Equal(
            $"{SelectPrefix} WHERE EXISTS (SELECT 1 FROM json_each(data, '$.Tags') WHERE value = @p0)",
            query.Sql);
        Assert.Equal("admin", Assert.Single(query.ParameterValues));
    }

    [Fact]
    public void GenerateQuerySql_WithTwoPredicates_JoinsThemWithAnd()
    {
        var query = Generate(
            Predicate("$.Status", QueryOperator.Equal, "open"),
            Predicate("$.Age", QueryOperator.GreaterThan, 18));

        Assert.Equal(
            $"{SelectPrefix} WHERE json_extract(data, '$.Status') = @p0 " +
            "AND json_extract(data, '$.Age') > @p1",
            query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithOneOrdering_EmitsOrderByAscending()
    {
        var orderings = new[] { new QueryOrdering("$.Name", false) };

        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, orderings, null, null);

        Assert.Equal($"{SelectPrefix} ORDER BY json_extract(data, '$.Name') ASC", query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithSeveralOrderings_EmitsThemCommaSeparatedInCallOrder()
    {
        var orderings = new[]
        {
            new QueryOrdering("$.Name", false),
            new QueryOrdering("$.Age", true)
        };

        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, orderings, null, null);

        Assert.Equal(
            $"{SelectPrefix} ORDER BY json_extract(data, '$.Name') ASC, " +
            "json_extract(data, '$.Age') DESC",
            query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithTakeOnly_EmitsLimit()
    {
        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, NoOrderings, null, 10);

        Assert.Equal($"{SelectPrefix} LIMIT 10", query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithSkipOnly_EmitsAnUnboundedLimitAndTheOffset()
    {
        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, NoOrderings, 5, null);

        Assert.Equal($"{SelectPrefix} LIMIT -1 OFFSET 5", query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithSkipAndTake_EmitsLimitThenOffset()
    {
        var query = SqlGenerator.GenerateQuerySql(Table, NoPredicates, NoOrderings, 5, 10);

        Assert.Equal($"{SelectPrefix} LIMIT 10 OFFSET 5", query.Sql);
    }

    [Fact]
    public void GenerateQuerySql_WithEveryClause_EmitsThemInOrder()
    {
        var predicates = new[]
        {
            Predicate("$.Status", QueryOperator.Equal, "open"),
            Predicate("$.DeletedAt", QueryOperator.IsNull, null),
            InPredicate("$.Region", "eu", "us"),
            Predicate("$.Tags", QueryOperator.ArrayContains, "admin")
        };
        var orderings = new[] { new QueryOrdering("$.CreatedAt", true) };

        var query = SqlGenerator.GenerateQuerySql(Table, predicates, orderings, 5, 10);

        Assert.Equal(
            $"{SelectPrefix} WHERE json_extract(data, '$.Status') = @p0" +
            " AND json_extract(data, '$.DeletedAt') IS NULL" +
            " AND json_extract(data, '$.Region') IN (@p1, @p2)" +
            " AND EXISTS (SELECT 1 FROM json_each(data, '$.Tags') WHERE value = @p3)" +
            " ORDER BY json_extract(data, '$.CreatedAt') DESC LIMIT 10 OFFSET 5",
            query.Sql);
    }

    // --- Parameter binding --------------------------------------------------------------

    [Fact]
    public void GenerateQuerySql_WithMixedPredicates_BindsValuesLeftToRight()
    {
        var query = Generate(
            Predicate("$.Age", QueryOperator.GreaterThan, 18),
            InPredicate("$.Region", "eu", "us"),
            Predicate("$.Tags", QueryOperator.ArrayContains, "admin"));

        Assert.Collection(
            query.ParameterValues,
            v => Assert.Equal(18, v),
            v => Assert.Equal("eu", v),
            v => Assert.Equal("us", v),
            v => Assert.Equal("admin", v));
    }

    [Fact]
    public void GenerateQuerySql_WithAValuelessPredicate_ConsumesNoParameterSlot()
    {
        var query = Generate(
            Predicate("$.DeletedAt", QueryOperator.IsNull, null),
            Predicate("$.Status", QueryOperator.Equal, "open"));

        Assert.Contains("json_extract(data, '$.Status') = @p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal("open", Assert.Single(query.ParameterValues));
    }

    // --- Filtered count -------------------------------------------------------------------

    [Fact]
    public void GenerateFilteredCountSql_WithNoPredicates_CountsEveryRow()
    {
        var query = SqlGenerator.GenerateFilteredCountSql(Table, NoPredicates);

        Assert.Equal("SELECT COUNT(*) FROM [Person]", query.Sql);
        Assert.Empty(query.ParameterValues);
    }

    [Fact]
    public void GenerateFilteredCountSql_WithPredicates_EmitsOnlyTheWhereClause()
    {
        var predicates = new[]
        {
            Predicate("$.Status", QueryOperator.Equal, "open"),
            InPredicate("$.Region", "eu", "us")
        };

        var query = SqlGenerator.GenerateFilteredCountSql(Table, predicates);

        Assert.Equal(
            "SELECT COUNT(*) FROM [Person] WHERE json_extract(data, '$.Status') = @p0" +
            " AND json_extract(data, '$.Region') IN (@p1, @p2)",
            query.Sql);
        Assert.DoesNotContain("ORDER BY", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", query.Sql, StringComparison.Ordinal);
    }

    // --- Validation at generation time ------------------------------------------------------

    [Fact]
    public void GenerateQuerySql_WithAnInvalidTableName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQuerySql("Person]; DROP TABLE Person; --", NoPredicates, NoOrderings, null, null));
    }

    [Fact]
    public void GenerateQuerySql_WithAMalformedPredicatePath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => Generate(Predicate("$.a' OR 1=1 --", QueryOperator.Equal, 1)));
    }

    [Fact]
    public void GenerateQuerySql_WithAMalformedOrderingPath_ThrowsArgumentException()
    {
        var orderings = new[] { new QueryOrdering("$.a' --", false) };

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQuerySql(Table, NoPredicates, orderings, null, null));
    }

    [Fact]
    public void GenerateFilteredCountSql_WithAMalformedPredicatePath_ThrowsArgumentException()
    {
        var predicates = new[] { Predicate("$.a' --", QueryOperator.Equal, 1) };

        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateFilteredCountSql(Table, predicates));
    }

    [Fact]
    public void GenerateQuerySql_BeyondTheParameterCap_ThrowsArgumentException()
    {
        var tooMany = Enumerable.Range(0, SqlGenerator.MaxBoundParameters + 1)
            .Select(i => (object?)i)
            .ToArray();

        Assert.Throws<ArgumentException>(() => Generate(InPredicate("$.Id", tooMany)));
    }

    [Fact]
    public void GenerateQuerySql_AtTheParameterCap_IsAccepted()
    {
        var atCap = Enumerable.Range(0, SqlGenerator.MaxBoundParameters)
            .Select(i => (object?)i)
            .ToArray();

        var query = Generate(InPredicate("$.Id", atCap));

        Assert.Equal(SqlGenerator.MaxBoundParameters, query.ParameterValues.Count);
    }
}
