using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Defines the contract for SQLite connection lifecycle management.
/// This factory is stateless - options are passed to each method, enabling
/// a single factory instance to create connections for multiple databases.
/// </summary>
/// <remarks>
/// <para>
/// An implementation that does not delegate to <see cref="DefaultConnectionFactory"/> owes every
/// option it claims to honour, and two in particular, because neither has a readback guard and
/// both fail silently when omitted.
/// It must <em>state</em> <c>PRAGMA foreign_keys = ON|OFF</c> rather than only turning it on:
/// Microsoft.Data.Sqlite opens connections with foreign keys already enabled, so a factory that
/// skips the statement when <see cref="DocumentStoreOptions.EnableForeignKeys"/> is <c>false</c>
/// leaves them on — measured through such a factory, <c>PRAGMA foreign_keys</c> read back 1 where
/// the default factory gives 0. And it must set <see cref="SqliteConnection.DefaultTimeout"/> from
/// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> (seconds, rounded up, floored at 1, skipped
/// when the connection string states <c>Default Timeout</c>/<c>Command Timeout</c>), because the
/// provider retries a contended statement until its own timeout elapses — measured, a factory that
/// omits it left <c>DefaultTimeout</c> at the provider's 30 with
/// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> = 250, where the default factory gives 1.
/// </para>
/// <para>
/// Two related settings behave differently, and only one of them is genuinely not a factory's
/// concern. A non-zero <see cref="DocumentStoreOptions.PageSize"/> must still be applied — before
/// <c>journal_mode</c>, since SQLite refuses to change the page size of a database already in WAL
/// mode — but getting it wrong cannot be <em>silent</em>: the pool reads the value back on every
/// physical connection and throws <see cref="Exceptions.IncompatiblePageSizeException"/> on a
/// mismatch, so a factory that skips the statement fails to open the store rather than running at
/// the wrong page size. The in-memory case needs nothing at all: an in-memory database combined
/// with <see cref="DocumentStoreOptions.EnableWalMode"/> is refused during options validation,
/// before any connection is opened.
/// </para>
/// </remarks>
public interface IConnectionFactory
{
    /// <summary>
    /// Creates and opens a new SQLite connection synchronously.
    /// </summary>
    /// <param name="options">Configuration options for the connection</param>
    /// <returns>An open SQLite connection</returns>
    SqliteConnection CreateConnection(DocumentStoreOptions options);

    /// <summary>
    /// Creates and opens a new SQLite connection.
    /// </summary>
    /// <param name="options">Configuration options for the connection</param>
    /// <param name="cancellationToken">
    /// Cancels a queued open. Cannot interrupt one already under way — Microsoft.Data.Sqlite
    /// does SQLite I/O synchronously.
    /// </param>
    /// <returns>An open SQLite connection</returns>
    Task<SqliteConnection> CreateConnectionAsync(
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures a SQLite connection with optimal performance settings
    /// (e.g., WAL mode, synchronous level, page size, cache size).
    /// </summary>
    /// <param name="connection">The connection to configure</param>
    /// <param name="options">Configuration options to apply</param>
    void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options);

    /// <summary>
    /// Configures a SQLite connection with optimal performance settings
    /// (e.g., WAL mode, synchronous level, page size, cache size).
    /// </summary>
    /// <param name="connection">The connection to configure</param>
    /// <param name="options">Configuration options to apply</param>
    /// <param name="cancellationToken">A token to cancel the configuration round trips</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task ConfigureConnectionAsync(
        SqliteConnection connection,
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default);
}
