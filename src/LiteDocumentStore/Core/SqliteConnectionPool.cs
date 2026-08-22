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
    /// Rents a connection, waiting for a free slot when the pool is saturated.
    /// </summary>
    public async ValueTask<PooledConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            while (_idle.TryTake(out var pooled))
            {
                if (pooled.State == ConnectionState.Open)
                {
                    return new PooledConnection(this, pooled);
                }

                DiscardBrokenConnection(pooled, $"state {pooled.State}");
            }

            return new PooledConnection(this, await CreateConnectionAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
    }

    /// <summary>
    /// Rents a connection synchronously. Used on the disposal path, where async is not
    /// available.
    /// </summary>
    public PooledConnection Rent()
    {
        ThrowIfDisposed();
        _slots.Wait();

        try
        {
            ThrowIfDisposed();
            while (_idle.TryTake(out var pooled))
            {
                if (pooled.State == ConnectionState.Open)
                {
                    return new PooledConnection(this, pooled);
                }

                DiscardBrokenConnection(pooled, $"state {pooled.State}");
            }

            return new PooledConnection(this, CreateConnection());
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
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
            return;
        }

        DiscardBrokenConnection(connection, "the caller reported it as no longer usable");
        ReleaseSlot();
    }

    /// <summary>
    /// Releases a rent slot, tolerating a pool that was disposed concurrently.
    /// </summary>
    private void ReleaseSlot()
    {
        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // The pool was disposed while this rent was in flight; the slot no longer matters.
        }
    }

    /// <summary>
    /// Closes every pooled connection. Rented connections are closed when returned.
    /// </summary>
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

        _slots.Dispose();
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

        _slots.Dispose();
    }

    private SqliteConnection CreateConnection()
    {
        var connection = _connectionFactory.CreateConnection(_options);
        var count = Interlocked.Increment(ref _created);
        _logger.LogDebug("Opened pooled connection {Count} of {MaxPoolSize}", count, _options.MaxPoolSize);
        return connection;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connectionFactory.CreateConnectionAsync(_options).ConfigureAwait(false);
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
