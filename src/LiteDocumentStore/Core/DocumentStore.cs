using System.Linq.Expressions;
using System.Text.Json;
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
    public Task CreateTableAsync<T>() => RunAsync(ops => ops.CreateTableAsync<T>());

    /// <inheritdoc />
    public Task<int> UpsertAsync<T>(string id, T data) => RunAsync(ops => ops.UpsertAsync(id, data));

    /// <inheritdoc />
    public Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items) =>
        RunAsync(ops => ops.UpsertManyAsync(items));

    /// <inheritdoc />
    public Task<long> UpsertWithVersionAsync<T>(string id, T data, long expectedVersion) =>
        RunAsync(ops => ops.UpsertWithVersionAsync(id, data, expectedVersion));

    /// <inheritdoc />
    public Task<VersionedDocument<T>?> GetWithVersionAsync<T>(string id) =>
        RunAsync(ops => ops.GetWithVersionAsync<T>(id));

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string id) => RunAsync(ops => ops.GetAsync<T>(id));

    /// <inheritdoc />
    public Task<IEnumerable<T>> GetAllAsync<T>() => RunAsync(ops => ops.GetAllAsync<T>());

    /// <inheritdoc />
    public Task<bool> DeleteAsync<T>(string id) => RunAsync(ops => ops.DeleteAsync<T>(id));

    /// <inheritdoc />
    public Task<int> DeleteManyAsync<T>(IEnumerable<string> ids) =>
        RunAsync(ops => ops.DeleteManyAsync<T>(ids));

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(string id) => RunAsync(ops => ops.ExistsAsync<T>(id));

    /// <inheritdoc />
    public Task<long> CountAsync<T>() => RunAsync(ops => ops.CountAsync<T>());

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value) =>
        RunAsync(ops => ops.QueryAsync<T, TValue>(jsonPath, value));

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(Expression<Func<T, object>> jsonPath, string? indexName = null) =>
        RunAsync(ops => ops.CreateIndexAsync(jsonPath, indexName));

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(Expression<Func<T, object>>[] jsonPaths, string? indexName = null) =>
        RunAsync(ops => ops.CreateCompositeIndexAsync(jsonPaths, indexName));

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT") =>
        RunAsync(ops => ops.AddVirtualColumnAsync(jsonPath, columnName, createIndex, columnType));

    /// <inheritdoc />
    public Task CreateBlobTableAsync() => RunAsync(ops => ops.CreateBlobTableAsync());

    /// <inheritdoc />
    public Task PutBlobAsync(string id, ReadOnlyMemory<byte> data) =>
        RunAsync(ops => ops.PutBlobAsync(id, data));

    /// <inheritdoc />
    public Task<byte[]?> GetBlobAsync(string id) => RunAsync(ops => ops.GetBlobAsync(id));

    /// <inheritdoc />
    public Task<bool> DeleteBlobAsync(string id) => RunAsync(ops => ops.DeleteBlobAsync(id));

    /// <inheritdoc />
    public Task<bool> BlobExistsAsync(string id) => RunAsync(ops => ops.BlobExistsAsync(id));

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
    public async Task<bool> IsHealthyAsync()
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

            await using var lease = await _pool.RentAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // Verify SQLite version supports JSONB (3.45+)
            var versionString = await connection.QueryFirstStringAsync(
                "SELECT sqlite_version()").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(versionString))
            {
                _logger.LogWarning("Health check failed: Could not retrieve SQLite version");
                return false;
            }

            if (!Version.TryParse(versionString, out var version))
            {
                _logger.LogWarning("Health check failed: Invalid SQLite version format: {Version}", versionString);
                return false;
            }

            var minVersion = new Version(3, 45, 0);
            if (version < minVersion)
            {
                _logger.LogWarning(
                    "Health check failed: SQLite version {Version} does not support JSONB (requires {MinVersion}+)",
                    version, minVersion);
                return false;
            }

            // Test basic query execution
            await connection.ExecuteScalarAsync<long>("SELECT 1").ConfigureAwait(false);

            _logger.LogDebug("Health check passed: SQLite version {Version}", version);
            return true;
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
    private async Task<TResult> RunAsync<TResult>(Func<DocumentOperations, Task<TResult>> operation)
    {
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync().ConfigureAwait(false);
        return await operation(Operations(lease.Connection)).ConfigureAwait(false);
    }

    /// <inheritdoc cref="RunAsync{TResult}" />
    private async Task RunAsync(Func<DocumentOperations, Task> operation)
    {
        ThrowIfDisposed();

        await using var lease = await _pool.RentAsync().ConfigureAwait(false);
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
    private async Task PerformWalCheckpointAsync()
    {
        if (!_walEnabled)
        {
            return;
        }

        try
        {
            await using var lease = await _pool.RentAsync().ConfigureAwait(false);

            var journalMode = await lease.Connection.QueryFirstStringAsync(
                "PRAGMA journal_mode").ConfigureAwait(false);

            if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Executing WAL checkpoint before disposal");
                // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                await lease.Connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)")
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
            using var lease = _pool.Rent();

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
