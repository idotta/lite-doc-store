namespace LiteDocumentStore;

/// <summary>
/// A unit of work over one SQLite connection. Every operation invoked on it participates in
/// the same transaction and is committed or rolled back together.
/// </summary>
/// <remarks>
/// <para>
/// A transaction holds one connection from the store's pool for its whole lifetime, so it must
/// be disposed. Disposing without a prior <see cref="CommitAsync"/> rolls back.
/// </para>
/// <para>
/// Transactions are independent: two concurrent transactions run on two connections, so
/// neither can see or roll back the other's writes. Operations invoked on the
/// <see cref="IDocumentStore"/> itself never join an open transaction — use the transaction
/// object for that.
/// </para>
/// <example>
/// <code>
/// await using var tx = await store.BeginTransactionAsync();
/// await tx.UpsertAsync(order.Id, order);
/// await tx.PutBlobAsync(order.Id, invoicePdf);
/// await tx.CommitAsync();
/// </code>
/// </example>
/// </remarks>
public interface IDocumentTransaction : IDocumentOperations, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether this transaction has been committed.
    /// </summary>
    bool IsCommitted { get; }

    /// <summary>
    /// Commits the transaction and releases its connection.
    /// </summary>
    /// <remarks>
    /// If the commit itself fails, the transaction stays open and keeps its connection so that
    /// disposal can roll it back — so a failed commit must still be followed by disposal, which
    /// <c>await using</c> guarantees.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the commit</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the transaction was already committed or rolled back.
    /// </exception>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls the transaction back and releases its connection. Disposing an uncommitted
    /// transaction does the same, so calling this explicitly is optional.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the rollback</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the transaction was already committed or rolled back.
    /// </exception>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
