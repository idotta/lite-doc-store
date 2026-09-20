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
        var migration = new SqlMigration(
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
        var ex = Assert.Throws<ArgumentException>(() => new SqlMigration(
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
            new SqlMigration(1, null!, "SELECT 1", "SELECT 2"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullUpSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SqlMigration(1, "Test", null!, "SELECT 2"));

        Assert.Equal("upSql", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullDownSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SqlMigration(1, "Test", "SELECT 1", null!));

        Assert.Equal("downSql", ex.ParamName);
    }

    [Fact]
    public void Migration_Checksum_IsAStableUppercaseSha256OfTheUpSql()
    {
        var first = new SqlMigration(1, "Test", "CREATE TABLE T (id TEXT)", "DROP TABLE T");
        var second = new SqlMigration(9, "Other", "CREATE TABLE T (id TEXT)", "SELECT 1");

        // 64 uppercase hex characters, and independent of version, name and down SQL.
        Assert.Equal(64, first.Checksum.Length);
        Assert.Equal(first.Checksum.ToUpperInvariant(), first.Checksum);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public void Migration_Checksum_ChangesWithTheUpSql()
    {
        var original = new SqlMigration(1, "Test", "CREATE TABLE T (id TEXT)", "DROP TABLE T");
        var edited = new SqlMigration(1, "Test", "CREATE TABLE T (id TEXT, extra TEXT)", "DROP TABLE T");

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

    [Fact]
    public void Checksum_WhenSubclassOverridesUpAsyncOnly_StillReportsTheBaseDigest()
    {
        // The documented limit: nothing detects the omission, so the interface read keeps
        // describing the constructor's SQL while a different migration body runs.
        IMigration migration = new UncoveredMigration();
        var baseline = new SqlMigration(1, "Uncovered", "CREATE TABLE T (id TEXT)", "DROP TABLE T");

        Assert.Equal(baseline.Checksum, migration.Checksum);
    }

    [Fact]
    public void Checksum_WhenSubclassHidesItWithNew_InterfaceStillReportsTheBaseDigest()
    {
        // Pins the trap both CLAUDE.md and docs/DESIGN-RATIONALE.md document: `new`-hiding
        // compiles and reads correctly off the concrete type, but the runner reads through
        // IMigration, and interface dispatch lands on the base. Without this test the doc claim
        // could rot silently — the compiler never complains, and nothing else exercises the shape.
        var hiding = new HidingMigration();
        var baseline = new SqlMigration(1, "Hiding", "CREATE TABLE T (id TEXT)", "DROP TABLE T");

        // Not vacuous: the hiding member really is reachable, just not through the interface.
        Assert.Equal("HIDDEN", hiding.Checksum);
        Assert.Equal(baseline.Checksum, ((IMigration)hiding).Checksum);
    }

    [Fact]
    public void Checksum_WhenSubclassOverridesChecksum_ReportsTheOverrideThroughTheInterface()
    {
        IMigration migration = new CoveredMigration("OVERRIDDEN");

        Assert.Equal("OVERRIDDEN", migration.Checksum);
    }

    [Fact]
    public void Checksum_WhenSubclassOverrideReturnsNull_ReportsNullThroughTheInterface()
    {
        IMigration migration = new OptedOutMigration();

        Assert.Null(migration.Checksum);
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

    /// <summary>
    /// Overrides <see cref="SqlMigration.UpAsync"/> and changes what runs, without covering
    /// <see cref="SqlMigration.Checksum"/> — the shape the checksum docs warn about.
    /// </summary>
    private sealed class UncoveredMigration()
        : SqlMigration(1, "Uncovered", "CREATE TABLE T (id TEXT)", "DROP TABLE T")
    {
        public override Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Hides <see cref="SqlMigration.Checksum"/> with <c>new</c> instead of overriding it — the shape
    /// the docs call out as silently ignored, kept here so that claim stays tested.
    /// </summary>
    private sealed class HidingMigration()
        : SqlMigration(1, "Hiding", "CREATE TABLE T (id TEXT)", "DROP TABLE T")
    {
        public new string Checksum => "HIDDEN";
    }

    /// <summary>
    /// Overrides both, which is what the docs require of a subclass that changes what runs.
    /// </summary>
    private sealed class CoveredMigration(string checksum)
        : SqlMigration(1, "Covered", "CREATE TABLE T (id TEXT)", "DROP TABLE T")
    {
        public override string Checksum => checksum;

        public override Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Opts out of verification. The declared type is non-nullable, so the opt-out is written
    /// <c>null!</c>.
    /// </summary>
    private sealed class OptedOutMigration()
        : SqlMigration(1, "OptedOut", "CREATE TABLE T (id TEXT)", "DROP TABLE T")
    {
        public override string Checksum => null!;
    }
}
