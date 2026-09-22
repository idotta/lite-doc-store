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
/// One probe, because it is the only one the path that runs it can act on. SQLite's own view is
/// <c>sqlite3_get_autocommit</c>: zero means a transaction is pending on the handle, which is
/// what a raw <c>BEGIN</c> or <c>SAVEPOINT</c> leaves behind — the next renter's statements then
/// silently enlist in it.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite keeps a second, managed view that this deliberately does <em>not</em>
/// probe. <see cref="SqliteConnection.CreateCommand"/> copies the connection's attached
/// <see cref="SqliteTransaction"/> onto every command it makes, so a raw <c>COMMIT</c> leaves one
/// with nothing to roll back and a raw <c>ROLLBACK</c> leaves one already completed; the
/// autocommit flag is clean in both cases. Measured against Microsoft.Data.Sqlite 10.0.11, that
/// probe costs ~223 ns and 192 bytes — and it would be answering a question already decided,
/// because an attached transaction object can only come from a caller who has had the raw
/// connection, and every path that hands one out now discards the connection rather than
/// returning it. See <see cref="SqliteConnectionPool.ReturnAfterExternalAccess"/>.
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
    /// Reports whether a connection's transaction state is dirty, naming why.
    /// </summary>
    /// <param name="connection">The connection being returned to the pool.</param>
    /// <param name="reason">The dirty half, phrased for the pool's discard log.</param>
    internal static bool IsSessionDirty(SqliteConnection connection, out string reason)
    {
        if (connection.State == ConnectionState.Open && HasPendingTransaction(connection))
        {
            reason = "it was returned with a transaction still pending";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
