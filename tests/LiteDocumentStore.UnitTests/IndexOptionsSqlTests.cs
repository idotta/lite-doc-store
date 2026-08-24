using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The <see cref="IndexFilter"/> builder's validation and the DDL <see cref="SqlGenerator"/>
/// emits for <see cref="IndexOptions"/>: the unique/collate/direction/filter matrix, that a
/// composite index carries the collation and direction on every column, and that the collation
/// name and the filter paths are still validated at generation time.
/// </summary>
[Trait("Category", "Unit")]
public class IndexOptionsSqlTests
{
    private const string Table = "Person";
    private const string Index = "idx_Person_Email";
    private const string Injected = "Email] ON [Person] ([id]); DROP TABLE [Person]; --";

    private static string Single(IndexOptions? options) =>
        SqlGenerator.GenerateCreateJsonIndexSql(Table, Index, "$.Email", options);

    private static string Composite(IndexOptions? options) =>
        SqlGenerator.GenerateCreateCompositeJsonIndexSql(Table, Index, ["$.City", "$.Age"], options);

    // --- The option matrix ---------------------------------------------------------------

    [Fact]
    public void GenerateCreateJsonIndexSql_WithNoOptions_EmitsTheStatementItAlwaysDid()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] (json_extract(data, '$.Email'))",
            Single(null));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithDefaultOptions_EmitsNoExtraDdl()
    {
        Assert.Equal(Single(null), Single(new IndexOptions()));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithUnique_EmitsCreateUniqueIndex()
    {
        Assert.Equal(
            "CREATE UNIQUE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] (json_extract(data, '$.Email'))",
            Single(new IndexOptions { Unique = true }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithCollation_CollatesTheIndexedExpression()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] " +
            "(json_extract(data, '$.Email') COLLATE NOCASE)",
            Single(new IndexOptions { Collation = "NOCASE" }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WhenDescending_EmitsDesc()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] (json_extract(data, '$.Email') DESC)",
            Single(new IndexOptions { Descending = true }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithAFilter_EmitsAPartialIndex()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] (json_extract(data, '$.Email')) " +
            "WHERE json_extract(data, '$.Email') IS NOT NULL",
            Single(new IndexOptions { Filter = IndexFilter.IsNotNull("$.Email") }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithAChainedFilter_CombinesTheTermsWithAnd()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] (json_extract(data, '$.Email')) " +
            "WHERE json_extract(data, '$.Email') IS NOT NULL AND json_extract(data, '$.DeletedAt') IS NULL",
            Single(new IndexOptions
            {
                Filter = IndexFilter.IsNotNull("$.Email").AndIsNull("$.DeletedAt")
            }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithEveryOption_EmitsThemInSqlitesOrder()
    {
        Assert.Equal(
            "CREATE UNIQUE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] " +
            "(json_extract(data, '$.Email') COLLATE NOCASE DESC) " +
            "WHERE json_extract(data, '$.Email') IS NOT NULL",
            Single(new IndexOptions
            {
                Unique = true,
                Collation = "NOCASE",
                Descending = true,
                Filter = IndexFilter.IsNotNull("$.Email")
            }));
    }

    // --- Composite ----------------------------------------------------------------------

    [Fact]
    public void GenerateCreateCompositeJsonIndexSql_WithNoOptions_EmitsTheStatementItAlwaysDid()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] " +
            "(json_extract(data, '$.City'), json_extract(data, '$.Age'))",
            Composite(null));
    }

    [Fact]
    public void GenerateCreateCompositeJsonIndexSql_WithCollationAndDirection_AppliesThemToEveryColumn()
    {
        Assert.Equal(
            "CREATE UNIQUE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] " +
            "(json_extract(data, '$.City') COLLATE NOCASE DESC, " +
            "json_extract(data, '$.Age') COLLATE NOCASE DESC)",
            Composite(new IndexOptions { Unique = true, Collation = "NOCASE", Descending = true }));
    }

    [Fact]
    public void GenerateCreateCompositeJsonIndexSql_WithAFilter_EmitsOneWhereForTheWholeIndex()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS [idx_Person_Email] ON [Person] " +
            "(json_extract(data, '$.City'), json_extract(data, '$.Age')) " +
            "WHERE json_extract(data, '$.City') IS NOT NULL",
            Composite(new IndexOptions { Filter = IndexFilter.IsNotNull("$.City") }));
    }

    // --- Validation at generation time ---------------------------------------------------

    [Fact]
    public void GenerateCreateJsonIndexSql_WithAnInjectedCollation_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Single(new IndexOptions { Collation = Injected }));

        Assert.Equal("options.Collation", exception.ParamName);
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithAQuotedCollation_Throws()
    {
        Assert.Throws<ArgumentException>(() => Single(new IndexOptions { Collation = "NO'CASE" }));
    }

    [Fact]
    public void GenerateCreateJsonIndexSql_WithAnInjectedFilterPath_Throws()
    {
        // The builder validates its paths, so bypass it — the generator must still defend itself.
        var filter = new IndexFilter(new IndexFilterTerm("$.Email') OR 1=1 --", RequiresNull: false));

        var exception = Assert.Throws<ArgumentException>(
            () => Single(new IndexOptions { Filter = filter }));

        Assert.Equal("options.Filter", exception.ParamName);
    }

    // --- The IndexFilter builder ---------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IndexFilter_WithAnEmptyPath_Throws(string? jsonPath)
    {
        Assert.Throws<ArgumentException>(() => IndexFilter.IsNull(jsonPath!));
        Assert.Throws<ArgumentException>(() => IndexFilter.IsNotNull(jsonPath!));
    }

    [Fact]
    public void IndexFilter_WithAMalformedPath_ThrowsAtBuildTime()
    {
        Assert.Throws<ArgumentException>(() => IndexFilter.IsNotNull("Email"));
        Assert.Throws<ArgumentException>(() => IndexFilter.IsNotNull("$.Em'ail"));
    }

    [Fact]
    public void IndexFilter_WhenExtended_LeavesTheOriginalUntouched()
    {
        var filter = IndexFilter.IsNotNull("$.Email");
        var extended = filter.AndIsNull("$.DeletedAt");

        Assert.Single(filter.Terms);
        Assert.Equal(2, extended.Terms.Count);
        Assert.NotSame(filter, extended);
    }

    [Fact]
    public void IndexFilter_KeepsTheTermsInCallOrder()
    {
        var filter = IndexFilter.IsNull("$.DeletedAt").AndIsNotNull("$.Email");

        Assert.Equal("$.DeletedAt", filter.Terms[0].JsonPath);
        Assert.True(filter.Terms[0].RequiresNull);
        Assert.Equal("$.Email", filter.Terms[1].JsonPath);
        Assert.False(filter.Terms[1].RequiresNull);
    }
}
