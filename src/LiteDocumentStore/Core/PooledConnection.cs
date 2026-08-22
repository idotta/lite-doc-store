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
    /// Returns the connection to the pool.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _pool?.Return(Connection);
        return ValueTask.CompletedTask;
    }
}
