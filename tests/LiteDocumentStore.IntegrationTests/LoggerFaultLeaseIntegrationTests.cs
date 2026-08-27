using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the release paths under a caller-supplied <see cref="ILogger"/> that
/// throws.
/// </summary>
/// <remarks>
/// <para>
/// A log call is caller code. On a path that hands a pooled connection back, a throw does not
/// just lose the message: every one of those paths is guarded by a one-shot flag, so the escape
/// skips the hand-back and no retry can ever reach it. The connection is then gone for the
/// lifetime of the process, and once that has happened <c>MaxPoolSize</c> times the store
/// deadlocks with nothing logged anywhere.
/// </para>
/// <para>
/// The transaction tests run with <c>MaxPoolSize = 1</c>, so a single lost lease is the
/// difference between the probe returning and the probe hanging; the store-disposal test uses 2,
/// so that "every connection" means more than one. The logger is armed only after the store is
/// built, because a logger that throws from the start cannot get a store constructed — which is
/// also the realistic shape: a file or network sink that dies mid-process. It fails on the exact
/// levels a test names, since some of these shapes need two log calls to throw and one to
/// succeed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LoggerFaultLeaseIntegrationTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Leave it to the temp directory rather than fail a passing test.
                }
            }
        }
    }

    private sealed record Doc(string Name);

    /// <summary>
    /// A switch shared with a logger, so the fault can be turned on after the store is built and
    /// off again before the probe.
    /// </summary>
    private sealed class Fault
    {
        public bool Armed { get; set; }
    }

    /// <summary>
    /// Fails on exactly the levels it is given, so a test can break one log call and still read
    /// back the ones around it.
    /// </summary>
    private sealed class FaultyLogger(Fault fault, params LogLevel[] failOn) : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (fault.Armed && Array.IndexOf(failOn, logLevel) >= 0)
            {
                throw new InvalidOperationException("logger failed");
            }

            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FaultyLoggerFactory(FaultyLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private static (IDocumentStore Store, FaultyLogger Logger) CreateStore(
        DocumentStoreOptions options,
        Fault fault,
        params LogLevel[] failOn)
    {
        var logger = new FaultyLogger(fault, failOn);
        var factory = new DocumentStoreFactory(new DefaultConnectionFactory(), null, new FaultyLoggerFactory(logger));

        return (factory.Create(options), logger);
    }

    private static (IDocumentStore Store, FaultyLogger Logger) CreateInMemoryStore(Fault fault, params LogLevel[] failOn)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 1;

        return CreateStore(options, fault, failOn);
    }

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-logger-fault-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);

        return path;
    }

    /// <summary>
    /// Bounded, so a lost lease surfaces as a failed assertion rather than a hung test run.
    /// </summary>
    /// <remarks>
    /// The probe creates the table rather than reading it: a discarded connection is the last
    /// one on a shared-cache in-memory database, which destroys it, and the point here is
    /// whether the pool slot came back — not what the database still holds.
    /// </remarks>
    private static async Task<bool> LeaseSurvivedAsync(IDocumentStore store)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await store.CreateTableAsync<Doc>(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Counts through a bounded token, so a lease that never came back fails the test instead of
    /// hanging it.
    /// </summary>
    private static async Task<long> ProbeCountAsync(IDocumentStore store)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            return await store.CountAsync<Doc>(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the pooled connection was never handed back");
            return -1;
        }
    }

    /// <summary>
    /// Commits the transaction out from under the object that owns it, leaving that object
    /// attached with nothing to roll back — the one shape where a transaction's own teardown
    /// fails.
    /// </summary>
    private static Task<int> CommitUnderneathAsync(IDocumentTransaction transaction) =>
        transaction.ExecuteRawAsync(async (connection, cancellationToken) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "COMMIT";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return 0;
        });

    [Fact]
    public async Task CommitAsync_WhenTheLoggerThrows_SurfacesTheFailureAndStillReturnsTheLease()
    {
        // The log here stays loud on purpose — a caller is waiting on this method — so the throw
        // is expected. What must not happen is the lease going with it.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Debug, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        fault.Armed = true;

        // Deliberately not disposed. A using block would hide the defect this pins: disposal
        // hands the lease back through its own path, so it would paper over CommitAsync failing
        // to. A caller who commits and walks away is the shape that loses the connection.
        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new Doc("a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());

        fault.Armed = false;

        // The commit itself is durable — the throw happens after it, not instead of it — and the
        // count needs a lease, so this asserts both at once.
        Assert.Equal(1, await ProbeCountAsync(store));
    }

    [Fact]
    public async Task RollbackAsync_WhenTheLoggerThrows_SurfacesTheFailureAndStillReturnsTheLease()
    {
        // The rollback half of the same deliberate decision, pinned separately: a commit-only
        // assertion would let this path be quieted without anything failing.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Debug, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("seed", new Doc("seed"));
        fault.Armed = true;

        // Not disposed, for the reason CommitAsync's test gives.
        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new Doc("a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());

        fault.Armed = false;
        Assert.Equal(1, await ProbeCountAsync(store));
    }

    [Fact]
    public async Task DisposeAsync_WhenTheSuccessLogThrows_DoesNotEscapeAndRecyclesTheConnection()
    {
        // No rollback failure is needed to reach this: the rollback succeeds and its Debug log
        // throws. Quieting that log does two things — it keeps the throw out of DisposeAsync, and
        // it keeps a successful rollback from being recorded as a failed one, which would set
        // _connectionCompromised and discard a perfectly good connection.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Debug, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("seed", new Doc("seed"));
        fault.Armed = true;

        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new Doc("a"));
        await transaction.DisposeAsync();

        fault.Armed = false;

        // The count carries the whole assertion. It needs a lease, so a lost one hangs it; and
        // this is the only connection on a shared-cache in-memory database, so a discarded one
        // would have destroyed the database along with the table. One row, not two, because the
        // rollback did happen.
        Assert.Equal(1, await ProbeCountAsync(store));
    }

    [Fact]
    public async Task Dispose_WhenTheSuccessLogThrows_DoesNotEscapeAndRecyclesTheConnection()
    {
        // The synchronous twin of DisposeAsync above, through RollbackIfPending.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Debug, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("seed", new Doc("seed"));
        fault.Armed = true;

        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new Doc("a"));
        transaction.Dispose();

        fault.Armed = false;
        Assert.Equal(1, await ProbeCountAsync(store));
    }

    [Fact]
    public async Task DisposeAsync_WhenTheRollbackFailsAndTheLoggerThrows_DoesNotEscapeAndHandsTheLeaseBack()
    {
        // A raw COMMIT leaves the transaction object attached with nothing to roll back, so the
        // rollback throws and the catch's Warning runs. That connection really is compromised, so
        // it is discarded rather than recycled — but the slot still has to come back.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        fault.Armed = true;

        var transaction = await store.BeginTransactionAsync();
        await CommitUnderneathAsync(transaction);
        await transaction.DisposeAsync();

        fault.Armed = false;
        Assert.True(await LeaseSurvivedAsync(store), "DisposeAsync lost its pooled connection");
    }

    [Fact]
    public async Task Dispose_WhenTheRollbackFailsAndTheLoggerThrows_DoesNotEscapeAndHandsTheLeaseBack()
    {
        // The synchronous twin, through RollbackIfPending's catch.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        fault.Armed = true;

        var transaction = await store.BeginTransactionAsync();
        await CommitUnderneathAsync(transaction);
        transaction.Dispose();

        fault.Armed = false;
        Assert.True(await LeaseSurvivedAsync(store), "Dispose lost its pooled connection");
    }

    [Fact]
    public async Task CleanCommit_WithALoggerThatOnlyFailsOnWarning_KeepsTheLease()
    {
        // The control: the fault is armed, but nothing this transaction does logs a warning, so
        // the outcome must be indistinguishable from a healthy logger.
        var fault = new Fault();
        var (store, _) = CreateInMemoryStore(fault, LogLevel.Warning);
        using var owned = store;
        await store.CreateTableAsync<Doc>();
        fault.Armed = true;

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync("a", new Doc("a"));
            await transaction.CommitAsync();
        }

        fault.Armed = false;
        Assert.True(await LeaseSurvivedAsync(store), "the clean path lost its pooled connection");
        Assert.Equal(1, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task StoreDisposal_WhenTheCheckpointLogThrows_StillCheckpointsAndClosesEveryConnection()
    {
        // The checkpoint's Debug log used to be caught by its own catch, whose Warning then threw
        // out of Dispose — skipping both the checkpoint and _pool.Dispose(), so every pooled
        // connection stayed open for the life of the process with _disposed already set.
        var path = NewDatabasePath();
        var options = new DocumentStoreOptionsBuilder()
            .WithConnectionString($"Data Source={path}")
            .WithWalMode(true)
            .WithMaxPoolSize(2)
            .Build();

        var fault = new Fault();
        var (store, logger) = CreateStore(options, fault, LogLevel.Debug, LogLevel.Warning);
        await store.CreateTableAsync<Doc>();

        // Two overlapping transactions force both physical connections open, so "every
        // connection" means more than one. They read rather than write — two concurrent write
        // transactions on one file would just deadlock on the write lock.
        await using (var first = await store.BeginTransactionAsync())
        {
            await using var second = await store.BeginTransactionAsync();
            await first.CountAsync<Doc>();
            await second.CountAsync<Doc>();
        }

        // Written outside those, so there are WAL frames for the checkpoint to move.
        await store.UpsertAsync("a", new Doc("a"));
        await store.UpsertAsync("b", new Doc("b"));
        Assert.True(File.Exists(path + "-wal"), "the test needs a WAL to checkpoint");

        fault.Armed = true;
        store.Dispose();
        fault.Armed = false;

        // Written after the checkpoint statement, so its presence proves execution got past the
        // throwing Debug log that precedes it — the filesystem cannot show that, since the WAL is
        // removed on last close whether or not TRUNCATE ran.
        Assert.Contains("WAL checkpoint completed successfully", logger.Messages);

        // SQLite removes the -wal file when the last connection on the database closes, so its
        // absence is the pool having actually been disposed.
        Assert.False(File.Exists(path + "-wal"), "the pool was not disposed, so the WAL survived");

        // And the committed rows are readable from a fresh connection.
        using var verification = new SqliteConnection($"Data Source={path}");
        verification.Open();
        using var command = verification.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Doc";
        Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task StoreDisposalAsync_WhenTheCheckpointLogThrows_StillCheckpointsAndClosesEveryConnection()
    {
        // The asynchronous twin: DisposeAsync and Dispose carry separate copies of the checkpoint,
        // so one test cannot speak for both.
        var path = NewDatabasePath();
        var options = new DocumentStoreOptionsBuilder()
            .WithConnectionString($"Data Source={path}")
            .WithWalMode(true)
            .WithMaxPoolSize(2)
            .Build();

        var fault = new Fault();
        var (store, logger) = CreateStore(options, fault, LogLevel.Debug, LogLevel.Warning);
        await store.CreateTableAsync<Doc>();

        await using (var first = await store.BeginTransactionAsync())
        {
            await using var second = await store.BeginTransactionAsync();
            await first.CountAsync<Doc>();
            await second.CountAsync<Doc>();
        }

        await store.UpsertAsync("a", new Doc("a"));
        await store.UpsertAsync("b", new Doc("b"));
        Assert.True(File.Exists(path + "-wal"), "the test needs a WAL to checkpoint");

        fault.Armed = true;
        await store.DisposeAsync();
        fault.Armed = false;

        Assert.Contains("WAL checkpoint completed successfully", logger.Messages);
        Assert.False(File.Exists(path + "-wal"), "the pool was not disposed, so the WAL survived");
    }
}
