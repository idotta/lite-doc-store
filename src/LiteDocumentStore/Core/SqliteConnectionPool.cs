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
/// A returned connection normally goes straight back to the idle bag rather than being closed. It
/// is closed instead when it comes back unusable — a state other than Open, a caller reporting it
/// through <see cref="Discard"/>, or transaction state left on it (see
/// <see cref="SqliteSessionState"/> and <see cref="ReturnAfterExternalAccess"/>).
/// </para>
/// <para>
/// What keeps a shared-cache in-memory database alive is <em>not</em> that: such a database is
/// destroyed when its last connection closes, and every one of those discard sites can close the
/// last one. <see cref="Initialize"/> therefore <strong>reserves</strong> the connection it opens
/// as a keeper — never leased, never discarded, never counted — so the leasable count can reach
/// zero harmlessly and no discard site needs a last-connection test.
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
    private readonly bool _needsKeeper;
    private SqliteConnection? _keeper;
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

        _options = Normalize(options, out _needsKeeper);
        _connectionFactory = connectionFactory;
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxPoolSize, _options.MaxPoolSize);
        _blobStreamSlots = new SemaphoreSlim(_options.MaxPoolSize, _options.MaxPoolSize);
    }

    /// <summary>
    /// Gets the cap on <em>leasable</em> pooled connections, which is not the most the store can
    /// hold open.
    /// </summary>
    /// <remarks>
    /// It bounds two budgets of this size independently — pooled connections and blob read stream
    /// connections — and a shared in-memory store keeps one reserved keeper besides. So a store
    /// holds up to <c>2 × MaxPoolSize + 1</c> connections: at <c>MaxPoolSize = 2</c>, five. That
    /// is the steady-state figure, not a hard ceiling on handles: <see cref="AbandonLease"/> gives
    /// a leaked transaction's slot back without closing its connection, so a replacement is opened
    /// while the abandoned handle waits on the provider's finalizer.
    /// </remarks>
    public int MaxPoolSize => _options.MaxPoolSize;

    /// <summary>
    /// Gets the number of leasable pooled connections the pool currently owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a running total of opens: it <em>decreases</em> when a connection is discarded or a
    /// leaked lease is abandoned. Three kinds of connection are deliberately excluded because the
    /// pool never hands them out — the in-memory <see cref="Initialize">keeper</see>, a blob read
    /// stream's connection, and one abandoned by <see cref="AbandonLease"/> and left to the
    /// provider's finalizer. So this is what the pool can lease, not how many handles exist.
    /// </para>
    /// <para>
    /// <strong>Meaningful only before disposal.</strong> <see cref="DrainIdle"/> closes the idle
    /// connections without decrementing, since nothing is going to lease them afterwards, so the
    /// value a disposed pool reports is whatever it held when disposal began and is stale by
    /// design. Treat it as undefined once <see cref="Dispose"/> has run.
    /// </para>
    /// </remarks>
    public int ConnectionCount => Volatile.Read(ref _created);

    /// <summary>
    /// Opens the first connection so that the database exists, the connection string is
    /// validated eagerly, and an in-memory database stays alive for the pool's lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a shared-cache in-memory database that connection is <strong>reserved as a keeper</strong>
    /// rather than banked: such a database is destroyed when its last connection closes, and every
    /// site that closes an established connection — the two discard branches in
    /// <see cref="ReturnCore"/>, <see cref="Discard"/>, and <see cref="TryTakeIdle"/> — can
    /// otherwise take the count to zero. Measured before it was reserved: a caller leaving a raw
    /// <c>BEGIN</c> in an <c>ExecuteRawAsync</c> callback made the guard discard the only
    /// connection and the whole database went with it, silently, while ordinary operations carried
    /// on against a fresh empty one.
    /// </para>
    /// <para>
    /// The keeper is never leased, never discarded and never counted in
    /// <see cref="ConnectionCount"/>: it is un-pooled, the same category as a blob read stream's
    /// connection. That is what lets every discard site keep closing connections unconditionally —
    /// there is no "am I the last?" test anywhere, so there is nothing to race. A file database
    /// needs none of this and gets none: no keeper, no extra handle, no changed path.
    /// </para>
    /// </remarks>
    public void Initialize()
    {
        ThrowIfDisposed();

        if (_needsKeeper)
        {
            ReserveKeeper(CreateConnection());
            return;
        }

        _idle.Add(CreateConnection());
        DrainIfDisposed();
    }

    /// <inheritdoc cref="Initialize" />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (_needsKeeper)
        {
            ReserveKeeper(connection);
            return;
        }

        _idle.Add(connection);
        DrainIfDisposed();
    }

    /// <summary>
    /// Rents a connection, waiting up to <see cref="DocumentStoreOptions.PoolWaitTimeoutMs"/> for
    /// a free slot when the pool is saturated.
    /// </summary>
    /// <remarks>
    /// The wait is bounded for the reason the blob-stream budget's is
    /// (<see cref="RentBlobStreamSlotAsync"/>): a slot is held until its lease is released, so a
    /// caller who leaks a transaction loses one — and <see cref="MaxPoolSize"/> such leaks would
    /// hang every later operation forever, with no exception, no log and no metric. The bound
    /// turns that into a diagnosable failure. <see cref="Timeout.Infinite"/> restores the
    /// unbounded wait for a caller who wants it.
    /// </remarks>
    public async ValueTask<PooledConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!await _slots.WaitAsync(_options.PoolWaitTimeoutMs, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Timed out after {_options.PoolWaitTimeoutMs} ms " +
                $"({nameof(DocumentStoreOptions.PoolWaitTimeoutMs)}) waiting for a free pooled " +
                $"connection (pool size {_options.MaxPoolSize}). Dispose transactions promptly — " +
                "each holds a connection until it is committed, rolled back or disposed.");
        }

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

            return new PooledConnection(this, FreshOrThrowIfDisposed(CreateConnection()));
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

            return new PooledConnection(
                this,
                FreshOrThrowIfDisposed(await CreateConnectionAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
    }

    /// <summary>
    /// Re-checks disposal after a connection has been opened, and closes it rather than handing it
    /// out when the pool was disposed while the open was in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rent passes <see cref="ThrowIfDisposed"/> and can still be inside the connection factory
    /// when <see cref="Dispose"/> runs. Without this the renter is handed a connection opened
    /// <em>after</em> the keeper closed — and for a shared-cache in-memory store that connection
    /// has just recreated the database <strong>empty</strong>, which is the same silent loss the
    /// keeper exists to prevent, merely moved to disposal. Ordering alone cannot close it: the open
    /// is arbitrarily long, and it is a caller-supplied <see cref="IConnectionFactory"/>.
    /// </para>
    /// <para>
    /// Re-reading the flag after the open is the same shape as <see cref="DrainIfDisposed"/> — one
    /// volatile read, no lock, on a path that has just paid for a physical connection — and it
    /// cannot deadlock against the one-shot <c>_disposed</c> exchange because it takes nothing:
    /// <see cref="Dispose"/> never waits for a rent, and this never waits for disposal. Whichever
    /// runs first, the connection is closed exactly once, here or by the drain.
    /// </para>
    /// </remarks>
    private SqliteConnection FreshOrThrowIfDisposed(SqliteConnection connection)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            return connection;
        }

        DiscardBrokenConnection(connection, "the pool was disposed while the connection was opening");
        ThrowIfDisposed();
        return connection;
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
                DrainIfDisposed();
            }
        }
        finally
        {
            ReleaseSlot();
        }
    }

    /// <summary>
    /// Closes anything left in the idle bag once the pool has been disposed, for a connection
    /// banked after disposal's own drain had already run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every path that banks a connection checks <c>_disposed</c> first, but that check alone only
    /// narrows the window: <see cref="Dispose"/> flips the flag and drains the bag exactly once, so
    /// an add that lands after that drain leaves the connection — and its file lock, or a
    /// shared-cache in-memory database — open until finalization. Measured at 313 of 400
    /// barrier-synchronized attempts (one thread disposing the lease, one the pool,
    /// <see cref="MaxPoolSize"/> of 1), so it is the common ordering rather than a rare one.
    /// </para>
    /// <para>
    /// Re-reading the flag <em>after</em> the add closes it: whichever racer gets there,
    /// <see cref="ConcurrentBag{T}.TryTake"/> hands the connection out exactly once, so it is
    /// closed exactly once. The happy path pays one volatile read rather than a lock on the
    /// per-operation return path, whose cost the connection model depends on.
    /// </para>
    /// </remarks>
    private void DrainIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            DrainIdle();
        }
    }

    // Not DiscardBrokenConnection: disposal does not adjust ConnectionCount either, and a
    // shutdown is not a broken connection.
    private void DrainIdle()
    {
        while (_idle.TryTake(out var pooled))
        {
            CloseQuietly(pooled);
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
    /// Gives up a lease whose connection can no longer be touched — a transaction reached from
    /// its finalizer — recovering the slot without closing or counting the connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A finalizer must not touch the provider's objects: they have their own finalizers, and the
    /// SafeHandle behind the connection closes the database whether or not this runs. So the
    /// connection is left to the provider and only the budget is repaired — the trade
    /// <see cref="BlobStreamSlot"/> already makes, and the reason a leak costs a handle until
    /// finalization rather than a slot forever.
    /// </para>
    /// <para>
    /// The uncount runs first, so a waiter woken by the release does not briefly see the pool
    /// counting a connection it no longer owns. <see cref="ConnectionCount"/> then under-reports
    /// live handles until the provider finalizes the abandoned one, which is the same sense
    /// <see cref="DiscardBrokenConnection"/> uses: it counts the connections the pool owns.
    /// </para>
    /// </remarks>
    internal void AbandonLease()
    {
        Interlocked.Decrement(ref _created);
        ReleaseSlot();
    }

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

        DrainIdle();

        // Last, and through CloseQuietly: a throwing close here must not abandon the idle bag,
        // and a keeper that outlives disposal is both a leaked handle and an in-memory database
        // still alive under a name the caller has finished with.
        CloseKeeper();
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

        // See Dispose: the keeper goes last.
        var keeper = Interlocked.Exchange(ref _keeper, null);

        if (keeper is not null)
        {
            await CloseQuietlyAsync(keeper).ConfigureAwait(false);
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
        return FreshUnpooledOrThrowIfDisposed(
            await OpenGuardedConnectionAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The unpooled twin of <see cref="FreshOrThrowIfDisposed"/>, for connections that were never
    /// counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same race, same consequence: the check before the open cannot see a disposal that happens
    /// during it, and a caller-supplied <see cref="IConnectionFactory"/> makes that window
    /// arbitrarily long. For a shared-cache in-memory store the connection handed back would then
    /// be one that had just <strong>recreated the database empty</strong> after the keeper closed,
    /// and a blob read stream would query that rather than fail — the silent loss the keeper exists
    /// to prevent, on the one creation path the pooled guard does not cover.
    /// </para>
    /// <para>
    /// It must not go through <see cref="DiscardBrokenConnection"/>. Connections from
    /// <see cref="OpenGuardedConnectionAsync"/> never reach <see cref="Announce"/>, so they were
    /// never added to <c>_created</c>; uncounting one here would push
    /// <see cref="ConnectionCount"/> <em>below</em> the number of pooled connections the pool
    /// actually holds. Closing without uncounting is the whole difference between the two.
    /// </para>
    /// </remarks>
    private SqliteConnection FreshUnpooledOrThrowIfDisposed(SqliteConnection connection)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            return connection;
        }

        CloseQuietly(connection);
        ThrowIfDisposed();
        return connection;
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

        return Announce(connection);
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

        return Announce(connection);
    }

    /// <summary>
    /// Counts a newly opened connection and reports it, closing it again if the report fails.
    /// </summary>
    /// <remarks>
    /// The log stays loud, unlike the release paths in <see cref="QuietLog"/>: a caller is waiting
    /// on this rent and should learn that their <see cref="ILogger"/> is broken. What must not
    /// happen is the connection going with it — it is a local here, so a throw between opening it
    /// and returning it abandons an open handle (and its file lock, or a shared-cache in-memory
    /// database) until finalization, and leaves <see cref="ConnectionCount"/> counting a
    /// connection the pool no longer has. The slot is recovered either way by the caller's
    /// <c>catch</c>, so the failure is otherwise invisible and repeats on every retry.
    /// </remarks>
    private SqliteConnection Announce(SqliteConnection connection)
    {
        var count = Interlocked.Increment(ref _created);

        try
        {
            _logger.LogDebug("Opened pooled connection {Count} of {MaxPoolSize}", count, _options.MaxPoolSize);
        }
        catch
        {
            DiscardBrokenConnection(connection, "the logger failed while reporting that it was opened");
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Holds the connection open for the pool's lifetime so a shared-cache in-memory database
    /// cannot be destroyed by a discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposed re-check is the same one every banking path runs, and for a sharper reason
    /// here: <see cref="Dispose"/> flips the flag and closes the keeper exactly once, so a keeper
    /// assigned after that close would be a handle nothing ever closes — and, worse, would hold a
    /// database alive that the caller believes is gone. Losing the race means closing this
    /// connection instead, which is what disposal would have done anyway.
    /// </para>
    /// <para>
    /// Exchanging rather than assigning answers the same failure by the other route. A keeper
    /// overwritten by a second <see cref="Initialize"/> is in neither the idle bag nor this field,
    /// so nothing can ever close it and it holds the named database alive past disposal — exactly
    /// the end state the re-check above exists to refuse. No path reaches a second initialization
    /// (the factory calls it once, on a store it has just constructed), and the guard is kept
    /// anyway for the reason <c>DocumentStoreTransaction.Release</c>'s own unreachable catch is
    /// kept: holding "a keeper is never lost" here is cheaper than re-deriving it from
    /// <see cref="Initialize"/>'s callers every time they change.
    /// </para>
    /// </remarks>
    private void ReserveKeeper(SqliteConnection connection)
    {
        // Uncount it: ConnectionCount counts the connections the pool can hand out, which is the
        // sense DiscardBrokenConnection and AbandonLease already use, and the keeper is never one
        // of them. Counting it would also make the keeper look like it occupies a slot.
        Interlocked.Decrement(ref _created);

        // Install first, close second: the replacement is already open, so the database never
        // stands on the connection being closed here.
        var previous = Interlocked.Exchange(ref _keeper, connection);

        if (previous is not null)
        {
            CloseQuietly(previous);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            CloseKeeper();
        }
    }

    private void CloseKeeper()
    {
        var keeper = Interlocked.Exchange(ref _keeper, null);

        if (keeper is not null)
        {
            CloseQuietly(keeper);
        }
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
    private static DocumentStoreOptions Normalize(DocumentStoreOptions options, out bool needsKeeper)
    {
        var builder = SqliteConnectionStringGuard.EnsureUsable(options, nameof(options));
        needsKeeper = SqliteConnectionStringGuard.IsSharedInMemory(builder);
        builder.Pooling = false;

        var normalized = options.Clone();
        normalized.ConnectionString = builder.ToString();
        return normalized;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(SqliteConnectionPool));
}
