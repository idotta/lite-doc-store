using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// A rented connection from a <see cref="SqliteConnectionPool"/>. Disposing the lease
/// returns the connection to the pool; it never closes the connection.
/// </summary>
/// <remarks>
/// This is a struct so that renting a connection for a single document operation does not
/// allocate. Dispose exactly once — a lease is not reference-counted.
/// </remarks>
internal readonly struct PooledConnection : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnectionPool _pool;

    internal PooledConnection(SqliteConnectionPool pool, SqliteConnection connection)
    {
        _pool = pool;
        Connection = connection;
    }

    /// <summary>
    /// Gets the rented connection. Valid until this lease is disposed.
    /// </summary>
    public SqliteConnection Connection { get; }

    /// <summary>
    /// Returns the connection to the pool.
    /// </summary>
    public void Dispose() => _pool?.Return(Connection);

    /// <summary>
    /// Closes the connection instead of returning it, for when its session state can no longer
    /// be trusted — a transaction that failed to roll back, for instance.
    /// </summary>
    public void Discard() => _pool?.Discard(Connection);

    /// <summary>
    /// Gives the slot back without touching the connection, for a lease reached from a finalizer.
    /// </summary>
    /// <remarks>
    /// The connection is left to the provider's own finalizers — see
    /// <see cref="SqliteConnectionPool.AbandonLease"/>. Never call this from a disposal path:
    /// <see cref="Dispose"/> and <see cref="Discard"/> are the paths that actually reclaim the
    /// connection.
    /// </remarks>
    public void Abandon() => _pool?.AbandonLease();

    /// <summary>
    /// Ends a lease whose connection a caller has run their own SQL against, closing the
    /// connection rather than recycling it.
    /// </summary>
    /// <remarks>
    /// Use instead of <see cref="Dispose"/> wherever the raw connection was handed out — an
    /// <c>ExecuteRawAsync</c> callback or a migration's own SQL. The close is unconditional and
    /// costs one physical open on the next rent; see
    /// <see cref="SqliteConnectionPool.ReturnAfterExternalAccess"/> for why nothing cheaper is
    /// correct.
    /// </remarks>
    public void ReturnAfterExternalAccess() => _pool?.ReturnAfterExternalAccess(Connection);

    /// <summary>
    /// Returns the connection to the pool.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _pool?.Return(Connection);
        return ValueTask.CompletedTask;
    }
}
