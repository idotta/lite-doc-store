using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the blob metadata SQL: the table layout, the in-place column upgrade, the
/// rebuild steps, the version-guarded writes, and the prefix range a listing filters on.
/// </summary>
[Trait("Category", "Unit")]
public class BlobMetadataSqlTests
{
    [Fact]
    public void GenerateCreateBlobTableSql_PutsThePayloadColumnLast()
    {
        var sql = SqlGenerator.GenerateCreateBlobTableSql();

        // Not cosmetic: SQLite reads a row front to back, so a column behind the payload can only
        // be reached by walking its overflow pages.
        var dataIndex = sql.IndexOf("data BLOB NOT NULL", StringComparison.Ordinal);
        Assert.True(dataIndex > 0);

        foreach (var (name, _) in SqlGenerator.BlobMetadataColumns)
        {
            Assert.True(
                sql.IndexOf(name + " ", StringComparison.Ordinal) < dataIndex,
                $"{name} must be declared before the payload column");
        }
    }

    [Fact]
    public void GenerateCreateBlobTableSql_DeclaresTheSameNullabilityTheUpgradeCanProduce()
    {
        var sql = SqlGenerator.GenerateCreateBlobTableSql();

        // ALTER TABLE ADD COLUMN only accepts a constant default, so an upgraded table cannot
        // have NOT NULL timestamps. A fresh one must not either, or the two would diverge.
        Assert.Contains("content_type TEXT NULL", sql);
        Assert.Contains("created_at INTEGER NULL", sql);
        Assert.Contains("updated_at INTEGER NULL", sql);
        Assert.Contains("version INTEGER NOT NULL DEFAULT 1", sql);
    }

    [Fact]
    public void BlobMetadataColumns_AreAllAddableByAlterTable()
    {
        foreach (var (name, definition) in SqlGenerator.BlobMetadataColumns)
        {
            var sql = SqlGenerator.GenerateAddBlobColumnSql(name);
            Assert.Equal($"ALTER TABLE [__store_blobs] ADD COLUMN {name} {definition}", sql);

            // A non-constant default (CURRENT_TIMESTAMP, an expression) is illegal there.
            if (definition.Contains("DEFAULT", StringComparison.Ordinal))
            {
                Assert.Contains("DEFAULT 1", definition);
            }
        }
    }

    [Fact]
    public void GenerateAddBlobColumnSql_RejectsAColumnItDoesNotOwn()
    {
        // The definition is looked up, never taken from the caller, so no fragment reaches DDL.
        var ex = Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateAddBlobColumnSql("data"));
        Assert.Equal("columnName", ex.ParamName);

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddBlobColumnSql("version] ; DROP TABLE x --"));
    }

    [Fact]
    public void GenerateBlobColumnExistsSql_BindsTheColumnName()
    {
        var sql = SqlGenerator.GenerateBlobColumnExistsSql();

        Assert.Contains("pragma_table_info('__store_blobs')", sql);
        Assert.Contains("name = @Name", sql);
    }

    [Fact]
    public void GenerateBlobTableRebuildSteps_CopiesEveryColumnAndSwapsTheTableIn()
    {
        var steps = SqlGenerator.GenerateBlobTableRebuildSteps();

        Assert.Equal(5, steps.Count);
        // A scratch table left by an interrupted rebuild must not fail the next one.
        Assert.Contains("DROP TABLE IF EXISTS [__store_blobs_rebuild]", steps[0]);
        Assert.Contains("CREATE TABLE [__store_blobs_rebuild]", steps[1]);
        Assert.Contains("SELECT id, content_type, created_at, updated_at, version, data", steps[2]);
        Assert.Equal("DROP TABLE [__store_blobs]", steps[3]);
        Assert.Contains("RENAME TO [__store_blobs]", steps[4]);

        // The scratch table has to be the layout the fresh table has, or the rebuild is pointless.
        var dataIndex = steps[1].IndexOf("data BLOB NOT NULL", StringComparison.Ordinal);
        Assert.True(steps[1].IndexOf("version ", StringComparison.Ordinal) < dataIndex);
    }

    [Fact]
    public void GeneratePutBlobSql_StampsMetadataAndBumpsTheVersionOnOverwrite()
    {
        var sql = SqlGenerator.GeneratePutBlobSql();

        Assert.Contains("unixepoch('subsec') * 1000", sql);
        Assert.Contains("version = version + 1", sql);
        Assert.Contains("content_type = excluded.content_type", sql);
        Assert.Contains("updated_at = excluded.updated_at", sql);
        // created_at is absent from the SET list, so an overwrite keeps naming the first write.
        Assert.DoesNotContain("created_at = ", sql);
    }

    [Fact]
    public void GenerateInsertBlobIfAbsentSql_DoesNothingOnConflictAndReturnsTheVersion()
    {
        var sql = SqlGenerator.GenerateInsertBlobIfAbsentSql();

        Assert.Contains("ON CONFLICT(id) DO NOTHING", sql);
        Assert.Contains("RETURNING version", sql);
    }

    [Fact]
    public void GenerateVersionedPutBlobSql_GuardsOnTheStoredVersion()
    {
        var sql = SqlGenerator.GenerateVersionedPutBlobSql();

        Assert.Contains("WHERE id = @Id AND version = @ExpectedVersion", sql);
        Assert.Contains("version = version + 1", sql);
        Assert.Contains("RETURNING version", sql);
    }

    [Fact]
    public void GenerateVersionedReserveBlobSql_GuardsTheStreamedWriteBeforeAByteIsWritten()
    {
        var sql = SqlGenerator.GenerateVersionedReserveBlobSql();

        Assert.Contains("data = zeroblob(@Len)", sql);
        Assert.Contains("WHERE id = @Id AND version = @ExpectedVersion", sql);
        // The rowid to fill and the version stored, in one statement.
        Assert.Contains("RETURNING rowid, version", sql);
    }

    [Fact]
    public void GenerateReserveBlobIfAbsentSql_ReservesOnlyWhenTheIdIsFree()
    {
        var sql = SqlGenerator.GenerateReserveBlobIfAbsentSql();

        Assert.Contains("zeroblob(@Len)", sql);
        Assert.Contains("ON CONFLICT(id) DO NOTHING", sql);
        Assert.Contains("RETURNING rowid, version", sql);
    }

    [Fact]
    public void GenerateVersionedDeleteBlobSql_GuardsOnTheStoredVersion()
    {
        Assert.Equal(
            "DELETE FROM [__store_blobs] WHERE id = @Id AND version = @ExpectedVersion",
            SqlGenerator.GenerateVersionedDeleteBlobSql());
    }

    [Fact]
    public void GenerateBlobInfoSql_ComputesTheLengthInsteadOfStoringIt()
    {
        var sql = SqlGenerator.GenerateBlobInfoSql();

        // length(data) is answered from the record header, so it costs nothing and cannot drift
        // from a payload a consumer writes with raw SQL.
        Assert.Contains("length(data)", sql);
        Assert.DoesNotContain("SELECT data", sql);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, true, false)]
    public void GenerateListBlobsSql_EmitsOnlyTheClausesItWasGiven(
        bool hasPrefix,
        bool hasUpperBound,
        bool hasSkip,
        bool hasTake)
    {
        var sql = SqlGenerator.GenerateListBlobsSql(hasPrefix, hasUpperBound, hasSkip, hasTake);

        Assert.Equal(hasPrefix, sql.Contains("id >= @Prefix", StringComparison.Ordinal));
        Assert.Equal(hasUpperBound, sql.Contains("id < @PrefixEnd", StringComparison.Ordinal));
        Assert.Equal(hasSkip, sql.Contains("OFFSET @Skip", StringComparison.Ordinal));
        Assert.Equal(hasTake, sql.Contains("LIMIT @Take", StringComparison.Ordinal));
        Assert.Contains("ORDER BY id", sql);

        // SQLite has no OFFSET without a LIMIT.
        if (hasSkip && !hasTake)
        {
            Assert.Contains("LIMIT -1", sql);
        }
    }

    [Fact]
    public void GenerateListBlobsSql_NeverBuildsALikePattern()
    {
        var sql = SqlGenerator.GenerateListBlobsSql(true, true, true, true);

        // A prefix is a key range, so wildcards in it are literal and there is no escape
        // character to get wrong — and LIKE would additionally be ASCII case-insensitive.
        Assert.DoesNotContain("LIKE", sql);
        Assert.DoesNotContain("GLOB", sql);
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("user/", "user0")]
    [InlineData("az", "a{")]
    [InlineData("a%", "a&")]
    [InlineData("é", "ê")]
    // A prefix ending at the last code point before the surrogate block skips over it: an
    // unpaired surrogate is not a code point SQLite can hold.
    [InlineData("a퟿", "a")]
    // Trailing maximum code points cannot be incremented, so they are dropped — "b" still bounds
    // every id starting with "a\U0010FFFF".
    [InlineData("a\U0010FFFF", "b")]
    [InlineData("a\U0010FFFF\U0010FFFF", "b")]
    public void TryGetUpperBound_ReturnsTheFirstStringAfterEveryIdWithThePrefix(
        string prefix,
        string expected)
    {
        Assert.True(BlobIdPrefix.TryGetUpperBound(prefix, out var upperBound));
        Assert.Equal(expected, upperBound);
        Assert.True(string.CompareOrdinal(prefix, upperBound) < 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\U0010FFFF")]
    [InlineData("\U0010FFFF\U0010FFFF")]
    public void TryGetUpperBound_ReportsNoBoundWhenTheLowerBoundAloneIsExact(string prefix)
    {
        Assert.False(BlobIdPrefix.TryGetUpperBound(prefix, out var upperBound));
        Assert.Equal(string.Empty, upperBound);
    }

    [Fact]
    public void TryGetUpperBound_HandlesASupplementaryPlaneCharacterAsOneCodePoint()
    {
        // "\U0001F600" is a surrogate pair in UTF-16; incrementing the trailing code unit would
        // produce a different character entirely.
        Assert.True(BlobIdPrefix.TryGetUpperBound("\U0001F600", out var upperBound));
        Assert.Equal("\U0001F601", upperBound);
    }

    [Fact]
    public void BlobWriteOptions_RejectsABlankContentType()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new BlobWriteOptions { ContentType = "  " }.Validate());
        Assert.Equal("ContentType", ex.ParamName);

        // Null means "record none", which is not the same thing and is allowed.
        new BlobWriteOptions { ContentType = null }.Validate();
        new BlobWriteOptions { ContentType = "application/pdf" }.Validate();
    }
}
