using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Validation of the three inputs <see cref="SqlGenerator"/> must interpolate rather than
/// bind: identifiers, JSON paths and column types.
/// </summary>
[Trait("Category", "Unit")]
public class SqlGeneratorValidationTests
{
    [Theory]
    [InlineData("Person")]
    [InlineData("_private")]
    [InlineData("__store_blobs")]
    [InlineData("Order2")]
    public void ValidIdentifiers_AreAccepted(string tableName)
    {
        var sql = SqlGenerator.GenerateGetByIdSql(tableName);

        Assert.Contains($"[{tableName}]", sql);
    }

    [Theory]
    [InlineData("Person]; DROP TABLE Person; --")]  // ] closes the bracket quoting
    [InlineData("Person Two")]
    [InlineData("2Fast")]
    [InlineData("Person\"")]
    [InlineData("")]
    public void InvalidIdentifiers_ThrowArgumentException(string tableName)
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateGetByIdSql(tableName));
    }

    [Fact]
    public void InvalidIdentifier_IsRejectedOnEveryInterpolatingGenerator()
    {
        const string Injected = "x] ON [Person] (id); --";

        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateCreateTableSql(Injected));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateUpsertSql(Injected));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkUpsertSql(Injected, 2));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkDeleteSql(Injected, 2));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateCountSql(Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", Injected, "$.Email"));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateColumnIndexSql("Person", "idx", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", Injected, "$.Email"));
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$.Email")]
    [InlineData("$.Address.City")]
    [InlineData("$.Tags[0]")]
    [InlineData("$.Orders[12].Total")]
    [InlineData("$._internal")]
    public void ValidJsonPaths_AreAccepted(string jsonPath)
    {
        var sql = SqlGenerator.GenerateQueryByJsonPathSql("Person", jsonPath);

        Assert.Contains($"json_extract(data, '{jsonPath}')", sql);
    }

    [Theory]
    [InlineData("$.a') = 1 OR 1=1 --")]  // the historical injection: ' closes the literal
    [InlineData("$.Email'")]
    [InlineData("Email")]                // no leading $
    [InlineData("$.")]
    [InlineData("$.Email.")]
    [InlineData("$.Tags[]")]
    [InlineData("$.Tags[a]")]
    [InlineData("$.Tags[0")]
    [InlineData("$..Email")]
    [InlineData("$ .Email")]
    [InlineData("")]
    public void InvalidJsonPaths_ThrowArgumentException(string jsonPath)
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQueryByJsonPathSql("Person", jsonPath));
    }

    [Fact]
    public void InvalidJsonPath_IsRejectedByTheIndexAndVirtualColumnGenerators()
    {
        const string Injected = "$.a') = 1 OR 1=1 --";

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx", ["$.Email", Injected]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", Injected));
    }

    [Theory]
    [InlineData("TEXT", "TEXT")]
    [InlineData("integer", "INTEGER")]
    [InlineData("Real", "REAL")]
    [InlineData("blob", "BLOB")]
    [InlineData("NUMERIC", "NUMERIC")]
    public void ColumnTypes_AreWhitelistedAndCanonicalized(string requested, string expected)
    {
        var sql = SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", "$.Email", requested);

        Assert.Contains($"[email] {expected} GENERATED ALWAYS AS", sql);
    }

    [Theory]
    [InlineData("TEXT DEFAULT 'x'")]
    [InlineData("TEXT, dropped INTEGER")]
    [InlineData("VARCHAR(255)")]
    [InlineData("")]
    public void UnsupportedColumnTypes_ThrowArgumentException(string columnType)
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", "$.Email", columnType));
    }

    // The bare root is grammatically valid and stays accepted by default — a read path only
    // extracts the whole document through it. Only a patch opts out, since there the root
    // rewrites or deletes the document instead of reading it.

    [Fact]
    public void TheDocumentRoot_IsAcceptedWhenRootIsAllowed()
    {
        Assert.Equal("$", SqlGenerator.ValidateJsonPath("$", "jsonPath"));
    }

    [Fact]
    public void TheDocumentRoot_IsRejectedWhenRootIsNotAllowed()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$", "jsonPath", allowRoot: false));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    [Theory]
    [InlineData("$[0]")]
    [InlineData("$.Email")]
    public void APathBelowTheRoot_IsAcceptedEitherWay(string jsonPath)
    {
        Assert.Equal(jsonPath, SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));
        Assert.Equal(jsonPath, SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false));
    }
}
