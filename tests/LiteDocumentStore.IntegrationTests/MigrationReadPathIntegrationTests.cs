using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// <see cref="IDocumentStore.GetAppliedMigrationsAsync"/> and
/// <see cref="IDocumentStore.GetCurrentMigrationVersionAsync"/> are documented as reads, and they
/// behave like reads through the public surface too: a store that never migrates is not given a
/// history table by calling them, and a legacy three-column table is not upgraded under a write
/// lock behind the caller's back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationReadPathIntegrationTests : IAsyncLifetime
{
    private const string TableName = "__store_migrations";

    private IDocumentStore _store = null!;

    public async Task InitializeAsync() =>
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private Task<long> ScalarAsync(string sql) =>
        _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        });

    private async Task<bool> HistoryTableExistsAsync() =>
        await ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'") == 1;

    private async Task<bool> ChecksumColumnExistsAsync() =>
        await ScalarAsync($"SELECT COUNT(*) FROM pragma_table_info('{TableName}') WHERE name = 'checksum'") == 1;

    private Task<int> CreateLegacyHistoryTableAsync() =>
        _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $@"
                CREATE TABLE [{TableName}] (
                    version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
                INSERT INTO [{TableName}] VALUES (7, 'seven', '2026-01-01T00:00:00.0000000+00:00');";
            return await command.ExecuteNonQueryAsync(ct);
        });

    [Fact]
    public async Task BothReads_OnAStoreThatNeverMigrates_LeaveTheHistoryTableAbsent()
    {
        Assert.Equal(0, await _store.GetCurrentMigrationVersionAsync());
        Assert.Empty(await _store.GetAppliedMigrationsAsync());

        Assert.False(await HistoryTableExistsAsync());
    }

    [Fact]
    public async Task BothReads_OnALegacyHistoryTable_LeaveTheChecksumColumnAbsent()
    {
        await CreateLegacyHistoryTableAsync();

        Assert.Equal(7, await _store.GetCurrentMigrationVersionAsync());
        var record = Assert.Single(await _store.GetAppliedMigrationsAsync());
        Assert.Equal(7, record.Version);
        Assert.Null(record.Checksum);

        Assert.False(await ChecksumColumnExistsAsync());
    }

    [Fact]
    public async Task MigrateAsync_StillCreatesTheHistoryTable()
    {
        await _store.MigrateAsync([new Migration(1, "one", "CREATE TABLE t1 (x)", "DROP TABLE t1")]);

        Assert.True(await HistoryTableExistsAsync());
        Assert.True(await ChecksumColumnExistsAsync());
        Assert.Equal(1, await _store.GetCurrentMigrationVersionAsync());
    }

    [Fact]
    public async Task MigrateAsync_StillUpgradesALegacyHistoryTable()
    {
        await CreateLegacyHistoryTableAsync();

        await _store.MigrateAsync([new Migration(8, "eight", "CREATE TABLE t8 (x)", "DROP TABLE t8")]);

        Assert.True(await ChecksumColumnExistsAsync());
        Assert.Equal(8, await _store.GetCurrentMigrationVersionAsync());
    }
}
