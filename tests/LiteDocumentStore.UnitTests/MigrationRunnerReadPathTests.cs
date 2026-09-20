using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The two documented reads answer from whatever the database already holds: they neither create
/// the history table nor upgrade a legacy three-column one. The apply path still does both.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MigrationRunnerReadPathTests : IDisposable
{
    private const string TableName = "__store_migrations";

    private readonly SqliteConnection _connection =
        new($"Data Source=migration-read-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    public MigrationRunnerReadPathTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private bool TableExists() =>
        Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'") == 1;

    private bool ChecksumColumnExists() =>
        Scalar($"SELECT COUNT(*) FROM pragma_table_info('{TableName}') WHERE name = 'checksum'") == 1;

    private void CreateLegacyTable()
    {
        Execute($@"CREATE TABLE [{TableName}] (
            version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)");
        Execute($"INSERT INTO [{TableName}] VALUES (7, 'seven', '2026-01-01T00:00:00.0000000+00:00')");
    }

    [Fact]
    public async Task GetCurrentVersionAsync_WithTheHistoryTableAbsent_AnswersZeroWithoutCreatingIt()
    {
        var runner = new MigrationRunner(_connection);

        Assert.Equal(0, await runner.GetCurrentVersionAsync());
        Assert.False(TableExists());
    }

    [Fact]
    public async Task GetAppliedMigrationsAsync_WithTheHistoryTableAbsent_AnswersEmptyWithoutCreatingIt()
    {
        var runner = new MigrationRunner(_connection);

        Assert.Empty(await runner.GetAppliedMigrationsAsync());
        Assert.False(TableExists());
    }

    [Fact]
    public async Task GetCurrentVersionAsync_WithALegacyTable_AnswersWithoutAddingTheChecksumColumn()
    {
        CreateLegacyTable();
        var runner = new MigrationRunner(_connection);

        Assert.Equal(7, await runner.GetCurrentVersionAsync());
        Assert.False(ChecksumColumnExists());
    }

    [Fact]
    public async Task GetAppliedMigrationsAsync_WithALegacyTable_ProjectsANullChecksumInstead()
    {
        CreateLegacyTable();
        var runner = new MigrationRunner(_connection);

        var record = Assert.Single(await runner.GetAppliedMigrationsAsync());

        Assert.Equal(7, record.Version);
        Assert.Equal("seven", record.Name);
        Assert.Null(record.Checksum);
        Assert.False(ChecksumColumnExists());
    }

    [Fact]
    public async Task ApplyMigrationsAsync_StillCreatesTheHistoryTable()
    {
        var runner = new MigrationRunner(_connection);

        var applied = await runner.ApplyMigrationsAsync(
            [new SqlMigration(1, "one", "CREATE TABLE t1 (x)", "DROP TABLE t1")],
            new MigrationOptions());

        Assert.Equal(1, applied);
        Assert.True(TableExists());
        Assert.True(ChecksumColumnExists());
    }

    [Fact]
    public async Task ApplyMigrationsAsync_StillUpgradesALegacyTable()
    {
        CreateLegacyTable();
        var runner = new MigrationRunner(_connection);

        await runner.ApplyMigrationsAsync(
            [new SqlMigration(8, "eight", "CREATE TABLE t8 (x)", "DROP TABLE t8")],
            new MigrationOptions());

        Assert.True(ChecksumColumnExists());
    }
}
