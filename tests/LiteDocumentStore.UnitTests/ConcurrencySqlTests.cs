using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the SQL shapes and exception surface behind version-checked writes and
/// deletes, and for the id column the null-document guard depends on.
/// </summary>
[Trait("Category", "Unit")]
public class ConcurrencySqlTests
{
    [Fact]
    public void GenerateInsertIfAbsentSql_ReturnsTheStoredVersion()
    {
        var sql = SqlGenerator.GenerateInsertIfAbsentSql("Person");

        Assert.Contains("ON CONFLICT(id) DO NOTHING", sql);
        Assert.Contains("RETURNING version", sql);
    }

    [Fact]
    public void GenerateVersionedUpdateSql_GuardsOnVersionAndReturnsTheNewOne()
    {
        var sql = SqlGenerator.GenerateVersionedUpdateSql("Person");

        Assert.Contains("WHERE id = @Id AND version = @ExpectedVersion", sql);
        Assert.Contains("version = version + 1", sql);
        Assert.Contains("RETURNING version", sql);
    }

    [Fact]
    public void GenerateVersionedDeleteSql_GuardsOnVersion()
    {
        var sql = SqlGenerator.GenerateVersionedDeleteSql("Person");

        Assert.Equal("DELETE FROM [Person] WHERE id = @Id AND version = @ExpectedVersion", sql);
    }

    [Fact]
    public void GenerateGetVersionSql_SelectsOnlyTheVersion()
    {
        var sql = SqlGenerator.GenerateGetVersionSql("Person");

        Assert.Equal("SELECT version FROM [Person] WHERE id = @Id", sql);
    }

    [Theory]
    [InlineData("robert'); DROP TABLE Person; --")]
    [InlineData("Person]")]
    public void VersionGenerators_WithAnInvalidIdentifier_Throw(string tableName)
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateVersionedDeleteSql(tableName));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateGetVersionSql(tableName));
    }

    [Fact]
    public void GenerateGetAllSql_SelectsTheIdAlongsideTheDocument()
    {
        var sql = SqlGenerator.GenerateGetAllSql("Person");

        Assert.Equal("SELECT id, json(data) as data FROM [Person]", sql);
    }

    [Fact]
    public void GenerateQueryByJsonPathSql_SelectsTheIdAlongsideTheDocument()
    {
        var sql = SqlGenerator.GenerateQueryByJsonPathSql("Person", "$.Email");

        Assert.StartsWith("SELECT id, json(data) as data FROM [Person]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrencyException_CarriesBothVersionsAndTheKind()
    {
        var exception = new ConcurrencyException(
            "conflict", "abc", "Person", expectedVersion: 3, actualVersion: 7,
            ConcurrencyConflictKind.VersionMismatch);

        Assert.Equal("abc", exception.DocumentId);
        Assert.Equal("Person", exception.TableName);
        Assert.Equal(3, exception.ExpectedVersion);
        Assert.Equal(7, exception.ActualVersion);
        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, exception.Kind);
    }

    [Fact]
    public void ConcurrencyException_WithoutConflictDetail_LeavesTheVersionsNull()
    {
        var exception = new ConcurrencyException("conflict", "abc", "Person");

        Assert.Null(exception.ExpectedVersion);
        Assert.Null(exception.ActualVersion);
        Assert.Equal(ConcurrencyConflictKind.Unspecified, exception.Kind);
    }

    [Fact]
    public void SerializationException_WithTargetTypeOnly_KeepsTheType()
    {
        var exception = new SerializationException("null document", typeof(string));

        Assert.Equal(typeof(string), exception.TargetType);
        Assert.Null(exception.InnerException);
    }
}
