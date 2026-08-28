using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="SqliteConnectionPool"/> — the component that makes the store
/// thread-safe by renting a connection per operation.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteConnectionPoolTests
{
    private static SqliteConnectionPool CreatePool(int maxPoolSize = 4)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = maxPoolSize;

        return new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance);
    }

    private static SqliteConnectionPool CreatePoolWithAThrowingLogger(int maxPoolSize = 1)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = maxPoolSize;

        return new SqliteConnectionPool(options, new DefaultConnectionFactory(), new ThrowingLogger());
    }

    /// <summary>
    /// A consumer logger that fails on exactly the level the pool's cleanup paths use.
    /// </summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                throw new InvalidOperationException("logger failed");
            }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Initialize_OpensOneConnection()
    {
        using var pool = CreatePool();

        pool.Initialize();

        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_ReturnsAnOpenConnection()
    {
        using var pool = CreatePool();

        await using var lease = await pool.RentAsync();

        Assert.Equal(ConnectionState.Open, lease.Connection.State);
    }

    [Fact]
    public async Task RentAsync_AfterReturn_ReusesTheSameConnection()
    {
        using var pool = CreatePool();

        SqliteConnection first;
        await using (var lease = await pool.RentAsync())
        {
            first = lease.Connection;
        }

        await using var second = await pool.RentAsync();

        Assert.Same(first, second.Connection);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhileLeasesAreHeld_OpensDistinctConnections()
    {
        using var pool = CreatePool(maxPoolSize: 3);

        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();
        await using var third = await pool.RentAsync();

        Assert.NotSame(first.Connection, second.Connection);
        Assert.NotSame(second.Connection, third.Connection);
        Assert.Equal(3, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhenPoolIsSaturated_WaitsForAReturnedConnection()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        lease.Dispose();

        await using var second = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(lease.Connection, second.Connection);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhenSaturatedAndCancelled_ThrowsAndKeepsTheSlot()
    {
        using var pool = CreatePool(maxPoolSize: 1);
        using var cancellation = new CancellationTokenSource();

        var held = await pool.RentAsync();

        var queued = pool.RentAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // The cancelled waiter must not have consumed the only slot.
        held.Dispose();
        await using var next = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Open, next.Connection.State);
    }

    [Fact]
    public async Task Discard_ClosesTheConnectionAndFreesItsSlot()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var discarded = lease.Connection;
        lease.Discard();

        Assert.Equal(ConnectionState.Closed, discarded.State);
        Assert.Equal(0, pool.ConnectionCount);

        // The slot is free again, and the next rent opens a fresh connection.
        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(discarded, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task Return_AfterConnectionWasClosedExternally_DoesNotRecycleIt()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var broken = lease.Connection;
        broken.Close();
        lease.Dispose();

        await using var replacement = await pool.RentAsync();
        Assert.NotSame(broken, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task Return_WithATransactionStillPending_DoesNotRecycleTheConnection()
    {
        // A raw BEGIN a caller never finished. Recycling the connection would let the next
        // renter's statements silently enlist in that transaction.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        Execute(poisoned, "BEGIN");
        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, poisoned.State);
        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithACleanConnection_RecyclesIt()
    {
        // The raw path must not discard indiscriminately: a well-behaved callback costs nothing.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var connection = lease.Connection;
        lease.ReturnAfterExternalAccess();

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, pool.ConnectionCount);

        await using var next = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(connection, next.Connection);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithATransactionStillPending_DoesNotRecycleTheConnection()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        Execute(poisoned, "BEGIN");
        lease.ReturnAfterExternalAccess();

        Assert.Equal(ConnectionState.Closed, poisoned.State);
        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithAManagedTransactionStillAttached_DoesNotRecycleTheConnection()
    {
        // The shape SQLite's own autocommit flag cannot see: a raw COMMIT leaves the provider's
        // transaction object attached, which breaks the next renter's BeginTransaction and makes
        // closing the connection throw. Only the raw path pays for this check.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        var stale = poisoned.BeginTransaction();
        Execute(poisoned, "COMMIT");
        lease.ReturnAfterExternalAccess();

        Assert.Equal(0, pool.ConnectionCount);

        // Closing this shape throws on the first attempt and succeeds on the second, so the
        // connection is really closed rather than left open until finalization.
        Assert.Equal(ConnectionState.Closed, poisoned.State);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
        await using var transaction = await replacement.Connection.BeginTransactionAsync();

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task Return_OnADisposedPool_ReleasesTheSlotEvenWhenClosingThrows()
    {
        // Closing a connection whose transaction object survived a raw COMMIT throws
        // "cannot rollback - no transaction is active". A throw here used to skip the slot
        // release, and a waiter parked in RentAsync only ever wakes on a free slot.
        var pool = CreatePool(maxPoolSize: 1);
        var lease = await pool.RentAsync();
        var stale = lease.Connection.BeginTransaction();
        Execute(lease.Connection, "COMMIT");

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        pool.Dispose();
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10)));

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task DirtyReturn_WithAThrowingLogger_StillDiscardsTheConnectionExactlyOnce()
    {
        // ILogger is caller-supplied. A logger that throws used to escape the return — which runs
        // from a lease disposal, so it replaced whatever the caller's operation had produced —
        // and leave the connection open, untracked and holding its transaction, while
        // ConnectionCount was decremented twice for one connection.
        using var pool = CreatePoolWithAThrowingLogger();

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        Execute(poisoned, "BEGIN");

        Assert.Null(Record.Exception(lease.ReturnAfterExternalAccess));

        Assert.Equal(ConnectionState.Closed, poisoned.State);
        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
    }

    [Fact]
    public async Task DirtyReturn_WithAThrowingLoggerAndAThrowingClose_DoesNotEscape()
    {
        // Both halves of the cleanup fail at once: the discard log throws, and closing a
        // connection whose transaction object survived a raw COMMIT throws too.
        using var pool = CreatePoolWithAThrowingLogger();

        var lease = await pool.RentAsync();
        var stale = lease.Connection.BeginTransaction();
        Execute(lease.Connection, "COMMIT");

        Assert.Null(Record.Exception(lease.ReturnAfterExternalAccess));
        Assert.Equal(0, pool.ConnectionCount);
        Assert.Equal(ConnectionState.Closed, lease.Connection.State);

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task Dispose_WithAThrowingLogger_StillClosesEveryIdleConnection()
    {
        // The drain loop logs a failed close; a throwing logger there used to abandon the rest of
        // the bag with its connections still open.
        var pool = CreatePoolWithAThrowingLogger(maxPoolSize: 2);

        var first = await pool.RentAsync();
        var second = await pool.RentAsync();
        var stale = first.Connection.BeginTransaction();
        Execute(first.Connection, "COMMIT");
        var connections = new[] { first.Connection, second.Connection };
        first.Dispose();
        second.Dispose();

        Assert.Null(Record.Exception(pool.Dispose));
        Assert.All(connections, c => Assert.Equal(ConnectionState.Closed, c.State));

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task Dispose_ClosesIdleConnections()
    {
        var pool = CreatePool();
        SqliteConnection connection;
        await using (var lease = await pool.RentAsync())
        {
            connection = lease.Connection;
        }

        pool.Dispose();

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Dispose_ClosesAConnectionReturnedAfterwards()
    {
        var pool = CreatePool();
        var lease = await pool.RentAsync();

        pool.Dispose();
        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, lease.Connection.State);
    }

    [Fact]
    public async Task RentAsync_OnDisposedPool_ThrowsObjectDisposedException()
    {
        var pool = CreatePool();
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pool.RentAsync());
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
        Assert.Throws<ObjectDisposedException>(pool.Initialize);
    }

    [Fact]
    public async Task Dispose_WithAQueuedRent_CompletesTheWaiterInsteadOfHangingIt()
    {
        // Disposing _slots under a parked WaitAsync used to leave this rent pending forever.
        var pool = CreatePool(maxPoolSize: 1);
        var held = await pool.RentAsync();

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        pool.Dispose();
        held.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Rent_WithATimeout_OnASaturatedPool_ThrowsTimeoutException()
    {
        using var pool = CreatePool(maxPoolSize: 1);
        await using var held = await pool.RentAsync();

        Assert.Throws<TimeoutException>(() => pool.Rent(TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await pool.RentAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Rent_WithATimeout_KeepsTheSlotWhenItExpires()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        using (var held = await pool.RentAsync())
        {
            Assert.Throws<TimeoutException>(() => pool.Rent(TimeSpan.FromMilliseconds(50)));
        }

        // The timed-out rent must not have consumed the slot it never acquired.
        await using var second = await pool.RentAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Open, second.Connection.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPoolSize_BelowOne_ThrowsNamingTheOption(int maxPoolSize)
    {
        var options = DocumentStoreOptions.ForInMemory();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxPoolSize = maxPoolSize);
        Assert.True(options.MaxPoolSize >= 1);
    }

    [Fact]
    public void Constructor_WithEmptyConnectionString_ThrowsArgumentException()
    {
        var options = new DocumentStoreOptions { MaxPoolSize = 2 };

        Assert.Throws<ArgumentException>(() =>
            new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance));
    }

    [Fact]
    public void Constructor_WithPrivateInMemoryConnectionString_ThrowsArgumentException()
    {
        var options = new DocumentStoreOptions("Data Source=:memory:");

        var error = Assert.Throws<ArgumentException>(() =>
            new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance));

        Assert.Contains(nameof(DocumentStoreOptions.ForInMemory), error.Message);
    }

    [Fact]
    public async Task Pool_DisablesTheBuiltInSqlitePool()
    {
        // The store's whole reason for owning a pool is that it configures each physical
        // connection once; Microsoft.Data.Sqlite's pool would hand back handles whose session
        // PRAGMAs it does not preserve.
        using var pool = CreatePool();

        await using var lease = await pool.RentAsync();

        var builder = new SqliteConnectionStringBuilder(lease.Connection.ConnectionString);
        Assert.False(builder.Pooling);
    }

    [Fact]
    public async Task Pool_AppliesConfiguredPragmasToEveryConnection()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 2;
        options.BusyTimeoutMs = 1234;

        using var pool = new SqliteConnectionPool(
            options, new DefaultConnectionFactory(), NullLogger.Instance);

        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();

        foreach (var lease in new[] { first, second })
        {
            var timeout = await lease.Connection.ExecuteScalarAsync<long>("PRAGMA busy_timeout", CancellationToken.None);
            Assert.Equal(1234, timeout);
        }
    }

    /// <summary>
    /// A clean return that lands while the pool is being disposed must not leave the connection
    /// open. Both racers reach the connection through the idle bag, and
    /// <c>ConcurrentBag.TryTake</c> hands it to exactly one of them, so it is closed exactly once.
    /// </summary>
    /// <remarks>
    /// A stress loop rather than a gated one: nothing between <c>ReturnCore</c>'s <c>_disposed</c>
    /// read and its <c>_idle.Add</c> is injectable, so the interleaving can only be provoked. It
    /// is not a rare one — before the post-add re-check this failed at 330 of 400.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Return_RacingDispose_ClosesEveryConnection(bool disposeAsync)
    {
        const int attempts = 400;
        var created = 0;
        var leftOpen = 0;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var options = DocumentStoreOptions.ForInMemory();
            options.MaxPoolSize = 1;

            var factory = new RecordingConnectionFactory();
            var pool = new SqliteConnectionPool(options, factory, NullLogger.Instance);
            var lease = await pool.RentAsync();

            using var ready = new Barrier(2);
            var returner = Task.Run(() =>
            {
                ready.SignalAndWait();
                lease.Dispose();
            });

            var disposer = Task.Run(async () =>
            {
                ready.SignalAndWait();
                if (disposeAsync)
                {
                    await pool.DisposeAsync();
                }
                else
                {
                    pool.Dispose();
                }
            });

            await Task.WhenAll(returner, disposer);

            foreach (var connection in factory.Opened)
            {
                created++;
                if (connection.State != ConnectionState.Closed)
                {
                    leftOpen++;
                    connection.Dispose();
                }
            }
        }

        // Guards the assertion below against passing on a run that opened nothing.
        Assert.Equal(attempts, created);
        Assert.Equal(0, leftOpen);
    }

    /// <summary>
    /// The same race on the initialization path, which banks its connection the same way. Gated
    /// rather than stressed: the factory parks inside the open until the pool has been disposed
    /// and its drain has run, so the losing interleaving happens on every run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_RacingDispose_ClosesTheConnectionItBanks(bool initializeAsync)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 1;

        using var creating = new ManualResetEventSlim(false);
        using var disposalDone = new ManualResetEventSlim(false);

        var factory = new GatedConnectionFactory(creating, disposalDone);
        using var pool = new SqliteConnectionPool(options, factory, NullLogger.Instance);

        var initializing = Task.Run(async () =>
        {
            // The pool is disposed while this call is parked in the factory. It passed
            // ThrowIfDisposed on the way in, so it banks its connection on a disposed pool; an
            // ordering that throws instead would be equally correct.
            try
            {
                if (initializeAsync)
                {
                    await pool.InitializeAsync();
                }
                else
                {
                    pool.Initialize();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });

        Assert.True(creating.Wait(TimeSpan.FromSeconds(10)), "the factory was never entered");
        pool.Dispose();
        disposalDone.Set();
        await initializing;

        var opened = Assert.Single(factory.Opened);
        Assert.Equal(ConnectionState.Closed, opened.State);
    }

    /// <summary>
    /// Hands out real connections and keeps every one it opened, so a test can assert on
    /// connections the pool no longer refers to.
    /// </summary>
    private sealed class RecordingConnectionFactory : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public List<SqliteConnection> Opened { get; } = [];

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            var connection = _inner.CreateConnection(options);
            Opened.Add(connection);

            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            var connection = await _inner.CreateConnectionAsync(options, cancellationToken);
            Opened.Add(connection);

            return connection;
        }

        public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
            _inner.ConfigureConnection(connection, options);

        public Task ConfigureConnectionAsync(
            SqliteConnection connection,
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ConfigureConnectionAsync(connection, options, cancellationToken);
    }

    /// <summary>
    /// A recording factory that parks inside the open, so a test can drive a disposal to
    /// completion while a caller sits between the pool's disposal check and its idle-bag add.
    /// </summary>
    private sealed class GatedConnectionFactory : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public GatedConnectionFactory(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public List<SqliteConnection> Opened { get; } = [];

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            WaitForRelease();
            var connection = _inner.CreateConnection(options);
            Opened.Add(connection);

            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            WaitForRelease();
            var connection = await _inner.CreateConnectionAsync(options, cancellationToken);
            Opened.Add(connection);

            return connection;
        }

        public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
            _inner.ConfigureConnection(connection, options);

        public Task ConfigureConnectionAsync(
            SqliteConnection connection,
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ConfigureConnectionAsync(connection, options, cancellationToken);

        private void WaitForRelease()
        {
            _entered.Set();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(10)), "the disposal never completed");
        }
    }
}
