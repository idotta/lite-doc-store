using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Default stateless implementation of <see cref="IConnectionFactory"/>, and the delegation
/// target for a custom one.
/// </summary>
/// <remarks>
/// <para>
/// A single instance can create connections for multiple databases by passing different options
/// to each method. It is <c>sealed</c> on purpose: a custom factory decorates it by holding one
/// and forwarding to it, not by deriving from it.
/// </para>
/// <para>
/// Three of its behaviours are correctness, not tuning, and are the reason a custom factory
/// should delegate rather than reimplement. It applies <c>PRAGMA page_size</c> <em>before</em>
/// <c>journal_mode</c>, because SQLite refuses to change the page size of a database already in
/// WAL mode — the other order made <see cref="DocumentStoreOptions.PageSize"/> a no-op even on a
/// brand-new file. It always <em>states</em> <c>PRAGMA foreign_keys = ON|OFF</c>, because
/// Microsoft.Data.Sqlite opens connections with foreign keys already on, so omitting the statement
/// silently ignores <see cref="DocumentStoreOptions.EnableForeignKeys"/> = <c>false</c>. And it
/// derives <see cref="SqliteConnection.DefaultTimeout"/> from
/// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> unless the connection string already states
/// <c>Default Timeout</c>/<c>Command Timeout</c>, because <c>PRAGMA busy_timeout</c> bounds only
/// SQLite's busy handler within one attempt while the provider re-runs the whole attempt until
/// its own command timeout elapses — leaving the provider's 30 s default in place makes
/// <c>BusyTimeoutMs</c> a floor rather than the bound it documents.
/// </para>
/// <para>
/// See <see cref="IConnectionFactory"/> for which of these the store detects behind any factory —
/// detection, not exemption: a factory that does not delegate still has to apply them.
/// </para>
/// </remarks>
public sealed class DefaultConnectionFactory : IConnectionFactory
{
    /// <summary>
    /// Initializes a new instance of DefaultConnectionFactory.
    /// </summary>
    public DefaultConnectionFactory()
    {
    }

    /// <inheritdoc/>
    public SqliteConnection CreateConnection(DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connection = new SqliteConnection(options.ConnectionString);

        try
        {
            connection.Open();
            ConfigureConnection(connection, options);
            return connection;
        }
        catch
        {
            // Open can succeed and a PRAGMA still throw, and nothing else holds a reference to
            // the connection yet — without this the handle stays open until finalization.
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<SqliteConnection> CreateConnectionAsync(
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connection = new SqliteConnection(options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // See CreateConnection — cancellation between the open and the PRAGMAs strands the
            // handle the same way a failing PRAGMA does.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies the command timeout and the PRAGMA block for <paramref name="options"/> to an
    /// already-open connection.
    /// </summary>
    /// <remarks>
    /// Not declared on <see cref="IConnectionFactory"/>, deliberately: the store only ever calls
    /// <see cref="CreateConnection(DocumentStoreOptions)"/>, so an interface declaration here
    /// invited an implementer to configure in a member nothing calls. It stays public on this
    /// sealed class because that is what a delegating factory calls on its inner instance.
    /// </remarks>
    /// <param name="connection">The connection to configure. Opened first if it is not already.</param>
    /// <param name="options">The options whose settings are applied.</param>
    [SuppressMessage("Performance", "CA1822",
        Justification = "Must stay a public instance member: this is the delegation seam. A custom " +
                        "IConnectionFactory decorates this sealed class by holding one and calling " +
                        "_inner.ConfigureConnection(...), which a static member would not compile.")]
    public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        // Before the PRAGMAs, not after them: every statement below runs under the command
        // timeout, so applying it last left a contended `PRAGMA journal_mode = WAL` waiting the
        // provider's 30 s whatever BusyTimeoutMs said.
        ApplyCommandTimeout(connection, options);

        // Page size first: SQLite refuses to change it once the database is in WAL mode, so
        // applying journal_mode before this would make PageSize a no-op even on a new database
        // (measured: an 8192 request read back as 4096). A PageSize of 0 means "keep whatever
        // the database has", so no statement is sent at all.
        if (options.PageSize != 0)
        {
            connection.Execute($"PRAGMA page_size = {options.PageSize};");
        }

        // Configure WAL mode
        if (options.EnableWalMode)
        {
            connection.Execute("PRAGMA journal_mode = WAL;");
        }

        // Configure synchronous mode
        var syncMode = GetSynchronousModeString(options.SynchronousMode);
        connection.Execute($"PRAGMA synchronous = {syncMode};");

        // Configure cache size
        connection.Execute($"PRAGMA cache_size = {options.CacheSize};");

        // Configure busy timeout
        connection.Execute($"PRAGMA busy_timeout = {options.BusyTimeoutMs};");

        // Always stated, never skipped: Microsoft.Data.Sqlite opens connections with
        // foreign_keys already ON, so omitting the OFF left EnableForeignKeys = false doing
        // nothing at all.
        connection.Execute($"PRAGMA foreign_keys = {(options.EnableForeignKeys ? "ON" : "OFF")};");

        // Execute additional pragmas
        foreach (var pragma in options.AdditionalPragmas)
        {
            connection.Execute(pragma);
        }
    }

    /// <summary>
    /// Applies the command timeout and the PRAGMA block for <paramref name="options"/> to an
    /// already-open connection.
    /// </summary>
    /// <remarks>
    /// See <see cref="ConfigureConnection(SqliteConnection, DocumentStoreOptions)"/> for why this
    /// is public on the sealed class but absent from <see cref="IConnectionFactory"/>.
    /// </remarks>
    /// <param name="connection">The connection to configure. Opened first if it is not already.</param>
    /// <param name="options">The options whose settings are applied.</param>
    /// <param name="cancellationToken">Cancels the configuration round trips.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [SuppressMessage("Performance", "CA1822",
        Justification = "Must stay a public instance member: this is the delegation seam. A custom " +
                        "IConnectionFactory decorates this sealed class by holding one and calling " +
                        "_inner.ConfigureConnection(...), which a static member would not compile.")]
    public async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        // See ConfigureConnection: the command timeout has to precede the PRAGMAs, which run
        // under it.
        ApplyCommandTimeout(connection, options);

        // See ConfigureConnection: page size must precede journal_mode, and 0 means "keep the
        // database's own page size".
        if (options.PageSize != 0)
        {
            await connection.ExecuteAsync($"PRAGMA page_size = {options.PageSize};", cancellationToken)
                .ConfigureAwait(false);
        }

        // Configure WAL mode
        if (options.EnableWalMode)
        {
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        }

        // Configure synchronous mode
        var syncMode = GetSynchronousModeString(options.SynchronousMode);
        await connection.ExecuteAsync($"PRAGMA synchronous = {syncMode};", cancellationToken)
            .ConfigureAwait(false);

        // Configure cache size
        await connection.ExecuteAsync($"PRAGMA cache_size = {options.CacheSize};", cancellationToken)
            .ConfigureAwait(false);

        // Configure busy timeout
        await connection.ExecuteAsync($"PRAGMA busy_timeout = {options.BusyTimeoutMs};", cancellationToken)
            .ConfigureAwait(false);

        // See ConfigureConnection: the OFF has to be stated, not skipped.
        await connection
            .ExecuteAsync($"PRAGMA foreign_keys = {(options.EnableForeignKeys ? "ON" : "OFF")};", cancellationToken)
            .ConfigureAwait(false);

        // Execute additional pragmas
        foreach (var pragma in options.AdditionalPragmas)
        {
            await connection.ExecuteAsync(pragma, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes <see cref="DocumentStoreOptions.BusyTimeoutMs"/> the actual bound on a contended
    /// statement by capping Microsoft.Data.Sqlite's own retry loop to the same value.
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA busy_timeout</c> bounds SQLite's busy handler *inside one attempt*; the provider
    /// then retries the whole attempt while its command timeout has not elapsed. The effective
    /// wait is therefore max(busy_timeout, command timeout), so leaving the provider's 30 s
    /// default in place made <c>BusyTimeoutMs</c> a floor rather than the bound its documentation
    /// promises — measured against real SQLite: a blocked <c>BEGIN IMMEDIATE</c> with
    /// <c>busy_timeout = 250</c> returned after ~2 s under <c>Default Timeout=2</c> and ~4 s under
    /// <c>Default Timeout=4</c>, while <c>busy_timeout = 3000</c> under <c>Default Timeout=1</c>
    /// took ~3 s. A command timeout stated in the connection string wins, and a custom
    /// <see cref="IConnectionFactory"/> has to do this itself.
    /// </remarks>
    private static void ApplyCommandTimeout(SqliteConnection connection, DocumentStoreOptions options)
    {
        // The connection's own string, not the options': ConfigureConnection is public and takes
        // a caller-supplied connection, which need not have been opened from options.
        // Microsoft.Data.Sqlite keeps the string verbatim, so a stated timeout is still visible.
        if (SqliteConnectionStringGuard.SpecifiesCommandTimeout(connection.ConnectionString))
        {
            return;
        }

        // Seconds is the provider's unit and 0 means "retry forever" there, not "fail now"
        // (measured: a blocked BEGIN IMMEDIATE with DefaultTimeout = 0 never came back, whatever
        // busy_timeout said). So every value floors at one second: the provider's retry loop cannot
        // express a shorter bound, and turning a caller's short timeout into an unbounded wait is
        // the one outcome worse than rounding it up.
        connection.DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMs / 1000.0));
    }

    private static string GetSynchronousModeString(SynchronousMode mode)
    {
        return mode switch
        {
            SynchronousMode.Off => "OFF",
            SynchronousMode.Normal => "NORMAL",
            SynchronousMode.Full => "FULL",
            _ => "NORMAL"
        };
    }
}
