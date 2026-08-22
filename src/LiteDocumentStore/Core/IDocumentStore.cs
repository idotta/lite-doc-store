namespace LiteDocumentStore;

/// <summary>
/// A hybrid document + relational store over a single SQLite database. Documents are stored
/// as SQLite JSONB, and the same tables stay reachable from raw SQL via
/// <see cref="IDocumentOperations.ExecuteRawAsync{TResult}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> A store is safe to share across threads and requests. It owns a pool
/// of SQLite connections and rents one per operation, so concurrent callers never share a
/// connection. Register it as a singleton; one store per database.
/// </para>
/// <para>
/// Because each operation runs on its own connection, operations invoked directly on the store
/// each commit on their own. To make several writes atomic, use
/// <see cref="BeginTransactionAsync"/> or <see cref="ExecuteInTransactionAsync"/> and invoke
/// the operations on the returned <see cref="IDocumentTransaction"/>.
/// </para>
/// </remarks>
public interface IDocumentStore : IDocumentOperations, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Starts a unit of work on a dedicated connection. Operations invoked on the returned
    /// transaction are committed or rolled back together.
    /// </summary>
    /// <remarks>
    /// The transaction holds one connection from the pool until it is disposed, so dispose it
    /// promptly — <c>await using</c> is the intended usage. Disposing without committing rolls
    /// back.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    /// <returns>A new transaction</returns>
    Task<IDocumentTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> inside a transaction, committing when it returns and
    /// rolling back when it throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only operations invoked on the supplied <see cref="IDocumentTransaction"/> are part of
    /// the transaction. Operations invoked on the store inside the callback run on their own
    /// connections and commit independently.
    /// </para>
    /// <para>
    /// <b>Do not write through the store from inside the callback.</b> Once the transaction has
    /// written, it holds the database's write lock, and a store write is a second connection
    /// waiting for a lock only this transaction can release — so it blocks for
    /// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> and then throws
    /// <c>SQLITE_BUSY</c> ("database is locked"). Reads through the store are fine in WAL mode.
    /// </para>
    /// </remarks>
    /// <param name="action">The work to run transactionally</param>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the store can reach the database and that SQLite is new enough for JSONB
    /// (3.45+).
    /// </summary>
    /// <returns>True when the store is usable; false instead of throwing on any failure</returns>
    Task<bool> IsHealthyAsync();
}
