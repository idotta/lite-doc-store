using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the bulk statement generators and their batch-size cap.
/// </summary>
[Trait("Category", "Unit")]
public class BatchSqlTests
{
    [Fact]
    public void MaxBatchItemsPerStatement_StaysInsideSqliteParameterLimit()
    {
        // An upsert binds 2N parameters; both must fit SQLITE_MAX_VARIABLE_NUMBER (32766).
        Assert.True(SqlGenerator.MaxBatchItemsPerStatement * 2 < 32766);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(SqlGenerator.MaxBatchItemsPerStatement)]
    public void GenerateBulkUpsertSql_AtOrBelowTheCap_Generates(int count)
    {
        var sql = SqlGenerator.GenerateBulkUpsertSql("Person", count);

        Assert.Contains($"@Id{count - 1}", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(SqlGenerator.MaxBatchItemsPerStatement)]
    public void GenerateBulkDeleteSql_AtOrBelowTheCap_Generates(int count)
    {
        var sql = SqlGenerator.GenerateBulkDeleteSql("Person", count);

        Assert.Contains($"@Id{count - 1}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateBulkUpsertSql_AboveTheCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlGenerator.GenerateBulkUpsertSql("Person", SqlGenerator.MaxBatchItemsPerStatement + 1));
    }

    [Fact]
    public void GenerateBulkDeleteSql_AboveTheCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlGenerator.GenerateBulkDeleteSql("Person", SqlGenerator.MaxBatchItemsPerStatement + 1));
    }
}
