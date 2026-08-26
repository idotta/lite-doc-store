using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Pins the difference between a deferred and an immediate transaction under a concurrent
/// writer. File-based on purpose: a shared-cache in-memory database uses table-level locks and
/// fails overlapping write transactions with SQLITE_LOCKED regardless of mode.
/// </summary>
public class TransactionModeIntegrationTests : IDisposable
{
    // SQLITE_BUSY_SNAPSHOT — a deferred transaction's read snapshot went stale before its first
    // write, so the upgrade to a write lock can never succeed.
    private const int SqliteBusySnapshot = 517;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lds-txmode-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Still locked by a finalizing connection; harmless for a temp file.
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeferredTransaction_ReadThenWrite_FailsWithBusySnapshotWhenAnotherConnectionCommits()
    {
        await using var store = await CreateStoreAsync();

        await using var transaction = await store.BeginTransactionAsync(TransactionMode.Deferred);

        // Pins the snapshot.
        Assert.NotNull(await transaction.GetAsync<Person>("seed"));

        // A second pooled connection moves the database past that snapshot.
        await store.UpsertAsync("outside", new Person { Name = "Outside", Age = 2, Email = "o@example.com" });

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            transaction.UpsertAsync("inside", new Person { Name = "Inside", Age = 3, Email = "i@example.com" }));

        Assert.Equal(SqliteBusySnapshot, error.SqliteExtendedErrorCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImmediateTransaction_ReadThenWrite_HoldsTheWriteLockSoTheOutsideWriterWaitsInstead()
    {
        await using var store = await CreateStoreAsync();

        await using var transaction = await store.BeginTransactionAsync(TransactionMode.Immediate);

        Assert.NotNull(await transaction.GetAsync<Person>("seed"));

        // The mirror image of the deferred test: the transaction already holds the write lock, so
        // the concurrent commit that would stale its snapshot cannot happen at all — the outside
        // writer is the one that waits, and gives up after BusyTimeoutMs with plain SQLITE_BUSY.
        var blocked = await Assert.ThrowsAsync<SqliteException>(() =>
            store.UpsertAsync("outside", new Person { Name = "Outside", Age = 2, Email = "o@example.com" }));

        Assert.Equal(SqliteErrorCode.Busy, blocked.SqliteErrorCode);
        Assert.NotEqual(SqliteBusySnapshot, blocked.SqliteExtendedErrorCode);

        // The read-then-write sequence that fails on deferred succeeds here.
        await transaction.UpsertAsync("inside", new Person { Name = "Inside", Age = 3, Email = "i@example.com" });
        await transaction.CommitAsync();

        // And the outside write goes through once the lock is released.
        await store.UpsertAsync("outside", new Person { Name = "Outside", Age = 2, Email = "o@example.com" });

        Assert.NotNull(await store.GetAsync<Person>("inside"));
        Assert.NotNull(await store.GetAsync<Person>("outside"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImmediateTransaction_BlocksASecondImmediateTransactionWithPlainBusy()
    {
        await using var store = await CreateStoreAsync();

        await using var first = await store.BeginTransactionAsync(TransactionMode.Immediate);

        // BEGIN IMMEDIATE has the write lock; a second one waits for busy_timeout and then
        // reports a plain SQLITE_BUSY (5), not the unretryable snapshot error.
        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            store.BeginTransactionAsync(TransactionMode.Immediate));

        Assert.Equal(SqliteErrorCode.Busy, error.SqliteErrorCode);
        Assert.NotEqual(SqliteBusySnapshot, error.SqliteExtendedErrorCode);

        await first.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BlockedImmediateBegin_WaitsBusyTimeoutMs_NotTheProvidersThirtySecondDefault()
    {
        // PRAGMA busy_timeout bounds only SQLite's own handler inside one attempt;
        // Microsoft.Data.Sqlite then retries the attempt until the connection's command timeout,
        // so the effective wait is max(BusyTimeoutMs, command timeout). The factory sets the
        // command timeout from BusyTimeoutMs to keep the documented bound true — without that, this
        // wait is the provider's 30 s default.
        await using var store = await CreateStoreAsync(busyTimeoutMs: 500);

        await using var first = await store.BeginTransactionAsync(TransactionMode.Immediate);

        var elapsed = await TimeBlockedBeginAsync(store);

        Assert.InRange(elapsed.TotalMilliseconds, 400, 10_000);

        await first.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BlockedImmediateBegin_WithABusyTimeoutOfZero_StillFailsInsteadOfWaitingForever()
    {
        // BusyTimeoutMs = 0 reads as "do not wait for a lock", but 0 is also the provider's "retry
        // forever" (measured: a blocked BEGIN IMMEDIATE with DefaultTimeout = 0 never returned).
        // The factory floors the command timeout at one second, so the caller gets SQLITE_BUSY
        // rather than a hang.
        await using var store = await CreateStoreAsync(busyTimeoutMs: 0);

        await using var first = await store.BeginTransactionAsync(TransactionMode.Immediate);

        var blocked = TimeBlockedBeginAsync(store);
        var completed = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(
            ReferenceEquals(completed, blocked),
            "The blocked BEGIN IMMEDIATE never came back: the command timeout was left at the provider's infinite 0.");

        // One second of provider retries is the shortest bound it can express.
        Assert.InRange((await blocked).TotalMilliseconds, 0, 10_000);

        await first.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BlockedImmediateBegin_HonoursACommandTimeoutStatedInTheConnectionString()
    {
        // A stated timeout wins over BusyTimeoutMs, and when it is the longer of the two it is what
        // the caller actually waits.
        await using var store = await CreateStoreAsync(busyTimeoutMs: 250, extraConnectionString: "Default Timeout=3");

        await using var first = await store.BeginTransactionAsync(TransactionMode.Immediate);

        var elapsed = await TimeBlockedBeginAsync(store);

        Assert.True(
            elapsed.TotalMilliseconds >= 2_000,
            $"Expected the stated 3 s command timeout to govern, but the begin failed after {elapsed.TotalMilliseconds:F0} ms.");

        await first.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_WithImmediate_CommitsTheCallbacksWrites()
    {
        await using var store = await CreateStoreAsync();

        await store.ExecuteInTransactionAsync(
            async tx =>
            {
                var seed = await tx.GetAsync<Person>("seed");
                Assert.NotNull(seed);
                await tx.UpsertAsync("copied", seed!);
            },
            TransactionMode.Immediate);

        Assert.NotNull(await store.GetAsync<Person>("copied"));
    }

    private static async Task<TimeSpan> TimeBlockedBeginAsync(IDocumentStore store)
    {
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            store.BeginTransactionAsync(TransactionMode.Immediate));
        stopwatch.Stop();

        Assert.Equal(SqliteErrorCode.Busy, error.SqliteErrorCode);
        return stopwatch.Elapsed;
    }

    private async Task<IDocumentStore> CreateStoreAsync(
        int busyTimeoutMs = 250,
        string? extraConnectionString = null)
    {
        var connectionString = $"Data Source={_path}";
        if (extraConnectionString is not null)
        {
            connectionString += $";{extraConnectionString}";
        }

        var options = new DocumentStoreOptionsBuilder()
            .WithConnectionString(connectionString)
            .WithWalMode(true)
            // Short, so the SQLITE_BUSY cases fail fast instead of parking the test run. The store
            // applies it to Microsoft.Data.Sqlite's retry loop too, which is what keeps the
            // unretryable SQLITE_BUSY_SNAPSHOT case from stalling for the provider's 30 s default.
            .WithBusyTimeout(busyTimeoutMs)
            .Build();

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Person>();
        await store.UpsertAsync("seed", new Person { Name = "Seed", Age = 1, Email = "s@example.com" });
        return store;
    }
}

internal static class SqliteErrorCode
{
    public const int Busy = 5;
}
