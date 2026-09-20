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
    /// <remarks>
    /// It must be greater than zero. Every non-positive version is rejected with an
    /// <see cref="ArgumentException"/> when the migration reaches the runner, the same rule
    /// <see cref="SqlMigration"/>'s constructor already applies, and rejection happens before
    /// <see cref="MigrationOptions.AllowOutOfOrder"/> is consulted, so that flag does not excuse
    /// one. Zero is why the floor exists: it is the sentinel
    /// <see cref="IDocumentStore.GetCurrentMigrationVersionAsync"/> returns for "nothing applied"
    /// and the lowest target <see cref="IDocumentStore.RollbackToVersionAsync"/> accepts, so a
    /// migration sitting there was indistinguishable from an empty history and could not be
    /// rolled back. A negative version is refused by the same guard rather than left to compare
    /// against that sentinel.
    /// </remarks>
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
    /// what was applied, so editing it must not fail a startup migration. <see cref="SqlMigration"/>
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
    /// that callback returns.
    /// </para>
    /// <para>
    /// <strong>That rule covers this method too, not only an <c>ExecuteRawAsync</c> callback.</strong>
    /// The run's lease goes back through the pool's external-access path exactly as a raw callback's
    /// does, and nothing resets connection-local state on the way — measured at
    /// <see cref="DocumentStoreOptions.MaxPoolSize"/> = 1 on a file database, a migration that set
    /// <c>PRAGMA cache_size = -7777</c> and created a <c>TEMP</c> table left both in place for the
    /// next store operation, and an <c>ATTACH</c>ed database stayed listed in
    /// <c>pragma_database_list</c>. <see cref="DownAsync"/> behaves the same way. So restore what
    /// you change before returning.
    /// </para>
    /// <para>
    /// Failing does not restore it for you, and what the rollback covers <em>differs by kind</em>,
    /// so it is worth knowing which is which: the runner's transaction takes a <c>TEMP</c> table
    /// back with the rest of the migration's DDL — measured, it was gone after the rollback — while
    /// a <c>PRAGMA</c> and an <c>ATTACH</c>ed database both survived it. Anything else living on the
    /// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> rather than in the database file behaves
    /// by the same mechanism.
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
    /// The <em>restrictions</em> in <see cref="UpAsync"/>'s remarks apply here unchanged, and for
    /// the same reasons: do not open, commit or roll back a transaction on the connection; build
    /// commands with <c>connection.CreateCommand()</c>; do not call back into the store, since this
    /// is the run's only pooled connection; keep statements SQLite prohibits inside a transaction
    /// out of the runner; and restore any connection-local state this method changes — a
    /// <c>PRAGMA</c>, a <c>TEMP</c> object, an <c>ATTACH</c>ed database — before returning, since
    /// the lease goes back to the pool carrying it. Measured on this path too: a nested transaction
    /// and a <c>VACUUM</c> both throw, <c>PRAGMA foreign_keys</c> is silently ignored, and a
    /// <c>TEMP</c> table plus a changed <c>cache_size</c> were both still on the connection after
    /// the rollback run finished.
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
