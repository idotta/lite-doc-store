using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the SQL shapes and the payload limit backing streamed blob I/O.
/// </summary>
[Trait("Category", "Unit")]
public class BlobStreamingSqlTests
{
    [Fact]
    public void GenerateBlobRowIdSql_SelectsRowIdFromTheBlobTable()
    {
        var sql = SqlGenerator.GenerateBlobRowIdSql();

        Assert.Contains("SELECT rowid", sql);
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", sql);
        Assert.Contains("WHERE id = @Id", sql);
    }

    [Fact]
    public void GenerateBlobLengthSql_SelectsLengthWithoutReadingThePayload()
    {
        var sql = SqlGenerator.GenerateBlobLengthSql();

        Assert.Contains("length(data)", sql);
        Assert.DoesNotContain("SELECT data", sql);
    }

    [Fact]
    public void GenerateReserveBlobSql_PreSizesWithZeroblobOnBothBranches()
    {
        var sql = SqlGenerator.GenerateReserveBlobSql();

        // Incremental blob I/O cannot resize a blob, so both the insert and the overwrite have
        // to reserve exactly @Len bytes before the first byte is written.
        Assert.Contains("VALUES (@Id, zeroblob(@Len))", sql);
        Assert.Contains("data = zeroblob(@Len)", sql);
    }

    [Fact]
    public void GenerateReserveBlobSql_ReturnsTheRowIdSoAnOverwriteNeedsNoSecondLookup()
    {
        Assert.Contains("RETURNING rowid", SqlGenerator.GenerateReserveBlobSql());
    }

    [Fact]
    public void MaxBlobLength_MatchesSqlitesDefaultMaximumLength()
    {
        Assert.Equal(1_000_000_000L, BlobLimits.MaxBlobLength);
    }

    [Theory]
    [InlineData("blob_a1")]
    [InlineData("blob_00ff")]
    public void SavepointGenerators_QuoteTheName(string name)
    {
        Assert.Equal($"SAVEPOINT [{name}]", SqlGenerator.GenerateSavepointSql(name));
        Assert.Equal($"ROLLBACK TO [{name}]", SqlGenerator.GenerateRollbackToSavepointSql(name));
        Assert.Equal($"RELEASE [{name}]", SqlGenerator.GenerateReleaseSavepointSql(name));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("bad]name")]
    [InlineData("1leading")]
    [InlineData("")]
    public void SavepointGenerators_RejectAnUnusableName(string name)
    {
        // The name is generated, never caller-supplied, but it is interpolated like any other
        // identifier and so is validated like one.
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateSavepointSql(name));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateRollbackToSavepointSql(name));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateReleaseSavepointSql(name));
    }
}
