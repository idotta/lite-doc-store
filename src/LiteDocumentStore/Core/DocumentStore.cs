using System.Linq.Expressions;
using System.Text.Json;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore;

/// <summary>
/// A high-performance document store for storing JSON objects in SQLite.
/// Uses raw ADO.NET (Microsoft.Data.Sqlite) with explicit parameter binding and JSONB
/// storage (SQLite 3.45+).
/// </summary>
/// <remarks>
/// The store owns a <see cref="SqliteConnectionPool"/> and rents a connection per operation,
/// which makes it thread-safe and lets concurrent readers scale in WAL mode. Multi-statement
/// atomicity comes from <see cref="BeginTransactionAsync"/>, which holds one connection for
/// the transaction's lifetime.
/// </remarks>
internal sealed class DocumentStore : IDocumentStore
{
    // Bounded so a leaked lease cannot hang Dispose. On expiry the checkpoint is skipped,
    // which is safe: SQLite checkpoints the WAL when the last connection closes.
    private static readonly TimeSpan WalCheckpointRentTimeout = TimeSpan.FromSeconds(5);

    private readonly SqliteConnectionPool _pool;
    private readonly ITableNamingConvention _tableNamingConvention;
    private readonly ILogger<DocumentStore> _logger;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly bool _walEnabled;
    private int _disposed;

    /// <summary>
    /// Initializes a new document store over the supplied options.
    /// </summary>
    /// <param name="options">
    /// Store configuration. Its connection string, PRAGMA settings and
    /// <see cref="DocumentStoreOptions.MaxPoolSize"/> govern the connection pool.
    /// </param>
    /// <param name="connectionFactory">Factory used to open and configure pooled connections</param>
    /// <param name="tableNamingConvention">
    /// Table naming convention. Falls back to <see cref="DocumentStoreOptions.TableNamingConvention"/>,
    /// then to <see cref="DefaultTableNamingConvention"/>.
    /// </param>
    /// <param name="logger">Logger for diagnostics (optional)</param>
    public DocumentStore(
        DocumentStoreOptions options,
        IConnectionFactory connectionFactory,
        ITableNamingConvention? tableNamingConvention = null,
        ILogger<DocumentStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _tableNamingConvention = tableNamingConvention
            ?? options.TableNamingConvention
            ?? new DefaultTableNamingConvention();
        _logger = logger ?? NullLogger<DocumentStore>.Instance;
        _serializerOptions = options.SerializerOptions ?? JsonHelper.CreateDefaultReflectionOptions();
        // Only a WAL database has a log to checkpoint on disposal; skipping the probe saves a
        // round trip for every in-memory or rollback-journal store.
        _walEnabled = options.EnableWalMode;
        _pool = new SqliteConnectionPool(options, connectionFactory, _logger);
    }

    /// <summary>
    /// Gets the maximum number of connections this store will open.
    /// </summary>
    internal int MaxPoolSize => _pool.MaxPoolSize;

    /// <summary>
    /// Gets the number of connections this store has actually opened.
    /// </summary>
    internal int OpenConnectionCount => _pool.ConnectionCount;

    /// <summary>
    /// Opens the store's first connection, validating the connection string eagerly and
    /// keeping an in-memory database alive for the store's lifetime.
    /// </summary>
    internal void Initialize() => _pool.Initialize();

    /// <inheritdoc cref="Initialize" />
    internal Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _pool.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task CreateTableAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateTableAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<int> UpsertAsync<T>(string id, T data, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.UpsertAsync(id, data, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<int> UpsertManyAsync<T>(
        IEnumerable<(string id, T data)> items,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.UpsertManyAsync(items, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.UpsertWithVersionAsync(id, data, expectedVersion, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<VersionedDocument<T>?> GetWithVersionAsync<T>(
        string id,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.GetWithVersionAsync<T>(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.GetAsync<T>(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.GetAllAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync<T>(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DeleteAsync<T>(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DeleteManyAsync<T>(ids, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.ExistsAsync<T>(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<long> CountAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CountAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T, TValue>(
        string jsonPath,
        TValue value,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.QueryAsync<T, TValue>(jsonPath, value, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.QueryAsync(query, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<long> CountAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CountAsync(query, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateIndexAsync(jsonPath, indexName, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateCompositeIndexAsync(jsonPaths, indexName, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default) =>
        RunAsync(
            ops => ops.AddVirtualColumnAsync(jsonPath, columnName, createIndex, columnType, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task CreateBlobTableAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateBlobTableAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.PutBlobAsync(id, data, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> GetBlobAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.GetBlobAsync(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DeleteBlobAsync(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.BlobExistsAsync(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<TResult> ExecuteRawAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        return await operation(lease.Connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExecuteRawAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        await operation(lease.Connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string GetTableName<T>()
    {
        ThrowIfDisposed();

        return _tableNamingConvention.GetTableName<T>();
    }

    /// <inheritdoc />
    public byte[] SerializeDocument<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfDisposed();

        return JsonHelper.SerializeToUtf8Bytes(value, _serializerOptions);
    }

    /// <inheritdoc />
    public T? DeserializeDocument<T>(string? json)
    {
        ThrowIfDisposed();

        return JsonHelper.Deserialize<T>(json, _serializerOptions);
    }

    /// <inheritdoc />
    public async Task<IDocumentTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deferred, so BEGIN itself takes no lock: a read-only transaction never blocks a
            // writer, and write conflicts surface at the first write (where busy_timeout can
            // retry them) instead of at BEGIN.
            var transaction = lease.Connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable, deferred: true);

            return new DocumentStoreTransaction(
                lease, transaction, _tableNamingConvention, _serializerOptions, _logger);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await action(transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Report false rather than throwing, so a health endpoint can call this on a
            // disposed store.
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.LogWarning("Health check failed: DocumentStore is disposed");
                return false;
            }

            await using var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
            var connection = lease.Connection;

            // Re-check rather than trust the open-time guard: the health endpoint's job is to
            // report on the connection it is holding now, and this also covers a store built on
            // a consumer-supplied IConnectionFactory that never went through the pool.
            var version = await SqliteVersionGuard
                .EnsureSupportedAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            // Test basic query execution
            await connection.ExecuteScalarAsync<long>("SELECT 1", cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Health check passed: SQLite version {Version}", version);
            return true;
        }
        catch (UnsupportedSqliteVersionException ex)
        {
            // A too-old library is a configuration problem, not a fault: report it as unhealthy
            // at warning level rather than as an unexpected exception.
            _logger.LogWarning(ex, "Health check failed: unsupported SQLite version");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Rents a connection and runs one document operation on it.
    /// </summary>
    /// <remarks>
    /// The token is taken here as well as captured by the caller's lambda because it must also
    /// cancel the wait for a free connection — on a saturated pool that wait is the part of an
    /// operation a caller is most likely to want to abandon.
    /// </remarks>
    private async Task<TResult> RunAsync<TResult>(
        Func<DocumentOperations, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        return await operation(Operations(lease.Connection)).ConfigureAwait(false);
    }

    /// <inheritdoc cref="RunAsync{TResult}" />
    private async Task RunAsync(Func<DocumentOperations, Task> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        await operation(Operations(lease.Connection)).ConfigureAwait(false);
    }

    private DocumentOperations Operations(SqliteConnection connection) =>
        new(connection, _tableNamingConvention, _serializerOptions, _logger);

    /// <summary>
    /// Disposes the store: checkpoints the WAL and closes every pooled connection.
    /// In-flight operations holding a connection close it when they return it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await PerformWalCheckpointAsync().ConfigureAwait(false);
        await _pool.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="DisposeAsync" />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PerformWalCheckpoint();
        _pool.Dispose();
    }

    /// <summary>
    /// Performs a WAL checkpoint to flush the Write-Ahead Log into the database file for
    /// durability. Runs only when the database is in WAL mode.
    /// </summary>
    /// <remarks>
    /// Gated on the option, not the file's actual journal mode, to save a rent and a
    /// <c>PRAGMA journal_mode</c> per dispose. So an existing WAL database opened with
    /// <c>EnableWalMode = false</c> skips this — costing only the <c>TRUNCATE</c>, since
    /// SQLite checkpoints on last-connection close anyway.
    /// </remarks>
    private async Task PerformWalCheckpointAsync()
    {
        if (!_walEnabled)
        {
            return;
        }

        try
        {
            await using var lease = await _pool.RentAsync(WalCheckpointRentTimeout).ConfigureAwait(false);

            var journalMode = await lease.Connection.QueryFirstStringAsync(
                "PRAGMA journal_mode", CancellationToken.None).ConfigureAwait(false);

            if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Executing WAL checkpoint before disposal");
                // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                await lease.Connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)", CancellationToken.None)
                    .ConfigureAwait(false);
                _logger.LogInformation("WAL checkpoint completed successfully");
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarning(ex, "Failed to perform WAL checkpoint during disposal");
        }
    }

    /// <inheritdoc cref="PerformWalCheckpointAsync" />
    private void PerformWalCheckpoint()
    {
        if (!_walEnabled)
        {
            return;
        }

        try
        {
            using var lease = _pool.Rent(WalCheckpointRentTimeout);

            var journalMode = lease.Connection.QueryFirstString("PRAGMA journal_mode");

            if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Executing WAL checkpoint before disposal");
                // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                lease.Connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                _logger.LogInformation("WAL checkpoint completed successfully");
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarning(ex, "Failed to perform WAL checkpoint during disposal");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
