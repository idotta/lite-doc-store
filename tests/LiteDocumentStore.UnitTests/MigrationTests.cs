using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

[Trait("Category", "Unit")]
public class MigrationTests
{
    [Fact]
    public void Migration_Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange & Act
        var migration = new Migration(
            version: 20260109001,
            name: "CreateCustomerTable",
            upSql: "CREATE TABLE Customer (id TEXT PRIMARY KEY)",
            downSql: "DROP TABLE Customer");

        // Assert
        Assert.Equal(20260109001, migration.Version);
        Assert.Equal("CreateCustomerTable", migration.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Migration_Constructor_WithInvalidVersion_ThrowsArgumentException(long version)
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => new Migration(
            version,
            "TestMigration",
            "SELECT 1",
            "SELECT 2"));

        Assert.Contains("Version must be greater than zero", ex.Message);
    }

    [Fact]
    public void Migration_Constructor_WithNullName_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, null!, "SELECT 1", "SELECT 2"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullUpSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, "Test", null!, "SELECT 2"));

        Assert.Equal("upSql", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullDownSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, "Test", "SELECT 1", null!));

        Assert.Equal("downSql", ex.ParamName);
    }

    [Fact]
    public void Migration_Checksum_IsAStableUppercaseSha256OfTheUpSql()
    {
        var first = new Migration(1, "Test", "CREATE TABLE T (id TEXT)", "DROP TABLE T");
        var second = new Migration(9, "Other", "CREATE TABLE T (id TEXT)", "SELECT 1");

        // 64 uppercase hex characters, and independent of version, name and down SQL.
        Assert.Equal(64, first.Checksum.Length);
        Assert.Equal(first.Checksum.ToUpperInvariant(), first.Checksum);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Migration_Checksum_ChangesWithTheUpSql()
    {
        var original = new Migration(1, "Test", "CREATE TABLE T (id TEXT)", "DROP TABLE T");
        var edited = new Migration(1, "Test", "CREATE TABLE T (id TEXT, extra TEXT)", "DROP TABLE T");

        Assert.NotEqual(original.Checksum, edited.Checksum);
    }

    [Fact]
    public void IMigration_Checksum_DefaultsToNull()
    {
        IMigration migration = new CodeMigration();

        Assert.Null(migration.Checksum);
    }

    [Fact]
    public void MigrationOptions_Defaults_RejectOutOfOrderAndVerifyChecksums()
    {
        var options = new MigrationOptions();

        Assert.False(options.AllowOutOfOrder);
        Assert.True(options.VerifyChecksums);
        Assert.False(MigrationOptions.Default.AllowOutOfOrder);
        Assert.True(MigrationOptions.Default.VerifyChecksums);
    }

    [Fact]
    public void MigrationOutOfOrderException_CarriesTheRejectedVersionAndCurrentVersion()
    {
        var ex = new MigrationOutOfOrderException(2, "AddPrice", 5);

        Assert.Equal(2, ex.Version);
        Assert.Equal("AddPrice", ex.Name);
        Assert.Equal(5, ex.CurrentVersion);
        Assert.Contains("AllowOutOfOrder", ex.Message);
        Assert.IsAssignableFrom<LiteDocumentStoreException>(ex);
    }

    [Fact]
    public void MigrationChecksumMismatchException_CarriesBothChecksums()
    {
        var ex = new MigrationChecksumMismatchException(3, "Seed", "STORED", "SUPPLIED");

        Assert.Equal(3, ex.Version);
        Assert.Equal("Seed", ex.Name);
        Assert.Equal("STORED", ex.ExpectedChecksum);
        Assert.Equal("SUPPLIED", ex.ActualChecksum);
        Assert.IsAssignableFrom<LiteDocumentStoreException>(ex);
    }

    /// <summary>
    /// A hand-written migration that implements the interface directly, so it takes the default
    /// <see cref="IMigration.Checksum"/>.
    /// </summary>
    private sealed class CodeMigration : IMigration
    {
        public long Version => 1;

        public string Name => "Code";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
