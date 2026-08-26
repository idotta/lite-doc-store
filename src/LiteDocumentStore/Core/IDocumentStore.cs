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
/// <see cref="BeginTransactionAsync(CancellationToken)"/> or
/// <see cref="ExecuteInTransactionAsync(Func{IDocumentTransaction, Task}, CancellationToken)"/>
/// and invoke the operations on the returned <see cref="IDocumentTransaction"/>.
/// </para>
/// </remarks>
public interface IDocumentStore : IDocumentOperations, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Starts a unit of work on a dedicated connection. Operations invoked on the returned
    /// transaction are committed or rolled back together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transaction holds one connection from the pool until it is disposed, so dispose it
    /// promptly — <c>await using</c> is the intended usage. Disposing without committing rolls
    /// back.
    /// </para>
    /// <para>
    /// Starts in <see cref="TransactionMode.Deferred"/>. A unit of work that <b>reads and then
    /// writes</b> should use
    /// <see cref="BeginTransactionAsync(TransactionMode, CancellationToken)"/> with
    /// <see cref="TransactionMode.Immediate"/> instead — see that enum for why.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    /// <returns>A new transaction</returns>
    Task<IDocumentTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="BeginTransactionAsync(CancellationToken)" />
    /// <param name="mode">Whether <c>BEGIN</c> takes the write lock up front</param>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    /// <returns>A new transaction</returns>
    Task<IDocumentTransaction> BeginTransactionAsync(
        TransactionMode mode,
        CancellationToken cancellationToken = default);

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
    /// waiting for a lock only this transaction can release — so it blocks and then throws
    /// <c>SQLITE_BUSY</c> ("database is locked"). It blocks for
    /// max(<see cref="DocumentStoreOptions.BusyTimeoutMs"/>, the connection's command timeout);
    /// the store aligns the second with the first unless the connection string states
    /// <c>Default Timeout</c> / <c>Command Timeout</c>. Reads through the store are fine in WAL
    /// mode.
    /// </para>
    /// <para>
    /// Runs in <see cref="TransactionMode.Deferred"/>. A callback that reads and then writes
    /// should use
    /// <see cref="ExecuteInTransactionAsync(Func{IDocumentTransaction, Task}, TransactionMode, CancellationToken)"/>
    /// with <see cref="TransactionMode.Immediate"/> instead — see that enum for why.
    /// </para>
    /// </remarks>
    /// <param name="action">The work to run transactionally</param>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteInTransactionAsync(Func{IDocumentTransaction, Task}, CancellationToken)" />
    /// <param name="action">The work to run transactionally</param>
    /// <param name="mode">Whether <c>BEGIN</c> takes the write lock up front</param>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        TransactionMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the store can reach the database and that SQLite is new enough for JSONB
    /// (3.45+).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the check</param>
    /// <returns>True when the store is usable; false instead of throwing on any failure</returns>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every supplied migration that is not already recorded in the history table, in
    /// ascending version order, under <see cref="MigrationOptions.Default"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole run holds one pooled connection, but each migration commits in its own
    /// <c>BEGIN IMMEDIATE</c> transaction: if versions 1 and 2 commit and 3 throws, 1 and 2 stay
    /// applied. "Already applied" is decided by the version being present in the history table,
    /// not by comparing against the highest applied version, so a back-filled migration is not
    /// silently skipped — it is rejected (see <see cref="MigrationOptions.AllowOutOfOrder"/>).
    /// </para>
    /// <para>
    /// Safe to call from two processes at once: the second waits for the first to commit, then
    /// re-reads the history table under the same write lock and reports the migration as
    /// already applied.
    /// </para>
    /// <para>
    /// Migrations are not available on <see cref="IDocumentTransaction"/>: a migration owns its
    /// own transaction.
    /// </para>
    /// </remarks>
    /// <param name="migrations">The migrations to apply; order is irrelevant, versions must be unique</param>
    /// <param name="cancellationToken">A token to cancel the run</param>
    /// <returns>The number of migrations applied</returns>
    /// <exception cref="ArgumentException">A migration is null, or two share a version</exception>
    /// <exception cref="Exceptions.MigrationOutOfOrderException">
    /// A migration that has never been applied sits below the highest applied version
    /// </exception>
    /// <exception cref="Exceptions.MigrationChecksumMismatchException">
    /// An applied migration was edited since it was applied
    /// </exception>
    Task<int> MigrateAsync(IEnumerable<IMigration> migrations, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="MigrateAsync(IEnumerable{IMigration}, CancellationToken)" />
    /// <param name="migrations">The migrations to apply; order is irrelevant, versions must be unique</param>
    /// <param name="options">The out-of-order and checksum policy for this run</param>
    /// <param name="cancellationToken">A token to cancel the run</param>
    Task<int> MigrateAsync(
        IEnumerable<IMigration> migrations,
        MigrationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every applied migration, ordered by version.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read</param>
    /// <returns>The history rows, including the checksum recorded with each</returns>
    Task<IReadOnlyList<MigrationHistoryRecord>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the highest applied migration version, or 0 when none have been applied.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read</param>
    /// <returns>The highest applied version</returns>
    Task<long> GetCurrentMigrationVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back every applied migration above <paramref name="targetVersion"/>, newest first.
    /// </summary>
    /// <remarks>
    /// Refuses the whole operation when any migration in the rollback range has no definition in
    /// <paramref name="migrations"/> — a partial rollback would leave the schema and the history
    /// table inconsistent. Checksums are never verified on this path, so a migration whose down
    /// SQL was edited still rolls back.
    /// </remarks>
    /// <param name="targetVersion">The version to roll back to; 0 rolls back everything</param>
    /// <param name="migrations">The available migration definitions</param>
    /// <param name="cancellationToken">A token to cancel the run</param>
    /// <returns>The number of migrations rolled back</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetVersion"/> is negative</exception>
    /// <exception cref="Exceptions.LiteDocumentStoreException">
    /// A migration in the rollback range has no definition
    /// </exception>
    Task<int> RollbackToVersionAsync(
        long targetVersion,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default);
}
