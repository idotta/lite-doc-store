using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

[Trait("Category", "Integration")]
public class MigrationIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private Task<bool> TableExistsAsync(string tableName) =>
        _store.ExecuteRawAsync((connection, ct) =>
            new SchemaIntrospector(connection).TableExistsAsync(tableName, ct));

    private static Migration CreateTable(long version, string tableName) =>
        new(version,
            $"Create{tableName}",
            $"CREATE TABLE {tableName} (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            $"DROP TABLE {tableName}");

    [Fact]
    public async Task MigrateAsync_WithNewMigration_AppliesIt()
    {
        var applied = await _store.MigrateAsync([CreateTable(1, "Product")]);

        Assert.Equal(1, applied);
        Assert.Equal(1, await _store.GetCurrentMigrationVersionAsync());
        Assert.True(await TableExistsAsync("Product"));
    }

    [Fact]
    public async Task MigrateAsync_WithAlreadyAppliedMigration_AppliesNothing()
    {
        var migration = CreateTable(1, "Product");
        await _store.MigrateAsync([migration]);

        var applied = await _store.MigrateAsync([migration]);

        Assert.Equal(0, applied);
        Assert.Single(await _store.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task MigrateAsync_WithUnorderedInput_AppliesInAscendingVersionOrder()
    {
        // The second migration depends on the table the first creates, so an out-of-order run
        // would fail rather than merely reorder history.
        var first = CreateTable(1, "Product");
        var second = new Migration(
            2, "AddProductPrice", "ALTER TABLE Product ADD COLUMN price REAL", "SELECT 1");

        var applied = await _store.MigrateAsync([second, first]);

        Assert.Equal(2, applied);
        var versions = (await _store.GetAppliedMigrationsAsync()).Select(m => m.Version).ToList();
        Assert.Equal([1L, 2L], versions);
    }

    [Fact]
    public async Task MigrateAsync_WithBackFilledVersion_ThrowsAndAppliesNothing()
    {
        await _store.MigrateAsync([CreateTable(1, "T1"), CreateTable(3, "T3")]);

        // v2 was never applied but sits below the current version: the old runner skipped it
        // silently, reporting the same "false" as an already-applied migration.
        var backFilled = CreateTable(2, "ShouldNotExist");

        var ex = await Assert.ThrowsAsync<MigrationOutOfOrderException>(
            () => _store.MigrateAsync([backFilled]));

        Assert.Equal(2, ex.Version);
        Assert.Equal("CreateShouldNotExist", ex.Name);
        Assert.Equal(3, ex.CurrentVersion);
        Assert.False(await TableExistsAsync("ShouldNotExist"));
        Assert.Equal(
            new long[] { 1, 3 },
            (await _store.GetAppliedMigrationsAsync()).Select(m => m.Version).ToList());
    }

    [Fact]
    public async Task MigrateAsync_WithBackFilledVersion_AndAllowOutOfOrder_AppliesIt()
    {
        await _store.MigrateAsync([CreateTable(1, "T1"), CreateTable(3, "T3")]);

        var applied = await _store.MigrateAsync(
            [CreateTable(2, "T2")],
            new MigrationOptions { AllowOutOfOrder = true });

        Assert.Equal(1, applied);
        Assert.True(await TableExistsAsync("T2"));
        Assert.Equal(
            new long[] { 1, 2, 3 },
            (await _store.GetAppliedMigrationsAsync()).Select(m => m.Version).ToList());
        // The back-fill does not move the current version.
        Assert.Equal(3, await _store.GetCurrentMigrationVersionAsync());
    }

    [Fact]
    public async Task MigrateAsync_WithEditedMigration_ThrowsChecksumMismatch()
    {
        var original = new Migration(1, "Seed", "CREATE TABLE Seeded (id TEXT PRIMARY KEY)", "DROP TABLE Seeded");
        await _store.MigrateAsync([original]);

        var edited = new Migration(1, "Seed", "CREATE TABLE Seeded (id TEXT PRIMARY KEY, extra TEXT)", "DROP TABLE Seeded");

        var ex = await Assert.ThrowsAsync<MigrationChecksumMismatchException>(
            () => _store.MigrateAsync([edited]));

        Assert.Equal(1, ex.Version);
        Assert.Equal(original.Checksum, ex.ExpectedChecksum);
        Assert.Equal(edited.Checksum, ex.ActualChecksum);
    }

    [Fact]
    public async Task MigrateAsync_WithEditedDownSql_IsAccepted()
    {
        // Only the up SQL is checksummed: the down SQL is not part of what was applied.
        await _store.MigrateAsync([new Migration(1, "Seed", "SELECT 1", "SELECT 1")]);

        var applied = await _store.MigrateAsync([new Migration(1, "Seed", "SELECT 1", "SELECT 2")]);

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task MigrateAsync_WithEditedMigration_AndVerifyChecksumsDisabled_IsAccepted()
    {
        await _store.MigrateAsync([new Migration(1, "Seed", "SELECT 1", "SELECT 1")]);

        var applied = await _store.MigrateAsync(
            [new Migration(1, "Seed", "SELECT 2", "SELECT 1")],
            new MigrationOptions { VerifyChecksums = false });

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task GetAppliedMigrationsAsync_ExposesTheRecordedChecksum()
    {
        var migration = CreateTable(1, "Product");
        var before = DateTimeOffset.UtcNow;

        await _store.MigrateAsync([migration]);
        var after = DateTimeOffset.UtcNow;

        var record = Assert.Single(await _store.GetAppliedMigrationsAsync());
        Assert.Equal(migration.Checksum, record.Checksum);
        Assert.Equal("CreateProduct", record.Name);
        Assert.InRange(record.AppliedAt, before, after);
    }

    [Fact]
    public async Task MigrateAsync_OverALegacyHistoryTable_AddsTheChecksumColumn()
    {
        // A history table written before checksums existed: three columns, one row.
        await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE [__store_migrations] (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                INSERT INTO [__store_migrations] (version, name, applied_at)
                VALUES (1, 'Legacy', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(ct);
        });

        var applied = await _store.MigrateAsync([CreateTable(2, "Product")]);

        Assert.Equal(1, applied);
        var records = (await _store.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(2, records.Count);
        Assert.Null(records[0].Checksum);
        Assert.NotNull(records[1].Checksum);
    }

    [Fact]
    public async Task MigrateAsync_OverALegacyHistoryRow_SkipsChecksumVerification()
    {
        await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE [__store_migrations] (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                INSERT INTO [__store_migrations] (version, name, applied_at)
                VALUES (1, 'Legacy', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(ct);
        });

        // The stored checksum is null, so there is nothing to compare against.
        var applied = await _store.MigrateAsync([new Migration(1, "Legacy", "SELECT 1", "SELECT 1")]);

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task MigrateAsync_WithDuplicateVersions_ThrowsBeforeApplyingAnything()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _store.MigrateAsync(
            [CreateTable(1, "First"), CreateTable(1, "Second")]));

        Assert.Equal("migrations", ex.ParamName);
        Assert.Contains("Duplicate migration version 1", ex.Message);
        Assert.False(await TableExistsAsync("First"));
        Assert.Empty(await _store.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task MigrateAsync_WithANullElement_ThrowsBeforeApplyingAnything()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _store.MigrateAsync(
            [CreateTable(1, "First"), null!]));

        Assert.Equal("migrations", ex.ParamName);
        Assert.False(await TableExistsAsync("First"));
    }

    [Fact]
    public async Task MigrateAsync_WithNullOptions_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.MigrateAsync([CreateTable(1, "Product")], null!));
    }

    [Fact]
    public async Task MigrateAsync_WithFailingMigration_KeepsEarlierMigrationsApplied()
    {
        var good = CreateTable(1, "Product");
        var bad = new Migration(2, "Broken", "CREATE TABLE Invalid (,,,)", "SELECT 1");

        await Assert.ThrowsAsync<SqliteException>(() => _store.MigrateAsync([good, bad]));

        // Transactions are per migration: the first stays applied, the second recorded nothing.
        Assert.Equal(1, await _store.GetCurrentMigrationVersionAsync());
        Assert.True(await TableExistsAsync("Product"));
    }

    [Fact]
    public async Task MigrateAsync_WithFailingMigration_RecordsNothingForIt()
    {
        var bad = new Migration(
            1,
            "Broken",
            "CREATE TABLE Product (id TEXT PRIMARY KEY); CREATE TABLE Invalid (,,,);",
            "DROP TABLE Product");

        await Assert.ThrowsAsync<SqliteException>(() => _store.MigrateAsync([bad]));

        Assert.Equal(0, await _store.GetCurrentMigrationVersionAsync());
        Assert.False(await TableExistsAsync("Product"));
    }

    [Fact]
    public async Task GetCurrentMigrationVersionAsync_WithNoMigrations_ReturnsZero()
    {
        Assert.Equal(0, await _store.GetCurrentMigrationVersionAsync());
        Assert.Empty(await _store.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task RollbackToVersionAsync_RollsBackNewestFirst()
    {
        IMigration[] migrations = [CreateTable(1, "T1"), CreateTable(2, "T2"), CreateTable(3, "T3")];
        await _store.MigrateAsync(migrations);

        var rolledBack = await _store.RollbackToVersionAsync(1, migrations);

        Assert.Equal(2, rolledBack);
        Assert.Equal(1, await _store.GetCurrentMigrationVersionAsync());
        Assert.True(await TableExistsAsync("T1"));
        Assert.False(await TableExistsAsync("T2"));
        Assert.False(await TableExistsAsync("T3"));
    }

    [Fact]
    public async Task RollbackToVersionAsync_ToZero_RollsBackEverything()
    {
        IMigration[] migrations = [CreateTable(1, "T1"), CreateTable(2, "T2")];
        await _store.MigrateAsync(migrations);

        var rolledBack = await _store.RollbackToVersionAsync(0, migrations);

        Assert.Equal(2, rolledBack);
        Assert.Empty(await _store.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task RollbackToVersionAsync_AboveCurrentVersion_RollsBackNothing()
    {
        IMigration[] migrations = [CreateTable(1, "T1")];
        await _store.MigrateAsync(migrations);

        Assert.Equal(0, await _store.RollbackToVersionAsync(5, migrations));
        Assert.True(await TableExistsAsync("T1"));
    }

    [Fact]
    public async Task RollbackToVersionAsync_WithMissingDefinition_ThrowsAndRollsBackNothing()
    {
        var v1 = CreateTable(1, "T1");
        var v2 = CreateTable(2, "T2");
        await _store.MigrateAsync([v1, v2]);

        await Assert.ThrowsAsync<LiteDocumentStoreException>(
            () => _store.RollbackToVersionAsync(0, [v2]));

        Assert.True(await TableExistsAsync("T1"));
        Assert.True(await TableExistsAsync("T2"));
        Assert.Equal(2, await _store.GetCurrentMigrationVersionAsync());
    }

    [Fact]
    public async Task RollbackToVersionAsync_WithNegativeTarget_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.RollbackToVersionAsync(-1, [CreateTable(1, "T1")]));
    }

    [Fact]
    public async Task RollbackToVersionAsync_IgnoresChecksumDrift()
    {
        await _store.MigrateAsync([CreateTable(1, "T1")]);

        // Rollback never verifies checksums, so an edited definition still reverts.
        var edited = new Migration(
            1, "CreateT1", "CREATE TABLE T1 (id TEXT PRIMARY KEY, extra TEXT)", "DROP TABLE T1");

        Assert.Equal(1, await _store.RollbackToVersionAsync(0, [edited]));
        Assert.False(await TableExistsAsync("T1"));
    }

    [Fact]
    public async Task MigrateAsync_FromTwoStoresAtOnce_AppliesTheMigrationExactlyOnce()
    {
        // A shared-cache in-memory database locks at table granularity, so only a file database
        // exercises the write lock two processes actually contend for.
        var path = Path.Combine(Path.GetTempPath(), $"lds-migrate-{Guid.NewGuid():N}.db");
        var options = DocumentStoreOptions.ForFile(path);
        options.BusyTimeoutMs = 30_000;

        var storeA = await new DocumentStoreFactory().CreateAsync(options);
        var storeB = await new DocumentStoreFactory().CreateAsync(options);

        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var gated = new GatedMigration(1, "CreateProduct",
                "CREATE TABLE Product (id TEXT PRIMARY KEY)", entered, release.Task);
            var plain = new Migration(1, "CreateProduct",
                "CREATE TABLE Product (id TEXT PRIMARY KEY)", "DROP TABLE Product");

            // A holds the write lock inside UpAsync; B then contends for it. A one-way gate, not
            // a barrier: B cannot reach UpAsync until A commits, so waiting on B here would hang.
            var first = storeA.MigrateAsync([gated]);

            // `entered` only completes from inside UpAsync, so a failure before that point
            // would park this await forever. Surface the fault instead of hanging the run.
            if (await Task.WhenAny(entered.Task, first) == first)
            {
                await first;
                Assert.Fail("The gated migration finished without entering UpAsync.");
            }

            var second = Task.Run(() => storeB.MigrateAsync([plain]));
            await Task.Delay(250);

            // B is parked on the write lock A holds, so it cannot have decided anything yet.
            Assert.False(second.IsCompleted);
            release.SetResult();

            var counts = await Task.WhenAll(first, second);

            Assert.Equal(1, counts[0]);
            Assert.Equal(0, counts[1]);
            Assert.Single(await storeA.GetAppliedMigrationsAsync());
        }
        finally
        {
            await storeA.DisposeAsync();
            await storeB.DisposeAsync();
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A file still held by the OS is a test-cleanup concern only.
            }
        }
    }

    /// <summary>
    /// A migration that parks inside <c>UpAsync</c> — with the write transaction open — until the
    /// test releases it.
    /// </summary>
    private sealed class GatedMigration(
        long version,
        string name,
        string upSql,
        TaskCompletionSource entered,
        Task release)
        : Migration(version, name, upSql, "SELECT 1")
    {
        public override async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release;
            await base.UpAsync(connection, cancellationToken);
        }
    }
}
