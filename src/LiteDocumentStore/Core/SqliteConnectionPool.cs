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
/// actionable exception instead of at the first write with <c>no such function: jsonb</c>,
/// and against <see cref="SqlitePageSizeGuard"/>, so a <see cref="DocumentStoreOptions.PageSize"/>
/// the database silently ignored fails there too.
/// </para>
/// <para>
/// A returned connection normally goes straight back to the idle bag rather than being closed,
/// which is what keeps an in-memory database alive between operations: a shared-cache in-memory
/// database is destroyed when its last connection closes, so the pool eagerly opens one
/// connection at initialization and holds it until disposal. It is closed instead when it comes
/// back unusable — a state other than Open, a caller reporting it through
/// <see cref="Discard"/>, or transaction state left on it (see <see cref="SqliteSessionState"/>
/// and <see cref="ReturnAfterExternalAccess"/>).
/// </para>
/// </remarks>
internal sealed class SqliteConnectionPool : IDisposable, IAsyncDisposable
{
    private readonly DocumentStoreOptions _options;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentBag<SqliteConnection> _idle = [];
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _blobStreamSlots;
    private int _created;
    private int _disposed;

    /// <summary>
    /// How long a caller waits for a blob read stream slot before the wait is treated as
    /// exhaustion rather than contention. Bounded so that streams leaked by one caller surface as
    /// a diagnosable timeout instead of a hang.
    /// </summary>
    internal static readonly TimeSpan BlobStreamSlotTimeout = TimeSpan.FromSeconds(30);

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
        _blobStreamSlots = new SemaphoreSlim(_options.MaxPoolSize, _options.MaxPoolSize);
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
    /// <remarks>
    /// A connection with a transaction still pending is closed rather than banked: recycling it
    /// would hand the next renter an open transaction, whose statements silently enlist in it.
    /// Only the cheap half of the check runs here — see
    /// <see cref="SqliteSessionState.HasPendingTransaction"/> for its cost and
    /// <see cref="ReturnAfterExternalAccess"/> for the paths that pay for both halves.
    /// </remarks>
    internal void Return(SqliteConnection connection) => ReturnCore(connection, externalAccess: false);

    /// <summary>
    /// Returns a connection a caller has run their own SQL against, closing it when either half
    /// of its transaction state is dirty.
    /// </summary>
    /// <remarks>
    /// The extra check over <see cref="Return"/> is
    /// <see cref="SqliteSessionState.HasManagedTransaction"/>, which costs ~223 ns and 192 bytes
    /// and catches what SQLite's own autocommit flag cannot: a transaction object the provider
    /// still has attached after a raw <c>COMMIT</c> or <c>ROLLBACK</c>. Only the raw-connection
    /// paths pay it — <c>ExecuteRawAsync</c> and a migration's own SQL — so the ~20 document
    /// operations keep the cheap check alone.
    /// </remarks>
    internal void ReturnAfterExternalAccess(SqliteConnection connection) =>
        ReturnCore(connection, externalAccess: true);

    private void ReturnCore(SqliteConnection connection, bool externalAccess)
    {
        if (connection is null)
        {
            return;
        }

        // The slot is released whatever happens below. A waiter parked in RentAsync only wakes on
        // a free slot, so a throw on this path — a Dispose that fails to roll back, a probe on a
        // handle that has gone away — would hang it forever rather than surfacing anything.
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Close it and release anyway: a waiter parked when the pool was disposed only
                // wakes on a free slot, and then throws from ThrowIfDisposed instead of hanging.
                // Not DiscardBrokenConnection — disposal's own drain loop does not adjust
                // ConnectionCount either, and a shutdown is not a broken connection.
                CloseQuietly(connection);
                return;
            }

            if (connection.State != ConnectionState.Open)
            {
                DiscardBrokenConnection(connection, $"state {connection.State}");
                return;
            }

            // Only the probes are guarded, and the guard decides rather than cleans up: whatever
            // it concludes, the connection is disposed of exactly once below. Nothing here is
            // allowed to throw — this runs from a lease disposal, so an exception would replace
            // whatever is already in flight, including the one a caller's own callback threw.
            if (IsSessionDirty(connection, externalAccess, out var reason))
            {
                DiscardBrokenConnection(connection, reason);
            }
            else
            {
                _idle.Add(connection);
            }
        }
        finally
        {
            ReleaseSlot();
        }
    }

    /// <summary>
    /// Runs the session-state probes, treating a probe that throws as a dirty verdict.
    /// </summary>
    /// <remarks>
    /// A probe reads a live SQLite handle, so it can fail on a connection that went away
    /// underneath it. There is no caller to report that to — the operation has already
    /// finished — and a connection the pool cannot vouch for must not be recycled, so the
    /// failure is logged and answered as dirty.
    /// </remarks>
    private bool IsSessionDirty(SqliteConnection connection, bool externalAccess, out string reason)
    {
        try
        {
            return SqliteSessionState.IsSessionDirty(connection, externalAccess, out reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarningQuietly(ex, "Failed to verify a returned pooled connection");
            reason = "the pool could not verify its session state";
            return true;
        }
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

        // Same reason Return releases in a finally: closing a connection whose transaction cannot
        // be rolled back throws, and this runs from a lease disposal.
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                CloseQuietly(connection);
                return;
            }

            DiscardBrokenConnection(connection, "the caller reported it as no longer usable");
        }
        finally
        {
            ReleaseSlot();
        }
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
            CloseQuietly(pooled);
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
            await CloseQuietlyAsync(pooled).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Claims one of the <see cref="MaxPoolSize"/> slots for concurrently open blob read streams,
    /// throwing rather than waiting past <see cref="BlobStreamSlotTimeout"/>.
    /// </summary>
    /// <remarks>
    /// A separate bound from the operation slots, not a share of them: a blob read stream is held
    /// by the caller until disposed, so renting an operation connection for one would let a
    /// forgetful caller starve the whole store. Bounding them separately keeps the two from
    /// starving each other while still refusing to open connections without limit.
    /// </remarks>
    /// <exception cref="TimeoutException">Every slot is held by a stream that is still open</exception>
    public async Task<BlobStreamSlot> RentBlobStreamSlotAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!await _blobStreamSlots.WaitAsync(BlobStreamSlotTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Timed out after {BlobStreamSlotTimeout} waiting for a blob read stream slot " +
                $"({_options.MaxPoolSize} may be open at once). Dispose blob read streams promptly — " +
                "each holds a connection until it is disposed.");
        }

        return new BlobStreamSlot(this);
    }

    // Safe after disposal for the same reason as ReleaseSlot: the semaphore is never disposed,
    // and a stream can outlive the store that opened it.
    internal void ReleaseBlobStreamSlot() => _blobStreamSlots.Release();

    /// <summary>
    /// Opens a connection that this pool configures and guards but does not own or count. The
    /// caller disposes it, and must already hold a slot from
    /// <see cref="RentBlobStreamSlotAsync"/>.
    /// </summary>
    public async Task<SqliteConnection> CreateUnpooledConnectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await OpenGuardedConnectionAsync(cancellationToken).ConfigureAwait(false);
    }
    private SqliteConnection CreateConnection()
    {
        var connection = _connectionFactory.CreateConnection(_options);

        try
        {
            SqliteVersionGuard.EnsureSupported(connection);
            SqlitePageSizeGuard.EnsureApplied(connection, _options.PageSize);
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


    private async Task<SqliteConnection> OpenGuardedConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionFactory
            .CreateConnectionAsync(_options, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SqliteVersionGuard.EnsureSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
            await SqlitePageSizeGuard.EnsureAppliedAsync(connection, _options.PageSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenGuardedConnectionAsync(cancellationToken).ConfigureAwait(false);

        var count = Interlocked.Increment(ref _created);
        _logger.LogDebug("Opened pooled connection {Count} of {MaxPoolSize}", count, _options.MaxPoolSize);
        return connection;
    }

    private void DiscardBrokenConnection(SqliteConnection connection, string reason)
    {
        Interlocked.Decrement(ref _created);
        _logger.LogWarningQuietly("Discarding a pooled connection: {Reason}", reason);
        CloseQuietly(connection);
    }

    /// <summary>
    /// Closes a connection, logging rather than propagating a failure.
    /// </summary>
    /// <remarks>
    /// <c>SqliteConnection.Dispose</c> is not exception-free: it rolls back a pending
    /// transaction, and a connection whose transaction object is attached but whose SQLite
    /// transaction is already gone — what a raw <c>COMMIT</c> leaves behind — fails with
    /// <c>cannot rollback - no transaction is active</c>. Every close here runs while the pool is
    /// tidying up, often under an exception that matters more.
    /// </remarks>
    private void CloseQuietly(SqliteConnection connection)
    {
        try
        {
            connection.Dispose();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarningQuietly(ex, "Failed to close a pooled connection; retrying once");
        }

        // The one failure mode measured here clears itself: the first Dispose throws while
        // rolling back a transaction object that has nothing to roll back, but it detaches that
        // object on the way out, so a second attempt closes the connection. Without the retry the
        // handle — and its file lock — would survive until finalization.
        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarningQuietly(ex, "Failed to close a pooled connection");
        }
    }

    /// <inheritdoc cref="CloseQuietly" />
    private async ValueTask CloseQuietlyAsync(SqliteConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarningQuietly(ex, "Failed to close a pooled connection; retrying once");
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarningQuietly(ex, "Failed to close a pooled connection");
        }
    }

    /// <summary>
    /// Rejects a connection string the store cannot honour (see
    /// <see cref="SqliteConnectionStringGuard"/>) and opts the rest out of
    /// Microsoft.Data.Sqlite's own pool, so that this pool owns the physical connections and
    /// their one-time PRAGMA configuration.
    /// </summary>
    private static DocumentStoreOptions Normalize(DocumentStoreOptions options)
    {
        var builder = SqliteConnectionStringGuard.EnsureUsable(options, nameof(options));
        builder.Pooling = false;

        var normalized = options.Clone();
        normalized.ConnectionString = builder.ToString();
        return normalized;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(SqliteConnectionPool));
}
