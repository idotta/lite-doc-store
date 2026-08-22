using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// A fixed-capacity pool of SQLite connections that are configured once, on creation, and
/// then reused for the lifetime of the pool.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Data.Sqlite has its own connection pool, but it gives no hook for "this handle
/// is new", so every rent would have to re-apply the session PRAGMAs
/// (<c>synchronous</c>, <c>cache_size</c>, <c>busy_timeout</c>) to be correct. That costs an
/// extra round trip per operation — measured at roughly +3 µs, which is 68% on a ~4.5 µs
/// read. This pool applies the PRAGMAs once per physical connection instead, so renting is a
/// semaphore wait plus a bag pop. The store therefore opts out of the built-in pool
/// (<c>Pooling=False</c>, applied by <see cref="Normalize"/>).
/// </para>
/// <para>
/// Every physical connection is checked against <see cref="SqliteVersionGuard"/> as it is
/// opened, so a SQLite library without <c>jsonb()</c> fails at store creation with an
/// actionable exception instead of at the first write with <c>no such function: jsonb</c>.
/// </para>
/// <para>
/// Connections are never closed while the pool is alive, only returned to the idle bag. That
/// is what keeps an in-memory database alive between operations: a shared-cache in-memory
/// database is destroyed when its last connection closes, so the pool eagerly opens one
/// connection at initialization and holds it until disposal.
/// </para>
/// </remarks>
internal sealed class SqliteConnectionPool : IDisposable, IAsyncDisposable
{
    private readonly DocumentStoreOptions _options;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentBag<SqliteConnection> _idle = [];
    private readonly SemaphoreSlim _slots;
    private int _created;
    private int _disposed;

    /// <summary>
    /// Initializes a pool over the supplied options. No connection is opened until
    /// <see cref="Initialize"/>/<see cref="InitializeAsync"/> or the first rent.
    /// </summary>
    public SqliteConnectionPool(
        DocumentStoreOptions options,
        IConnectionFactory connectionFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = Normalize(options);
        _connectionFactory = connectionFactory;
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxPoolSize, _options.MaxPoolSize);
    }

    /// <summary>
    /// Gets the maximum number of connections this pool will open.
    /// </summary>
    public int MaxPoolSize => _options.MaxPoolSize;

    /// <summary>
    /// Gets the number of physical connections opened so far.
    /// </summary>
    public int ConnectionCount => Volatile.Read(ref _created);

    /// <summary>
    /// Opens the first connection so that the database exists, the connection string is
    /// validated eagerly, and an in-memory database stays alive for the pool's lifetime.
    /// </summary>
    public void Initialize()
    {
        ThrowIfDisposed();
        _idle.Add(CreateConnection());
    }

    /// <inheritdoc cref="Initialize" />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _idle.Add(await CreateConnectionAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Rents a connection, waiting indefinitely for a free slot when the pool is saturated.
    /// </summary>
    public async ValueTask<PooledConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await TakeOrCreateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rents a connection, throwing rather than waiting past <paramref name="timeout"/>. Used
    /// by disposal, which must not hang on a leaked lease.
    /// </summary>
    public async ValueTask<PooledConnection> RentAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!await _slots.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for a free pooled connection (pool size {_options.MaxPoolSize}).");
        }

        return await TakeOrCreateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rents a connection synchronously, for the disposal path. A null timeout waits forever.
    /// </summary>
    public PooledConnection Rent(TimeSpan? timeout = null)
    {
        ThrowIfDisposed();

        if (!_slots.Wait(timeout ?? Timeout.InfiniteTimeSpan))
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for a free pooled connection (pool size {_options.MaxPoolSize}).");
        }

        try
        {
            ThrowIfDisposed();
            if (TryTakeIdle(out var idle))
            {
                return new PooledConnection(this, idle);
            }

            return new PooledConnection(this, CreateConnection());
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
    }

    // Runs with a slot already acquired, and hands it back if this throws — otherwise a failed
    // rent would shrink the pool permanently.
    private async ValueTask<PooledConnection> TakeOrCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfDisposed();
            if (TryTakeIdle(out var idle))
            {
                return new PooledConnection(this, idle);
            }

            return new PooledConnection(this, await CreateConnectionAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
    }

    private bool TryTakeIdle(out SqliteConnection connection)
    {
        while (_idle.TryTake(out var pooled))
        {
            if (pooled.State == ConnectionState.Open)
            {
                connection = pooled;
                return true;
            }

            DiscardBrokenConnection(pooled, $"state {pooled.State}");
        }

        connection = null!;
        return false;
    }

    /// <summary>
    /// Returns a rented connection to the idle bag, or closes it when the pool is disposed.
    /// </summary>
    internal void Return(SqliteConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            connection.Dispose();

            // Release anyway: a waiter parked when the pool was disposed only wakes on a free
            // slot, and then throws from ThrowIfDisposed instead of hanging.
            ReleaseSlot();
            return;
        }

        if (connection.State != ConnectionState.Open)
        {
            DiscardBrokenConnection(connection, $"state {connection.State}");
        }
        else
        {
            _idle.Add(connection);
        }

        ReleaseSlot();
    }

    /// <summary>
    /// Closes a rented connection instead of recycling it, for when its session state can no
    /// longer be trusted (for example a transaction that failed to roll back — recycling it
    /// would hand the next renter an open transaction).
    /// </summary>
    internal void Discard(SqliteConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            connection.Dispose();
            ReleaseSlot();
            return;
        }

        DiscardBrokenConnection(connection, "the caller reported it as no longer usable");
        ReleaseSlot();
    }

    // Safe after disposal: _slots is never disposed. See the remarks on Dispose.
    private void ReleaseSlot() => _slots.Release();

    /// <summary>
    /// Closes every pooled connection. Rented connections are closed when returned.
    /// </summary>
    /// <remarks>
    /// Do not dispose <c>_slots</c>. Disposing a <see cref="SemaphoreSlim"/> under a parked
    /// <c>WaitAsync</c> drops the waiter without completing it, hanging any operation queued
    /// for a connection. It holds no unmanaged resource here, so there is nothing to release.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_idle.TryTake(out var pooled))
        {
            try
            {
                pooled.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close a pooled connection during disposal");
            }
        }
    }

    /// <inheritdoc cref="Dispose" />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_idle.TryTake(out var pooled))
        {
            try
            {
                await pooled.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close a pooled connection during disposal");
            }
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connection = _connectionFactory.CreateConnection(_options);

        try
        {
            SqliteVersionGuard.EnsureSupported(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        var count = Interlocked.Increment(ref _created);
        _logger.LogDebug("Opened pooled connection {Count} of {MaxPoolSize}", count, _options.MaxPoolSize);
        return connection;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionFactory
            .CreateConnectionAsync(_options, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SqliteVersionGuard.EnsureSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var count = Interlocked.Increment(ref _created);
        _logger.LogDebug("Opened pooled connection {Count} of {MaxPoolSize}", count, _options.MaxPoolSize);
        return connection;
    }

    private void DiscardBrokenConnection(SqliteConnection connection, string reason)
    {
        Interlocked.Decrement(ref _created);
        _logger.LogWarning("Discarding a pooled connection: {Reason}", reason);

        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close a broken pooled connection");
        }
    }

    /// <summary>
    /// Opts the connection string out of Microsoft.Data.Sqlite's own pool, so that this pool
    /// owns the physical connections and their one-time PRAGMA configuration.
    /// </summary>
    private static DocumentStoreOptions Normalize(DocumentStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "Connection string must be set before creating a document store.",
                nameof(options));
        }

        var builder = new SqliteConnectionStringBuilder(options.ConnectionString) { Pooling = false };

        // A private in-memory database belongs to a single connection, so a pool of them would
        // give every operation its own empty database. Refuse it rather than silently losing
        // writes; a uniquely named shared-cache database has the same "private" semantics and
        // works across connections.
        if (IsPrivateInMemory(builder))
        {
            throw new ArgumentException(
                "A private in-memory database (\"Data Source=:memory:\" or Mode=Memory without " +
                "Cache=Shared) cannot be used by a document store, because the store pools " +
                "connections and each connection would get its own empty database. Use " +
                $"{nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.ForInMemory)}() for a " +
                $"private in-memory store, or {nameof(DocumentStoreOptions.ForSharedInMemory)}(name) " +
                "to share one by name.",
                nameof(options));
        }

        var normalized = options.Clone();
        normalized.ConnectionString = builder.ToString();
        return normalized;
    }

    private static bool IsPrivateInMemory(SqliteConnectionStringBuilder builder)
    {
        if (builder.Cache == SqliteCacheMode.Shared)
        {
            return false;
        }

        if (builder.Mode == SqliteOpenMode.Memory)
        {
            return true;
        }

        var dataSource = builder.DataSource;
        return dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)
                && !dataSource.Contains("cache=shared", StringComparison.OrdinalIgnoreCase));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(SqliteConnectionPool));
}
