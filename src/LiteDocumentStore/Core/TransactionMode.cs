namespace LiteDocumentStore;

/// <summary>
/// How a transaction acquires the database write lock.
/// </summary>
public enum TransactionMode
{
    /// <summary>
    /// <c>BEGIN DEFERRED</c> — no lock is taken at <c>BEGIN</c>. A read-only transaction never
    /// blocks a writer, and a write-first transaction waits for the lock at its first write,
    /// where <see cref="DocumentStoreOptions.BusyTimeoutMs"/> retries it.
    /// </summary>
    /// <remarks>
    /// A transaction that <b>reads and then writes</b> is the hazard: the first read pins a
    /// snapshot, and if another connection commits before the first write, the upgrade fails
    /// with <c>SQLITE_BUSY_SNAPSHOT</c> (extended code 517). Waiting can never resolve that —
    /// the snapshot is already stale — so the only recovery is to roll back and redo the whole
    /// transaction. Measured against SQLite through Microsoft.Data.Sqlite, the provider retries
    /// it anyway until its command timeout elapses, so the caller stalls for that long before the
    /// failure surfaces: the store caps that timeout at
    /// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> (floored at one second), but a connection
    /// string that states its own <c>Default Timeout</c> keeps it (30 s if that is what it asks
    /// for). Use
    /// <see cref="Immediate"/> for read-then-write units of work.
    /// </remarks>
    Deferred = 0,

    /// <summary>
    /// <c>BEGIN IMMEDIATE</c> — the write lock is taken at <c>BEGIN</c>, before any snapshot
    /// exists, so a read-then-write transaction cannot fail with <c>SQLITE_BUSY_SNAPSHOT</c>.
    /// Contention becomes a plain <c>SQLITE_BUSY</c> wait at <c>BEGIN</c>, which
    /// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> retries.
    /// </summary>
    /// <remarks>
    /// The cost is that the write lock is held for the whole transaction, including its reads,
    /// so concurrent writers serialize on it. Past the wait the caller still sees
    /// <c>SQLITE_BUSY</c> and must retry. That wait is
    /// max(<see cref="DocumentStoreOptions.BusyTimeoutMs"/>, the connection's command timeout):
    /// <c>PRAGMA busy_timeout</c> bounds only SQLite's own handler within one attempt, and
    /// Microsoft.Data.Sqlite retries the attempt until its command timeout elapses. The store
    /// sets that timeout from <see cref="DocumentStoreOptions.BusyTimeoutMs"/> so the two agree,
    /// except that the provider's loop cannot express less than a second — a
    /// <c>BusyTimeoutMs</c> below 1000, including 0, still waits about one second. A connection
    /// string that states <c>Default Timeout</c> / <c>Command Timeout</c> wins instead, and if it
    /// is longer, it is what the caller actually waits.
    /// </remarks>
    Immediate = 1
}
