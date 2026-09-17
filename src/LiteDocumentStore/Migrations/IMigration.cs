namespace LiteDocumentStore;

/// <summary>
/// Defines a database schema migration with version control and up/down support.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Gets the unique version identifier for this migration.
    /// Migrations are applied in ascending order by version.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// Gets a descriptive name for this migration.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a checksum of this migration's applied state, or null to opt out of drift
    /// detection. It is recorded with the history row and compared on every later run, so an
    /// edit to an already-applied migration is reported rather than silently ignored.
    /// </summary>
    /// <remarks>
    /// Only the <em>up</em> definition should be covered: the down definition is not part of
    /// what was applied, so editing it must not fail a startup migration. <see cref="Migration"/>
    /// returns an uppercase SHA-256 hex digest of its UTF-8 up SQL. An implementation that
    /// returns null is never verified, which is what keeps history written before checksums
    /// existed usable.
    /// </remarks>
    string? Checksum => null;

    /// <summary>
    /// Applies the migration (upgrade operation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection arrives <strong>inside an open <c>BEGIN IMMEDIATE</c> transaction owned by
    /// the runner</strong>, which commits it: this method's SQL and the history row that records
    /// the migration are one atomic unit, which is what makes a migration that throws while that
    /// transaction is intact leave nothing behind. So do not open, commit or roll back a
    /// transaction on the connection.
    /// <c>connection.BeginTransaction()</c> throws <c>SqliteConnection does not support nested
    /// transactions</c>, which is loud; a bare <c>COMMIT</c> is not. Ending the runner's transaction
    /// early makes this method's work and the history row <strong>separately durable rather than
    /// one unit</strong>, and what follows is then no longer under the runner's control: the run can
    /// throw on the way out with the version already recorded, in which case a retry reports the
    /// migration as already applied and never re-runs it. Nothing about that end state is
    /// guaranteed — which is the point. It is the one misuse here whose outcome the caller cannot
    /// predict from the exception they are handed.
    /// </para>
    /// <para>
    /// Build commands with <c>connection.CreateCommand()</c>, which copies the active transaction
    /// onto them. A directly constructed <see cref="Microsoft.Data.Sqlite.SqliteCommand"/> has no
    /// transaction and will not execute while one is pending — it fails with <c>Execute requires
    /// the command to have a transaction object…</c>.
    /// </para>
    /// <para>
    /// This connection is the <strong>whole run's only leased connection</strong>, so a migration
    /// must not call back into the store that is running it. At
    /// <see cref="DocumentStoreOptions.MaxPoolSize"/> = 1 such a call waits for a connection the
    /// run itself is holding: it fails once <see cref="DocumentStoreOptions.PoolWaitTimeoutMs"/>
    /// elapses, with a <see cref="TimeoutException"/> whose message blames undisposed transactions
    /// and so misdiagnoses this case, or hangs when that option is
    /// <see cref="Timeout.Infinite"/>. If it obtains another lease, it runs on a
    /// <strong>different</strong> pooled connection, so it is not enlisted in this transaction and
    /// is not covered by its atomicity —
    /// which is the part that matters here. It may also block or fail on SQLite's locks: a write
    /// contends with the write lock this transaction holds and can surface <c>SQLITE_BUSY</c>
    /// (<c>database is locked</c>), measured at about 3.6 s with
    /// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> = 1500 at the default pool size.
    /// </para>
    /// <para>
    /// Statements SQLite prohibits inside a transaction belong outside the runner, in
    /// <see cref="IDocumentOperations.ExecuteRawAsync{TResult}"/>, run before or after the
    /// migration rather than within it. <c>VACUUM</c> fails loudly here (<c>cannot VACUUM from
    /// within a transaction</c>). <c>PRAGMA foreign_keys</c> does not fail at all: it is
    /// <strong>silently ignored</strong> inside the transaction, leaving the connection with
    /// whatever value it already had — so on a store configured
    /// <see cref="DocumentStoreOptions.EnableForeignKeys"/> = <c>true</c>, which is the default,
    /// <c>PRAGMA foreign_keys = OFF</c> is accepted, reads back <c>1</c> and leaves foreign keys
    /// enforced — so a table rebuild written on the assumption that they are suspended is not
    /// running under that assumption.
    /// When such a <c>PRAGMA</c> is set through <c>ExecuteRawAsync</c> instead, restore it before
    /// that callback returns — along with any other connection-local state the callback changed,
    /// since an <c>ATTACH</c>ed database and a <c>TEMP</c> table were measured to survive on the
    /// pooled connection too, and anything else living on the connection rather than in the file
    /// behaves the same way. The store resets none of it.
    /// </para>
    /// </remarks>
    /// <param name="connection">
    /// The SQLite connection to execute the migration on, already inside the runner's transaction
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task UpAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts the migration (downgrade operation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four <em>restrictions</em> in <see cref="UpAsync"/>'s remarks apply here unchanged, and
    /// for the same reasons: do not open, commit or roll back a transaction on the connection;
    /// build commands with <c>connection.CreateCommand()</c>; do not call back into the store,
    /// since this is the run's only pooled connection; and keep statements SQLite prohibits inside
    /// a transaction out of the runner. Measured on this path too — a nested transaction and a
    /// <c>VACUUM</c> both throw, and <c>PRAGMA foreign_keys</c> is silently ignored.
    /// </para>
    /// <para>
    /// The <em>consequences</em> are this path's own, because the transaction here is atomic with
    /// the <c>DELETE</c> of the history row rather than an <c>INSERT</c> of it. If this method
    /// throws while the runner's transaction is still intact, the migration is left
    /// <strong>still applied</strong> — its history row survives — not "nothing behind". The
    /// exception is the misuse below: ending the transaction early with a bare <c>COMMIT</c>
    /// separates the down work from the removal of that row, so the migration can end up recorded as
    /// <strong>not</strong> applied while the run reports failure — the mirror image of the up
    /// path's shape, and equally unpredictable from the exception.
    /// </para>
    /// </remarks>
    /// <param name="connection">
    /// The SQLite connection to execute the rollback on, already inside the runner's transaction
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task DownAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken = default);
}
