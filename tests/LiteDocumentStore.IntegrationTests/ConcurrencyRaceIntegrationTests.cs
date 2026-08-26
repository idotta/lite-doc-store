using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The concurrency API under actual contention: the read-modify-CAS retry loop the versioned
/// writes exist for, and the conflict metadata's exactness inside a transaction.
/// </summary>
/// <remarks>
/// A real file database, not shared-cache in-memory: the latter takes table-level locks and fails
/// overlapping write transactions with <c>SQLITE_LOCKED</c>, which <c>busy_timeout</c> does not
/// retry. <see cref="ConcurrencyException.ActualVersion"/> and <see cref="ConcurrencyException.Kind"/>
/// are asserted exactly only where the store documents them as exact — through an open
/// transaction, which holds the SQLite locks across the failed write and the stored-version read.
/// Outside one, that pair is a post-conflict observation and is deliberately left untested rather
/// than pinned to an interleaving no public gate can schedule.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ConcurrencyRaceIntegrationTests : IAsyncLifetime
{
    private sealed record Counter(int Value);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"lds-race-{Guid.NewGuid():N}.db");

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = DocumentStoreOptions.ForFile(_databasePath);
        options.MaxPoolSize = 16;
        _store = await new DocumentStoreFactory().CreateAsync(options);
        await _store.CreateTableAsync<Counter>();
    }

    [Fact]
    public async Task RacingWriters_RetryingOnConflict_ApplyEveryIncrementExactlyOnce()
    {
        // The shape the versioned API is designed for: read, modify, CAS, redo on rejection.
        // A lost update would leave Value below the writer count; a double-applied retry would
        // put it above, and the stored version would stop matching the number of writes.
        const int writers = 8;
        const int incrementsPerWriter = 5;

        await _store.UpsertAsync("counter", new Counter(0));

        // Every writer parks on the same gate, so the increments overlap instead of queuing
        // behind whichever task the scheduler happened to start first.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = Enumerable.Range(0, writers)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var conflicts = 0;

        var tasks = Enumerable.Range(0, writers).Select(async writer =>
        {
            // Read before the gate, so every writer starts from the same version and all but one
            // of them is guaranteed stale on its first attempt. SQLite serializes writers behind
            // the write lock, so leaving the first read inside the loop made the conflict depend
            // on the scheduler — it happened most runs, and not all of them.
            var seed = await _store.GetWithVersionAsync<Counter>("counter");

            ready[writer].SetResult();
            await start.Task;

            for (var i = 0; i < incrementsPerWriter; i++)
            {
                var current = seed;
                seed = null;

                while (true)
                {
                    current ??= await _store.GetWithVersionAsync<Counter>("counter");
                    Assert.NotNull(current);

                    try
                    {
                        await _store.UpsertWithVersionAsync(
                            "counter",
                            new Counter(current.Data.Value + 1),
                            current.Version);
                        break;
                    }
                    catch (ConcurrencyException ex)
                    {
                        // The only kind a live competitor can produce here: the row exists and
                        // its version moved on. Anything else means the retry loop is wrong.
                        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, ex.Kind);
                        Interlocked.Increment(ref conflicts);
                        current = null;
                    }
                }
            }
        }).ToArray();

        await Task.WhenAll(ready.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(30));
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        // At least one writer per pre-read snapshot has to lose, or the writes never overlapped
        // and the retry loop below is untested. The upper bound is scheduler-dependent, so only
        // the floor is asserted.
        Assert.True(
            conflicts >= writers - 1,
            $"Expected at least {writers - 1} lost CAS attempts, saw {conflicts}; the race did not overlap.");

        var final = await _store.GetWithVersionAsync<Counter>("counter");
        Assert.NotNull(final);
        Assert.Equal(writers * incrementsPerWriter, final.Data.Value);

        // One insert plus one successful write per increment. Retries do not bump the version,
        // so this is the assertion that catches an increment applied twice.
        Assert.Equal(1 + (writers * incrementsPerWriter), final.Version);
    }

    [Fact]
    public async Task RacingDeleters_OnlyOneSucceeds_TheRestSeeTheRowGone()
    {
        await _store.UpsertAsync("victim", new Counter(1));
        var stored = await _store.GetWithVersionAsync<Counter>("victim");
        Assert.NotNull(stored);

        const int deleters = 8;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, deleters).Select(async _ =>
        {
            await start.Task;
            try
            {
                await _store.DeleteWithVersionAsync<Counter>("victim", stored.Version);
                return (ConcurrencyConflictKind?)null;
            }
            catch (ConcurrencyException ex)
            {
                return ex.Kind;
            }
        }).ToArray();

        start.SetResult();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Single(outcomes, outcome => outcome is null);

        // Every loser deleted nothing because the row was already gone, not because its version
        // was stale — the kind a caller would branch on to decide "already handled by someone".
        Assert.All(
            outcomes.Where(outcome => outcome is not null),
            outcome => Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, outcome));
        Assert.False(await _store.ExistsAsync<Counter>("victim"));
    }

    [Fact]
    public async Task VersionMismatch_InsideATransaction_ReportsTheExactStoredVersion()
    {
        await _store.UpsertAsync("doc", new Counter(1));

        await _store.ExecuteInTransactionAsync(async transaction =>
        {
            var bumped = await transaction.UpsertWithVersionAsync("doc", new Counter(2), 1);
            Assert.Equal(2, bumped);

            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.UpsertWithVersionAsync("doc", new Counter(3), 1));

            // Exact, not approximate: the transaction holds the locks across the failed write and
            // the stored-version read, so nothing can move the row in between.
            Assert.Equal(ConcurrencyConflictKind.VersionMismatch, conflict.Kind);
            Assert.Equal(1, conflict.ExpectedVersion);
            Assert.Equal(2, conflict.ActualVersion);
            Assert.Equal("doc", conflict.DocumentId);
            Assert.Equal(_store.GetTableName<Counter>(), conflict.TableName);
        });
    }

    [Fact]
    public async Task AlreadyExists_InsideATransaction_ReportsTheExactStoredVersion()
    {
        await _store.ExecuteInTransactionAsync(async transaction =>
        {
            await transaction.UpsertAsync("doc", new Counter(1));
            await transaction.UpsertAsync("doc", new Counter(2));

            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.UpsertWithVersionAsync("doc", new Counter(3), 0));

            Assert.Equal(ConcurrencyConflictKind.AlreadyExists, conflict.Kind);
            Assert.Equal(0, conflict.ExpectedVersion);
            Assert.Equal(2, conflict.ActualVersion);
        });
    }

    [Fact]
    public async Task DocumentNotFound_InsideATransaction_ReportsNoStoredVersion()
    {
        await _store.ExecuteInTransactionAsync(async transaction =>
        {
            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.DeleteWithVersionAsync<Counter>("absent", 1));

            Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, conflict.Kind);
            Assert.Null(conflict.ActualVersion);
        });
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The provider may still hold the handle; a stale temp file is harmless.
            }
        }
    }
}
