using System.Data;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Default stateless implementation of <see cref="IConnectionFactory"/>.
/// A single instance can create connections for multiple databases by passing
/// different options to each method.
/// </summary>
internal sealed class DefaultConnectionFactory : IConnectionFactory
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

    /// <inheritdoc/>
    public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        // Configure WAL mode
        if (options.EnableWalMode)
        {
            connection.Execute("PRAGMA journal_mode = WAL;");
        }

        // Configure synchronous mode
        var syncMode = GetSynchronousModeString(options.SynchronousMode);
        connection.Execute($"PRAGMA synchronous = {syncMode};");

        // Configure page size (must be set before any tables are created)
        connection.Execute($"PRAGMA page_size = {options.PageSize};");

        // Configure cache size
        connection.Execute($"PRAGMA cache_size = {options.CacheSize};");

        // Configure busy timeout
        connection.Execute($"PRAGMA busy_timeout = {options.BusyTimeoutMs};");

        // Configure foreign keys
        if (options.EnableForeignKeys)
        {
            connection.Execute("PRAGMA foreign_keys = ON;");
        }

        // Execute additional pragmas
        foreach (var pragma in options.AdditionalPragmas)
        {
            connection.Execute(pragma);
        }
    }

    /// <inheritdoc/>
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

        // Configure WAL mode
        if (options.EnableWalMode)
        {
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        }

        // Configure synchronous mode
        var syncMode = GetSynchronousModeString(options.SynchronousMode);
        await connection.ExecuteAsync($"PRAGMA synchronous = {syncMode};", cancellationToken)
            .ConfigureAwait(false);

        // Configure page size (must be set before any tables are created)
        await connection.ExecuteAsync($"PRAGMA page_size = {options.PageSize};", cancellationToken)
            .ConfigureAwait(false);

        // Configure cache size
        await connection.ExecuteAsync($"PRAGMA cache_size = {options.CacheSize};", cancellationToken)
            .ConfigureAwait(false);

        // Configure busy timeout
        await connection.ExecuteAsync($"PRAGMA busy_timeout = {options.BusyTimeoutMs};", cancellationToken)
            .ConfigureAwait(false);

        // Configure foreign keys
        if (options.EnableForeignKeys)
        {
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        }

        // Execute additional pragmas
        foreach (var pragma in options.AdditionalPragmas)
        {
            await connection.ExecuteAsync(pragma, cancellationToken).ConfigureAwait(false);
        }
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
