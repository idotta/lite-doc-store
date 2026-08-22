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
        _operations = new DocumentOperations(lease.Connection, tableNamingConvention, serializerOptions, logger);
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

    /// <inheritdoc />
    public Task CreateTableAsync<T>() => _operations.CreateTableAsync<T>();

    /// <inheritdoc />
    public Task<int> UpsertAsync<T>(string id, T data) => _operations.UpsertAsync(id, data);

    /// <inheritdoc />
    public Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items) =>
        _operations.UpsertManyAsync(items);

    /// <inheritdoc />
    public Task<long> UpsertWithVersionAsync<T>(string id, T data, long expectedVersion) =>
        _operations.UpsertWithVersionAsync(id, data, expectedVersion);

    /// <inheritdoc />
    public Task<VersionedDocument<T>?> GetWithVersionAsync<T>(string id) =>
        _operations.GetWithVersionAsync<T>(id);

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string id) => _operations.GetAsync<T>(id);

    /// <inheritdoc />
    public Task<IEnumerable<T>> GetAllAsync<T>() => _operations.GetAllAsync<T>();

    /// <inheritdoc />
    public Task<bool> DeleteAsync<T>(string id) => _operations.DeleteAsync<T>(id);

    /// <inheritdoc />
    public Task<int> DeleteManyAsync<T>(IEnumerable<string> ids) => _operations.DeleteManyAsync<T>(ids);

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(string id) => _operations.ExistsAsync<T>(id);

    /// <inheritdoc />
    public Task<long> CountAsync<T>() => _operations.CountAsync<T>();

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value) =>
        _operations.QueryAsync<T, TValue>(jsonPath, value);

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(Expression<Func<T, object>> jsonPath, string? indexName = null) =>
        _operations.CreateIndexAsync(jsonPath, indexName);

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(Expression<Func<T, object>>[] jsonPaths, string? indexName = null) =>
        _operations.CreateCompositeIndexAsync(jsonPaths, indexName);

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT") =>
        _operations.AddVirtualColumnAsync(jsonPath, columnName, createIndex, columnType);

    /// <inheritdoc />
    public Task CreateBlobTableAsync() => _operations.CreateBlobTableAsync();

    /// <inheritdoc />
    public Task PutBlobAsync(string id, ReadOnlyMemory<byte> data) => _operations.PutBlobAsync(id, data);

    /// <inheritdoc />
    public Task<byte[]?> GetBlobAsync(string id) => _operations.GetBlobAsync(id);

    /// <inheritdoc />
    public Task<bool> DeleteBlobAsync(string id) => _operations.DeleteBlobAsync(id);

    /// <inheritdoc />
    public Task<bool> BlobExistsAsync(string id) => _operations.BlobExistsAsync(id);

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
