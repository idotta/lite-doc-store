using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the basic-operation statement generators: bulk get, delete-all and the
/// drop statements.
/// </summary>
[Trait("Category", "Unit")]
public class BasicOpsSqlTests
{
    [Fact]
    public void GenerateBulkGetSql_WithASingleId_GeneratesOneParameter()
    {
        var sql = SqlGenerator.GenerateBulkGetSql("Person", 1);

        Assert.Equal("SELECT id, json(data) as data FROM [Person] WHERE id IN (@Id0)", sql);
    }

    [Fact]
    public void GenerateBulkGetSql_WithThreeIds_GeneratesTheExpectedStatement()
    {
        var sql = SqlGenerator.GenerateBulkGetSql("Person", 3);

        Assert.Equal(
            "SELECT id, json(data) as data FROM [Person] WHERE id IN (@Id0, @Id1, @Id2)",
            sql);
    }

    [Fact]
    public void GenerateBulkGetSql_AtTheCap_BindsOneParameterPerItem()
    {
        var count = SqlGenerator.MaxBatchItemsPerStatement;

        var sql = SqlGenerator.GenerateBulkGetSql("Person", count);

        Assert.Contains("@Id0", sql, StringComparison.Ordinal);
        Assert.Contains($"@Id{count - 1}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Id{count}", sql, StringComparison.Ordinal);
        Assert.Equal(count, sql.Split("@Id", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GenerateBulkGetSql_WithANonPositiveCount_Throws(int count)
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkGetSql("Person", count));
    }

    [Fact]
    public void GenerateBulkGetSql_AboveTheCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlGenerator.GenerateBulkGetSql("Person", SqlGenerator.MaxBatchItemsPerStatement + 1));
    }

    [Fact]
    public void GenerateBulkGetSql_WithAnInvalidTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkGetSql("Person]; DROP TABLE x--", 3));
    }

    [Fact]
    public void GenerateDeleteAllSql_WithAValidTableName_GeneratesTheExpectedStatement()
    {
        var sql = SqlGenerator.GenerateDeleteAllSql("Person");

        Assert.Equal("DELETE FROM [Person]", sql);
    }

    [Fact]
    public void GenerateDeleteAllSql_WithAnInvalidTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateDeleteAllSql("Person]; DROP TABLE x--"));
    }

    [Fact]
    public void GenerateDropTableSql_WithAValidTableName_GeneratesTheExpectedStatement()
    {
        var sql = SqlGenerator.GenerateDropTableSql("Person");

        Assert.Equal("DROP TABLE IF EXISTS [Person]", sql);
    }

    [Fact]
    public void GenerateDropTableSql_WithAnInvalidTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateDropTableSql("Person]; DROP TABLE x--"));
    }

    [Fact]
    public void GenerateDropIndexSql_WithAValidIndexName_GeneratesTheExpectedStatement()
    {
        var sql = SqlGenerator.GenerateDropIndexSql("idx_Person_Email");

        Assert.Equal("DROP INDEX IF EXISTS [idx_Person_Email]", sql);
    }

    [Fact]
    public void GenerateDropIndexSql_WithAnInvalidIndexName_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateDropIndexSql("idx]; DROP TABLE x--"));
    }
}
