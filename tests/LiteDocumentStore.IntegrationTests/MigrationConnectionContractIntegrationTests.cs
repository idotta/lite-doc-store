using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Characterization tests for the contract <see cref="IMigration.UpAsync"/> documents: the
/// connection arrives inside the runner's <c>BEGIN IMMEDIATE</c> transaction, and it is the whole
/// run's only pooled connection.
/// </summary>
/// <remarks>
/// These pin promises the XML docs now make to migration authors, so they fail if the shape ever
/// changes silently. A real file database throughout — the write lock and the pool are the subject.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MigrationConnectionContractIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private DocumentStoreOptions FileOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-migration-contract-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={path};Pooling=False",
            EnableWalMode = true,
            PageSize = 0,
            BusyTimeoutMs = 1000,
        };
    }

    private sealed class LambdaMigration(long version, string name, Func<SqliteConnection, CancellationToken, Task> up)
        : IMigration
    {
        public long Version => version;

        public string Name => name;

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            up(connection, cancellationToken);

        public Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// The runner's transaction is really open when <c>UpAsync</c> runs, so the most natural
    /// migration body — open a transaction, do the work, commit — cannot work.
    /// </summary>
    [Fact]
    public async Task Migration_OpeningItsOwnTransaction_ThrowsNestedTransaction()
    {
        await using var store = new DocumentStoreFactory().Create(FileOptions());

        var migration = new LambdaMigration(1, "own-transaction", async (connection, cancellationToken) =>
        {
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, "CREATE TABLE own_tx (id TEXT);", cancellationToken);
            transaction.Commit();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MigrateAsync([migration]));
        Assert.Contains("does not support nested transactions", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>PRAGMA foreign_keys</c> is a no-op inside a transaction — the reason the docs name it
    /// explicitly rather than lumping it under "some statements". It does not fail; it is ignored.
    /// </summary>
    [Fact]
    public async Task Migration_TurningForeignKeysOff_IsSilentlyIgnoredInsideTheRunnersTransaction()
    {
        var options = FileOptions();
        options.EnableForeignKeys = true;
        await using var store = new DocumentStoreFactory().Create(options);

        long readBack = -1;
        var migration = new LambdaMigration(1, "fk-off", async (connection, cancellationToken) =>
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
            readBack = await ScalarAsync(connection, "PRAGMA foreign_keys;", cancellationToken);
        });

        var applied = await store.MigrateAsync([migration]);

        Assert.Equal(1, applied);
        Assert.Equal(1, readBack);
    }

    /// <summary>
    /// The single-lease promise proper: the connection is held for the <em>whole run</em>, not
    /// rented and returned per migration. Pinned at <see cref="DocumentStoreOptions.MaxPoolSize"/>
    /// = 1, where an operation started from inside the first migration can only complete once the
    /// run releases the lease — so it must still be pending when the second migration runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Assert.Same</c> test below is not enough on its own: a runner that rented per
    /// migration would return the same pooled connection each time and satisfy it. Measured —
    /// renting per migration makes the queued operation complete between migrations and fails this
    /// test, while leaving that one green.
    /// </para>
    /// <para>
    /// The discriminator is an <em>ordering</em>, with no elapsed time in it anywhere:
    /// <see cref="DocumentStoreOptions.PoolWaitTimeoutMs"/> is <see cref="Timeout.Infinite"/>, so
    /// nothing can be inferred from how long a wait took. The first migration queues an
    /// <c>ExecuteRawAsync</c> whose callback does nothing but signal a
    /// <see cref="TaskCompletionSource"/>; the call registers on the pool's only slot
    /// synchronously, since the first await on that path is the semaphore wait.
    /// </para>
    /// <para>
    /// The signal is set <em>from inside the callback</em>, and deliberately not read off the outer
    /// task's <c>IsCompleted</c>. The pool releases the slot in <c>ReturnAfterExternalAccess</c>
    /// after the callback returns, so the competing rent can be satisfied — and the second
    /// migration can be running — before the outer task is marked complete. Reading
    /// <c>IsCompleted</c> therefore has a window in which the mutant looks like the correct
    /// implementation. A signal set while the callback still holds the lease has no such window:
    /// it is strictly ordered before the release that lets anything else proceed.
    /// </para>
    /// <para>
    /// So under one lease for the run the callback never acquires the slot and the signal is unset
    /// when the second migration looks; renting per migration, it acquires the slot freed after the
    /// first migration, sets the signal and returns promptly, and the second migration sees it set.
    /// The second migration then cancels the wait, which is what releases a still-parked callback
    /// under the infinite timeout, and the <c>finally</c> observes the task's outcome without
    /// asserting on it — so nothing is left unobserved and no assertion there can mask a real
    /// failure of the run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MigrateAsync_HoldsOneLeaseForTheWholeRun_NotOnePerMigration()
    {
        var options = FileOptions();
        options.MaxPoolSize = 1;
        options.PoolWaitTimeoutMs = Timeout.Infinite;
        await using var store = new DocumentStoreFactory().Create(options);
        await store.CreateTableAsync<Counted>();

        using var queuedCancellation = new CancellationTokenSource();
        var acquiredALease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? queued = null;
        bool? queuedAcquiredALeaseDuringTheRun = null;

        var first = new LambdaMigration(1, "first", async (connection, cancellationToken) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE lease_1 (id TEXT);", cancellationToken);

            // Registers on the pool's only slot before this call returns. The callback runs only
            // once it holds a lease, and signals before returning one — so the signal is ordered
            // strictly before the release, unlike the outer task's completion.
            queued = store.ExecuteRawAsync(
                (_, _) =>
                {
                    acquiredALease.TrySetResult();
                    return Task.CompletedTask;
                },
                queuedCancellation.Token);
        });

        var second = new LambdaMigration(2, "second", async (connection, cancellationToken) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE lease_2 (id TEXT);", cancellationToken);

            // Ordering, not duration: reaching here at all means this migration holds the slot.
            queuedAcquiredALeaseDuringTheRun = acquiredALease.Task.IsCompleted;
            await queuedCancellation.CancelAsync();
        });

        try
        {
            var applied = await store.MigrateAsync([first, second]);
            Assert.Equal(2, applied);
        }
        finally
        {
            // Observe the outcome, whatever it is; asserting here would replace a real failure.
            if (queued is not null)
            {
                try
                {
                    await queued;
                }
                catch
                {
                    // Cancelled, or failed with the run — either way it is now observed.
                }
            }
        }

        Assert.False(
            queuedAcquiredALeaseDuringTheRun,
            "The queued operation acquired a lease while the run was still in progress, so the run "
                + "released its lease between the migrations.");
    }

    private sealed class Counted
    {
        public string Id { get; set; } = "";
    }

    /// <summary>
    /// The consequence a migration author sees: every <c>UpAsync</c> in a run gets one and the
    /// same connection instance.
    /// </summary>
    /// <remarks>
    /// Kept as a direct identity assertion, but it is the weaker of the two lease tests and does
    /// not stand in for the one above. It does <strong>not</strong> catch a runner that rents per
    /// migration — measured, that mutant leaves this test green, because the pool hands the same
    /// idle connection straight back — and so it does not pin the single-lease promise at all. Only
    /// a refactor that passed a different <c>SqliteConnection</c> instance would fail here.
    /// </remarks>
    [Fact]
    public async Task MigrateAsync_HandsEveryMigrationTheSameConnectionInstance()
    {
        await using var store = new DocumentStoreFactory().Create(FileOptions());

        var seen = new List<SqliteConnection>();
        IMigration Migration(long version) => new LambdaMigration(
            version,
            $"m{version}",
            async (connection, cancellationToken) =>
            {
                seen.Add(connection);
                await ExecuteAsync(connection, $"CREATE TABLE m{version} (id TEXT);", cancellationToken);
            });

        var applied = await store.MigrateAsync([Migration(1), Migration(2), Migration(3)]);

        Assert.Equal(3, applied);
        Assert.Equal(3, seen.Count);
        Assert.All(seen, connection => Assert.Same(seen[0], connection));
    }

    /// <summary>
    /// The sharpest shape in the docs, because the outcome is silent-shaped and only conditional:
    /// a bare <c>COMMIT</c> ends the runner's transaction, so the migration's work and its history
    /// row become separately durable and the run <em>can</em> throw with the version already
    /// recorded — as it does here, leaving the caller told it failed and no retry re-running it.
    /// The docs promise that this can happen, not that it always does.
    /// </summary>
    [Fact]
    public async Task Migration_IssuingABareCommit_ThrowsWhileLeavingTheMigrationRecordedAsApplied()
    {
        await using var store = new DocumentStoreFactory().Create(FileOptions());

        var migration = new LambdaMigration(1, "bare-commit", async (connection, cancellationToken) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE bare_commit (id TEXT);", cancellationToken);
            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
        });

        await Assert.ThrowsAnyAsync<Exception>(() => store.MigrateAsync([migration]));

        // The run reported failure, but the work and the history row committed anyway.
        Assert.Equal(1, await store.GetCurrentMigrationVersionAsync());

        var tableCount = await store.ExecuteRawAsync((connection, cancellationToken) =>
            ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE name = 'bare_commit';", cancellationToken));
        Assert.Equal(1, tableCount);

        // And a retry cannot re-run it: membership in the history table says already applied.
        Assert.Equal(0, await store.MigrateAsync([migration]));
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A failed test may still hold the file; the temp directory keeps it.
                }
            }
        }
    }
}
