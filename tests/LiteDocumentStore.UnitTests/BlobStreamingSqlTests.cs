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
    public void GenerateBlobRowIdSql_AlsoReadsTheStorageClass()
    {
        // Incremental blob I/O opens a TEXT value and reads its UTF-8 bytes, so the payload has
        // to be rejected before the handle is constructed rather than by SqliteBlob refusing.
        Assert.Contains("typeof(data)", SqlGenerator.GenerateBlobRowIdSql());
    }

    [Fact]
    public void GenerateBlobLengthSql_SelectsLengthWithoutReadingThePayload()
    {
        var sql = SqlGenerator.GenerateBlobLengthSql();

        Assert.Contains("length(data)", sql);
        Assert.DoesNotContain("SELECT data", sql);
    }

    [Fact]
    public void GenerateBlobLengthSql_AlsoReadsTheStorageClass()
    {
        // length() counts characters on a TEXT value and digits on a number, so the length alone
        // is a plausible byte count that is not one.
        Assert.Contains("typeof(data)", SqlGenerator.GenerateBlobLengthSql());
    }

    [Fact]
    public void GenerateGetBlobSql_ReadsTheStorageClassBeforeThePayload()
    {
        var sql = SqlGenerator.GenerateGetBlobSql();

        // typeof(data) leads the projection: a reader hands back coerced bytes for a non-blob
        // value without complaint, so the class has to be checked before ordinal 1 is read.
        Assert.StartsWith("SELECT typeof(data), data", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateReserveBlobSql_PreSizesWithZeroblobOnBothBranches()
    {
        var sql = SqlGenerator.GenerateReserveBlobSql();

        // Incremental blob I/O cannot resize a blob, so both the insert and the overwrite have
        // to reserve exactly @Len bytes before the first byte is written.
        Assert.Contains("zeroblob(@Len))", sql);
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
