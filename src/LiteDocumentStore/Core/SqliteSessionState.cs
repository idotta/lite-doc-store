using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace LiteDocumentStore;

/// <summary>
/// Answers whether a connection still carries transaction state, so that a pooled connection
/// whose session can no longer be trusted is closed instead of handed to the next renter.
/// </summary>
/// <remarks>
/// <para>
/// Two independent checks, because a poisoned connection has two independent halves. SQLite's
/// own view is <c>sqlite3_get_autocommit</c>: zero means a transaction is pending on the
/// handle, which is what a raw <c>BEGIN</c> or <c>SAVEPOINT</c> leaves behind — the next
/// renter's statements then silently enlist in it. Microsoft.Data.Sqlite keeps a second,
/// managed view: <see cref="SqliteConnection.CreateCommand"/> copies the connection's attached
/// <see cref="SqliteTransaction"/> onto every command it makes, so an attached transaction
/// object outlives the SQLite transaction and poisons the connection on its own — a raw
/// <c>COMMIT</c> leaves one that makes the next <c>BeginTransaction</c> throw
/// <c>SqliteConnection does not support nested transactions</c> and the eventual
/// <c>Close</c> throw <c>cannot rollback - no transaction is active</c>, and a raw
/// <c>ROLLBACK</c> leaves one that makes every later command throw
/// <c>This SqliteTransaction has completed</c>.
/// </para>
/// <para>
/// Neither check subsumes the other: measured against Microsoft.Data.Sqlite 10.0.11, a raw
/// <c>BEGIN</c> is invisible to the managed check and a raw <c>COMMIT</c> or <c>ROLLBACK</c>
/// is invisible to the autocommit one. They differ in cost by a factor of five, which is why
/// the pool applies them at different places — see <see cref="SqliteConnectionPool.Return"/>
/// and <see cref="SqliteConnectionPool.ReturnAfterExternalAccess"/>.
/// </para>
/// </remarks>
internal static class SqliteSessionState
{
    /// <summary>
    /// Reports whether SQLite has a transaction pending on the connection. Measured at ~42 ns
    /// with no allocation.
    /// </summary>
    /// <remarks>
    /// Valid only on an open connection: the underlying handle is released on close, and the
    /// call then throws <see cref="ArgumentNullException"/> rather than answering false.
    /// </remarks>
    internal static bool HasPendingTransaction(SqliteConnection connection) =>
        raw.sqlite3_get_autocommit(connection.Handle) == 0;

    /// <summary>
    /// Reports whether a Microsoft.Data.Sqlite <see cref="SqliteTransaction"/> is still attached
    /// to the connection. Measured at ~223 ns and 192 bytes, so it is applied only where a
    /// caller has had the raw connection.
    /// </summary>
    /// <remarks>
    /// This observes the provider's managed attachment, not SQLite's transaction state; the two
    /// disagree in both directions. Safe on a closed connection, which reports false.
    /// </remarks>
    internal static bool HasManagedTransaction(SqliteConnection connection)
    {
        using var probe = connection.CreateCommand();
        return probe.Transaction is not null;
    }

    /// <summary>
    /// Reports whether a connection's transaction state is dirty, naming the half that is.
    /// </summary>
    /// <param name="connection">The connection being returned to the pool.</param>
    /// <param name="includeManagedTransaction">
    /// Whether to pay for <see cref="HasManagedTransaction"/> as well. True only where a caller
    /// has run their own SQL against the connection.
    /// </param>
    /// <param name="reason">The dirty half, phrased for the pool's discard log.</param>
    internal static bool IsSessionDirty(
        SqliteConnection connection,
        bool includeManagedTransaction,
        out string reason)
    {
        if (connection.State == ConnectionState.Open && HasPendingTransaction(connection))
        {
            reason = "it was returned with a transaction still pending";
            return true;
        }

        if (includeManagedTransaction && HasManagedTransaction(connection))
        {
            reason = "it was returned with a SQLite transaction object still attached";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
