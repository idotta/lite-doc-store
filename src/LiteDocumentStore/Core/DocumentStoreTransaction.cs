using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// A unit of work bound to one pooled connection and one <see cref="SqliteTransaction"/>.
/// </summary>
/// <remarks>
/// The connection is held for the transaction's lifetime and returned to the pool on
/// completion. The transaction object is always disposed before the connection is returned —
/// handing a connection with an open transaction back to the pool would poison the next
/// renter.
/// </remarks>
internal sealed class DocumentStoreTransaction : IDocumentTransaction
{
    private readonly PooledConnection _lease;
    private readonly DocumentOperations _operations;
    private readonly ILogger _logger;
    private SqliteTransaction? _transaction;
    private int _disposed;
    private int _released;
    private bool _connectionCompromised;

    internal DocumentStoreTransaction(
        PooledConnection lease,
        SqliteTransaction transaction,
        ITableNamingConvention tableNamingConvention,
        JsonSerializerOptions serializerOptions,
        ILogger logger)
    {
        _lease = lease;
        _transaction = transaction;
        _logger = logger;
        _operations = new DocumentOperations(lease.Connection, tableNamingConvention, serializerOptions, logger, inAmbientTransaction: true);
    }

    /// <inheritdoc />
    public bool IsCommitted { get; private set; }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var transaction = ActiveTransaction();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        IsCommitted = true;
        _logger.LogDebug("Transaction committed");

        Release();
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var transaction = ActiveTransaction();

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Transaction rolled back");

        Release();
    }

    // Every operation below goes through ActiveTransaction() first: once the transaction has
    // been committed, rolled back or disposed, its connection is back in the pool and may already
    // be serving another renter, so the command must fail rather than run on a foreign connection.

    /// <inheritdoc />
    public Task CreateTableAsync<T>(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateTableAsync<T>(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> UpsertAsync<T>(string id, T data, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.UpsertAsync(id, data, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> UpsertManyAsync<T>(
        IEnumerable<(string id, T data)> items,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.UpsertManyAsync(items, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.UpsertWithVersionAsync(id, data, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteWithVersionAsync<T>(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteWithVersionAsync<T>(id, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PatchAsync<T>(
        string id,
        DocumentPatch<T> patch,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PatchAsync(id, patch, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PatchWithVersionAsync<T>(
        string id,
        DocumentPatch<T> patch,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PatchWithVersionAsync(id, patch, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<VersionedDocument<T>?> GetWithVersionAsync<T>(
        string id,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetWithVersionAsync<T>(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetAsync<T>(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetAllAsync<T>(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, T>> GetManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetManyAsync<T>(ids, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteAsync<T>(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteManyAsync<T>(ids, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteAllAsync<T>(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteAllAsync<T>(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.ExistsAsync<T>(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountAsync<T>(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CountAsync<T>(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T, TValue>(
        string jsonPath,
        TValue value,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.QueryAsync<T, TValue>(jsonPath, value, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CountAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.ExistsAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateIndexAsync(jsonPath, indexName, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.CreateIndexAsync(jsonPath, indexName, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        string jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateIndexAsync<T>(jsonPath, indexName, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        string jsonPath,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.CreateIndexAsync<T>(jsonPath, indexName, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateCompositeIndexAsync(jsonPaths, indexName, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.CreateCompositeIndexAsync(jsonPaths, indexName, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        string[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateCompositeIndexAsync<T>(jsonPaths, indexName, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        string[] jsonPaths,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.CreateCompositeIndexAsync<T>(jsonPaths, indexName, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.AddVirtualColumnAsync(jsonPath, columnName, createIndex, columnType, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        string jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.AddVirtualColumnAsync<T>(jsonPath, columnName, createIndex, columnType, cancellationToken);
    }

    /// <inheritdoc />
    public Task DropTableAsync<T>(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DropTableAsync<T>(cancellationToken);
    }

    /// <inheritdoc />
    public Task DropIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DropIndexAsync(indexName, cancellationToken);
    }

    /// <inheritdoc />
    public Task DropIndexAsync<T>(
        Expression<Func<T, object>> expression,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DropIndexAsync(expression, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateBlobTableAsync(CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.CreateBlobTableAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PutBlobAsync(id, data, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        BlobWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.PutBlobAsync(id, data, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        Stream source,
        long length,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PutBlobAsync(id, source, length, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        Stream source,
        long length,
        BlobWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.PutBlobAsync(id, source, length, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        ReadOnlyMemory<byte> data,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PutBlobWithVersionAsync(id, data, expectedVersion, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        ReadOnlyMemory<byte> data,
        long expectedVersion,
        BlobWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.PutBlobWithVersionAsync(id, data, expectedVersion, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        Stream source,
        long length,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.PutBlobAsync(id, source, length, null, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        Stream source,
        long length,
        long expectedVersion,
        BlobWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ActiveTransaction();
        return _operations.PutBlobAsync(id, source, length, options, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long?> BlobLengthAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.BlobLengthAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetBlobAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteBlobAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteBlobWithVersionAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.DeleteBlobWithVersionAsync(id, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<BlobInfo?> GetBlobInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.GetBlobInfoAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BlobInfo>> ListBlobsAsync(
        string? idPrefix = null,
        int skip = 0,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.ListBlobsAsync(idPrefix, skip, take, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveTransaction();
        return _operations.BlobExistsAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> ExecuteRawAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ActiveTransaction();
        return operation(_lease.Connection, cancellationToken);
    }

    /// <inheritdoc />
    public Task ExecuteRawAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ActiveTransaction();
        return operation(_lease.Connection, cancellationToken);
    }

    /// <inheritdoc />
    public string GetTableName<T>() => _operations.GetTableName<T>();

    /// <inheritdoc />
    public byte[] SerializeDocument<T>(T value) => _operations.SerializeDocument(value);

    /// <inheritdoc />
    public T? DeserializeDocument<T>(string? json) => _operations.DeserializeDocument<T>(json);

    /// <summary>
    /// Rolls back an uncommitted transaction, then releases the connection.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RollbackIfPending();
        Release();
    }

    /// <inheritdoc cref="Dispose" />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_transaction is { } pending)
        {
            try
            {
                await pending.RollbackAsync().ConfigureAwait(false);
                _logger.LogDebug("Uncommitted transaction rolled back on disposal");
            }
            catch (Exception ex)
            {
                // The connection may still carry an open transaction, which would poison the
                // next renter, so it must not go back into the pool.
                _connectionCompromised = true;
                _logger.LogWarning(ex, "Failed to roll back an uncommitted transaction during disposal");
            }
        }

        Release();
    }

    private void RollbackIfPending()
    {
        if (_transaction is not { } pending)
        {
            return;
        }

        try
        {
            pending.Rollback();
            _logger.LogDebug("Uncommitted transaction rolled back on disposal");
        }
        catch (Exception ex)
        {
            // See DisposeAsync: a connection that may still hold an open transaction cannot be
            // recycled.
            _connectionCompromised = true;
            _logger.LogWarning(ex, "Failed to roll back an uncommitted transaction during disposal");
        }
    }

    private SqliteTransaction ActiveTransaction()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        return _transaction ?? throw new InvalidOperationException(
            "The transaction has already been committed or rolled back.");
    }

    /// <summary>
    /// Disposes the SQLite transaction and hands its connection back to the pool, discarding
    /// the connection instead when its session state can no longer be trusted.
    /// </summary>
    /// <remarks>
    /// Idempotent, so commit-then-dispose does not return the same connection twice. Once it has
    /// run, <see cref="Dispose"/> is a no-op and a second commit or rollback fails with
    /// <see cref="InvalidOperationException"/>.
    /// </remarks>
    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        var transaction = Interlocked.Exchange(ref _transaction, null);

        try
        {
            transaction?.Dispose();
        }
        catch (Exception ex)
        {
            _connectionCompromised = true;
            _logger.LogWarning(ex, "Failed to dispose the underlying SQLite transaction");
        }

        if (_connectionCompromised)
        {
            _lease.Discard();
        }
        else
        {
            _lease.Dispose();
        }
    }
}
