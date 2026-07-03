using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the SQL shapes backing optimistic concurrency and blob storage.
/// </summary>
public class VersionAndBlobSqlTests
{
    [Fact]
    public void GenerateCreateTableSql_IncludesVersionColumn()
    {
        var sql = SqlGenerator.GenerateCreateTableSql("Person");

        Assert.Contains("version INTEGER NOT NULL DEFAULT 0", sql);
    }

    [Fact]
    public void GenerateUpsertSql_StartsVersionAtOneAndIncrementsOnConflict()
    {
        var sql = SqlGenerator.GenerateUpsertSql("Person");

        Assert.Contains("(id, data, version)", sql);
        Assert.Contains("jsonb(@Data), 1", sql);
        Assert.Contains("version = version + 1", sql);
    }

    [Fact]
    public void GenerateBulkUpsertSql_StartsVersionAtOneAndIncrementsOnConflict()
    {
        var sql = SqlGenerator.GenerateBulkUpsertSql("Person", 2);

        Assert.Contains("(id, data, version)", sql);
        Assert.Contains("(@Id0, jsonb(@Data0), 1)", sql);
        Assert.Contains("(@Id1, jsonb(@Data1), 1)", sql);
        Assert.Contains("version = version + 1", sql);
    }

    [Fact]
    public void GenerateInsertIfAbsentSql_UsesDoNothingOnConflict()
    {
        var sql = SqlGenerator.GenerateInsertIfAbsentSql("Person");

        Assert.Contains("ON CONFLICT(id) DO NOTHING", sql);
        Assert.Contains("jsonb(@Data), 1", sql);
    }

    [Fact]
    public void GenerateVersionedUpdateSql_GuardsOnIdAndVersion()
    {
        var sql = SqlGenerator.GenerateVersionedUpdateSql("Person");

        Assert.Contains("WHERE id = @Id AND version = @ExpectedVersion", sql);
        Assert.Contains("version = version + 1", sql);
    }

    [Fact]
    public void GenerateGetWithVersionSql_SelectsJsonDataAndVersion()
    {
        var sql = SqlGenerator.GenerateGetWithVersionSql("Person");

        Assert.Equal("SELECT json(data) as data, version FROM [Person] WHERE id = @Id", sql);
    }

    [Fact]
    public void GenerateBlobSql_TargetsReservedBlobTable()
    {
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", SqlGenerator.GenerateCreateBlobTableSql());
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", SqlGenerator.GeneratePutBlobSql());
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", SqlGenerator.GenerateGetBlobSql());
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", SqlGenerator.GenerateDeleteBlobSql());
        Assert.Contains($"[{SqlGenerator.BlobTableName}]", SqlGenerator.GenerateBlobExistsSql());
    }

    [Fact]
    public void GeneratePutBlobSql_StoresRawBytesWithoutJsonbConversion()
    {
        var sql = SqlGenerator.GeneratePutBlobSql();

        Assert.DoesNotContain("jsonb(", sql);
        Assert.Contains("ON CONFLICT(id) DO UPDATE", sql);
    }
}
