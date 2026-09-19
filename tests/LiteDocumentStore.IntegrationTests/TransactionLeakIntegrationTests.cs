using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for a transaction the caller never finished, and for the bound that keeps
/// pool exhaustion diagnosable.
/// </summary>
/// <remarks>
/// <para>
/// A transaction holds one of <c>MaxPoolSize</c> slots until it is committed, rolled back or
/// disposed. A leaked one used to hold its slot for the lifetime of the process, and the
/// operation rent waited forever, so <c>MaxPoolSize</c> leaks hung the whole store silently. The
/// finalizer now gives the slot back and the rent is bounded by
/// <see cref="DocumentStoreOptions.PoolWaitTimeoutMs"/>.
/// </para>
/// <para>
/// These use a file database in WAL mode, and the recovery probe is a <b>read</b>: the finalizer
/// deliberately does not close or roll back the abandoned connection, so its write lock outlives
/// the leak and a write would fail with <c>SQLITE_BUSY</c> even on a store whose slot came back.
/// Every wait is bounded by a cancellation watchdog rather than a timeout, so a runaway surfaces
/// as <see cref="OperationCanceledException"/> instead of masquerading as the
/// <see cref="TimeoutException"/> under test.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TransactionLeakIntegrationTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private sealed record Doc(string Name);

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

    /// <summary>
    /// Counts what the store logged at Error, and can fail on that level on demand.
    /// </summary>
    private sealed class ErrorCountingLogger(bool throwOnError) : ILogger
    {
        public int Errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error)
            {
                return;
            }

            Interlocked.Increment(ref Errors);

            if (throwOnError)
            {
                throw new InvalidOperationException("logger failed");
            }
        }
    }

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private async Task<IDocumentStore> CreateStoreAsync(int poolWaitTimeoutMs, ILogger? logger = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-tx-leak-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);

        var options = DocumentStoreOptions.ForFile(path);
        options.MaxPoolSize = 1;
        options.PoolWaitTimeoutMs = poolWaitTimeoutMs;

        // The abandoned connection keeps its write lock until the provider finalizes it, so the
        // dispose-time wal_checkpoint(TRUNCATE) waits out the busy timeout on it (5 s by default,
        // measured). Nothing here is about that wait, and skipping the TRUNCATE costs nothing —
        // SQLite checkpoints the WAL itself when the last connection closes.
        options.BusyTimeoutMs = 500;

        var factory = logger is null
            ? new DocumentStoreFactory()
            : new DocumentStoreFactory(new DefaultConnectionFactory(), null, new SingleLoggerFactory(logger));

        var store = await factory.CreateAsync(options);
        await store.CreateTableAsync<Doc>();

        return store;
    }

    /// <summary>
    /// Starts a transaction, writes through it and drops it on the floor. Nothing in the caller's
    /// frame roots it afterwards; the returned weak reference is only a diagnostic.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LeakATransactionAsync(IDocumentStore store)
    {
        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("leaked", new Doc("n"));

        return new WeakReference(transaction);
    }

    /// <summary>
    /// Drains the finalizer queue until the leaked transaction's finalizer has reported the leak,
    /// or the deadline passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait is on the finalizer's own observable effect — the <c>Error</c> log it writes,
    /// counted by <paramref name="logger"/> — and never on <paramref name="leaked"/>. A short
    /// <see cref="WeakReference"/> is cleared when its target becomes <i>unreachable</i>, which
    /// happens before the finalizer runs, so it cannot say that the finalizer ran; and once some
    /// earlier unrelated collection has cleared it, a loop guarded on <c>IsAlive</c> drains
    /// nothing at all and every later assertion about the finalizer's effect fails at random.
    /// That was this class's flake. The drain is therefore unconditional, and
    /// <paramref name="leaked"/> only enriches the failure message.
    /// </para>
    /// <para>
    /// Checking the count <i>after</i> <see cref="GC.WaitForPendingFinalizers"/> returns is what
    /// makes the slot release observable too: the count is incremented inside the finalizer, so a
    /// drain that has returned with the count already raised has run the whole finalizer body,
    /// including the <c>finally</c> that gives the slot back.
    /// </para>
    /// </remarks>
    private static void DrainFinalizersUntilLeakReported(WeakReference leaked, ErrorCountingLogger logger)
    {
        // Same bound as the watchdogs below: generous enough not to flake on a loaded machine,
        // short enough that a genuine regression fails the run rather than hanging it.
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (Volatile.Read(ref logger.Errors) > 0)
            {
                return;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(20))
            {
                Assert.Fail(
                    "the leaked transaction's finalizer never reported the leak within 20 s " +
                    $"(leaked.IsAlive = {leaked.IsAlive})");
            }

            Thread.Sleep(25);
        }
    }

    private static async Task ReadSucceedsAsync(IDocumentStore store)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // A read, not a write: the abandoned connection keeps its write lock until the provider
        // finalizes it, so a write could fail for a reason that has nothing to do with the slot.
        var read = store.GetAsync<Doc>("absent");
        var document = await read.WaitAsync(watchdog.Token);

        Assert.Null(document);
    }

    [Fact]
    public async Task LeakedTransaction_IsFinalized_GivingItsPoolSlotBack()
    {
        var logger = new ErrorCountingLogger(throwOnError: false);
        await using var store = await CreateStoreAsync(poolWaitTimeoutMs: 2000, logger);

        DrainFinalizersUntilLeakReported(await LeakATransactionAsync(store), logger);

        Assert.True(Volatile.Read(ref logger.Errors) > 0, "the leak was never reported");
        await ReadSucceedsAsync(store);
    }

    [Fact]
    public async Task LeakedTransaction_WithALoggerThatThrows_StillGivesItsPoolSlotBack()
    {
        // The release is in a finally for this: an ILogger is caller code, and a throwing one must
        // not cost the slot the finalizer exists to recover.
        var logger = new ErrorCountingLogger(throwOnError: true);
        await using var store = await CreateStoreAsync(poolWaitTimeoutMs: 2000, logger);

        DrainFinalizersUntilLeakReported(await LeakATransactionAsync(store), logger);

        Assert.True(Volatile.Read(ref logger.Errors) > 0, "the diagnostic was never attempted");
        await ReadSucceedsAsync(store);
    }

    [Fact]
    public async Task LeakedTransaction_BeforeItIsFinalized_FailsTheNextOperationWithATimeout()
    {
        // Until the finalizer runs the slot really is gone; the point of the bound is that the
        // caller learns that instead of hanging on it.
        await using var store = await CreateStoreAsync(poolWaitTimeoutMs: 200);
        var leaked = await store.BeginTransactionAsync();
        await leaked.UpsertAsync("held", new Doc("n"));

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => store.GetAsync<Doc>("absent").WaitAsync(watchdog.Token));

        Assert.Contains(nameof(DocumentStoreOptions.PoolWaitTimeoutMs), thrown.Message, StringComparison.Ordinal);
        Assert.Contains("200 ms", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("pool size 1", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Dispose transactions promptly", thrown.Message, StringComparison.Ordinal);

        GC.KeepAlive(leaked);
        await leaked.DisposeAsync();
    }

    [Fact]
    public async Task NestedStoreRead_InsideATransaction_TimesOutInsteadOfHanging()
    {
        // A read through the store from inside the callback is documented as safe in WAL mode, but
        // at MaxPoolSize = 1 it waits for the connection the transaction itself is holding.
        await using var store = await CreateStoreAsync(poolWaitTimeoutMs: 200);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var work = store.ExecuteInTransactionAsync(async _ => await store.GetAsync<Doc>("absent"));

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(watchdog.Token));
        Assert.Contains("pool size 1", thrown.Message, StringComparison.Ordinal);
    }
}
