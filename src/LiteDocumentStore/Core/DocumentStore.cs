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
/// atomicity comes from <see cref="BeginTransactionAsync(CancellationToken)"/>, which holds one
/// connection for the transaction's lifetime.
/// </remarks>
internal sealed class DocumentStore : IDocumentStore
{
    // Bounded so a leaked lease cannot hang Dispose. On expiry the checkpoint is skipped,
    // which is safe: SQLite checkpoints the WAL when the last connection closes.
    private static readonly TimeSpan WalCheckpointRentTimeout = TimeSpan.FromSeconds(5);

    private readonly SqliteConnectionPool _pool;
    private readonly TableNameCollisionGuard _tableNamingConvention;
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
    /// <remarks>
    /// <para>
    /// The constructor takes a <see cref="DocumentStoreOptions.Clone"/> snapshot of
    /// <paramref name="options"/> and validates <em>that</em>, so the store is governed by the
    /// configuration as it stood at construction: later mutation of the caller's object — or
    /// mutation from arbitrary caller code running between an earlier <see cref="DocumentStoreOptions.Validate"/>
    /// and this constructor, such as an <c>ILoggerFactory</c>'s <c>CreateLogger</c> — does not
    /// reach it. This is also the validation boundary that cannot be bypassed: it runs whether the
    /// store was built through <see cref="DocumentStoreFactory"/>, through the DI registration, or
    /// directly.
    /// </para>
    /// <para>
    /// The snapshot detaches <em>values</em>, not behaviour: a reference-typed option —
    /// <see cref="DocumentStoreOptions.SerializerOptions"/> and
    /// <see cref="DocumentStoreOptions.TableNamingConvention"/> — is copied by reference, so the
    /// <em>property</em> is detached from the caller's object but the instance behind it is still
    /// shared, as the connection factory and the logger always have been. See
    /// <see cref="DocumentStoreOptions.Clone"/> for why each one is shared.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An option is outside its valid range, or the connection string names a database the store
    /// cannot use as configured. The exception names the offending option.
    /// </exception>
    public DocumentStore(
        DocumentStoreOptions options,
        IConnectionFactory connectionFactory,
        ITableNamingConvention? tableNamingConvention = null,
        ILogger<DocumentStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        // Snapshot first, then validate the snapshot: the caller's object is mutable and arbitrary
        // caller code runs between DocumentStoreFactory's own Validate() and this constructor (an
        // ILoggerFactory's CreateLogger, or simply another thread setting a property), so what was
        // validated there is not necessarily what arrives here. Validating the detached copy makes
        // what is validated what is used. Every read below comes from the snapshot — reading
        // `options` again anywhere past this line reopens the same window one line further down.
        var snapshot = options.Clone();
        snapshot.Validate();

        // Per store, so a residual naming collision is a throw rather than silent cross-type
        // overwriting. Wraps whatever convention was configured, including a caller-supplied one.
        _tableNamingConvention = new TableNameCollisionGuard(tableNamingConvention
            ?? snapshot.TableNamingConvention
            ?? DefaultTableNamingConvention.Instance);
        _logger = logger ?? NullLogger<DocumentStore>.Instance;
        // Validate() ends in ThrowIfSerializerOptionsUnusable, so both serializer rejections have
        // already run against the snapshot; no standalone re-check is needed here.
        _serializerOptions = snapshot.SerializerOptions ?? JsonHelper.CreateDefaultReflectionOptions();
        // Only a WAL database has a log to checkpoint on disposal; skipping the probe saves a
        // round trip for every in-memory or rollback-journal store.
        _walEnabled = snapshot.EnableWalMode;
        _pool = new SqliteConnectionPool(snapshot, connectionFactory, _logger);
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
    public Task<int> UpsertAsync<T>(string id, T data, CancellationToken cancellationToken = default)
    {
        // Ahead of the rent, like every guard below: see the validation region in
        // DocumentOperations for why the same check runs at both boundaries.
        DocumentOperations.ValidateId(id);
        ArgumentNullException.ThrowIfNull(data);

        return RunAsync(ops => ops.UpsertAsync(id, data, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> UpsertManyAsync<T>(
        IEnumerable<(string id, T data)> items,
        CancellationToken cancellationToken = default)
    {
        // The null check only. The per-element validation runs after the operation materializes
        // the sequence, and repeating it here would enumerate it a second time — silently
        // consuming a one-shot enumerable.
        ArgumentNullException.ThrowIfNull(items);

        return RunAsync(ops => ops.UpsertManyAsync(items, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);
        ArgumentNullException.ThrowIfNull(data);
        DocumentOperations.ValidateExpectedVersion(expectedVersion);

        return RunAsync(
            ops => ops.UpsertWithVersionAsync(id, data, expectedVersion, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteWithVersionAsync<T>(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);
        DocumentOperations.ValidateExpectedVersion(expectedVersion);

        return RunAsync(ops => ops.DeleteWithVersionAsync<T>(id, expectedVersion, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PatchAsync<T>(
        string id,
        DocumentPatch<T> patch,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);
        ArgumentNullException.ThrowIfNull(patch);

        return RunAsync(ops => ops.PatchAsync(id, patch, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PatchWithVersionAsync<T>(
        string id,
        DocumentPatch<T> patch,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        // The version first, matching the operation: PatchWithVersionAsync screens it before
        // delegating to the shared core that checks the id and the patch.
        DocumentOperations.ValidateExpectedVersion(expectedVersion);
        DocumentOperations.ValidateId(id);
        ArgumentNullException.ThrowIfNull(patch);

        return RunAsync(
            ops => ops.PatchWithVersionAsync(id, patch, expectedVersion, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<VersionedDocument<T>?> GetWithVersionAsync<T>(
        string id,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.GetWithVersionAsync<T>(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.GetAsync<T>(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.GetAllAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, T>> GetManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        // See UpsertManyAsync: the per-element check stays in the operation so the sequence is
        // enumerated once.
        ArgumentNullException.ThrowIfNull(ids);

        return RunAsync(ops => ops.GetManyAsync<T>(ids, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.DeleteAsync<T>(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        // See UpsertManyAsync.
        ArgumentNullException.ThrowIfNull(ids);

        return RunAsync(ops => ops.DeleteManyAsync<T>(ids, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteAllAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DeleteAllAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.ExistsAsync<T>(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CountAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T, TValue>(
        string jsonPath,
        TValue value,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateQueryJsonPath(jsonPath);
        ArgumentNullException.ThrowIfNull(value);

        return RunAsync(ops => ops.QueryAsync<T, TValue>(jsonPath, value, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return RunAsync(ops => ops.QueryAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return RunAsync(ops => ops.CountAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return RunAsync(ops => ops.ExistsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        return RunAsync(ops => ops.CreateIndexAsync(jsonPath, indexName, null, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jsonPath);

        return RunAsync(ops => ops.CreateIndexAsync(jsonPath, indexName, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        string jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateJsonPathArgument(jsonPath);

        return RunAsync(ops => ops.CreateIndexAsync<T>(jsonPath, indexName, null, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateIndexAsync<T>(
        string jsonPath,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DocumentOperations.ValidateJsonPathArgument(jsonPath);

        return RunAsync(ops => ops.CreateIndexAsync<T>(jsonPath, indexName, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateJsonPathExpressions(jsonPaths);

        return RunAsync(
            ops => ops.CreateCompositeIndexAsync(jsonPaths, indexName, null, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DocumentOperations.ValidateJsonPathExpressions(jsonPaths);

        return RunAsync(
            ops => ops.CreateCompositeIndexAsync(jsonPaths, indexName, options, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        string[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateJsonPathArguments(jsonPaths);

        return RunAsync(
            ops => ops.CreateCompositeIndexAsync<T>(jsonPaths, indexName, null, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateCompositeIndexAsync<T>(
        string[] jsonPaths,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DocumentOperations.ValidateJsonPathArguments(jsonPaths);

        return RunAsync(
            ops => ops.CreateCompositeIndexAsync<T>(jsonPaths, indexName, options, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default)
    {
        // Only the expression: the column name is screened by the string overload the operation
        // delegates to, after the path has been resolved, and hoisting it here would reorder the
        // two for a call that gets both wrong.
        ArgumentNullException.ThrowIfNull(jsonPath);

        return RunAsync(
            ops => ops.AddVirtualColumnAsync(jsonPath, columnName, createIndex, columnType, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task AddVirtualColumnAsync<T>(
        string jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateJsonPathArgument(jsonPath);
        DocumentOperations.ValidateColumnName(columnName);

        return RunAsync(
            ops => ops.AddVirtualColumnAsync<T>(jsonPath, columnName, createIndex, columnType, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DropTableAsync<T>(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DropTableAsync<T>(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task DropIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateIndexName(indexName);

        return RunAsync(ops => ops.DropIndexAsync(indexName, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task DropIndexAsync<T>(
        Expression<Func<T, object>> expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return RunAsync(ops => ops.DropIndexAsync(expression, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateBlobTableAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateBlobTableAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> RebuildBlobTableAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.RebuildBlobTableAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateBlobWriteArguments(id, data, null);

        return RunAsync(ops => ops.PutBlobAsync(id, data, null, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        BlobWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DocumentOperations.ValidateBlobWriteArguments(id, data, options);

        return RunAsync(ops => ops.PutBlobAsync(id, data, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBlobAsync(
        string id,
        Stream source,
        long length,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateBlobStreamArguments(id, source, length, null, null);

        return RunAsync(
            ops => ops.PutBlobAsync(id, source, length, null, null, cancellationToken),
            cancellationToken);
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
        DocumentOperations.ValidateBlobStreamArguments(id, source, length, options, null);

        return RunAsync(
            ops => ops.PutBlobAsync(id, source, length, options, null, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        ReadOnlyMemory<byte> data,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateBlobWriteArguments(id, data, null);
        DocumentOperations.ValidateExpectedVersion(expectedVersion);

        return RunAsync(
            ops => ops.PutBlobWithVersionAsync(id, data, expectedVersion, null, cancellationToken),
            cancellationToken);
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
        DocumentOperations.ValidateBlobWriteArguments(id, data, options);
        DocumentOperations.ValidateExpectedVersion(expectedVersion);

        return RunAsync(
            ops => ops.PutBlobWithVersionAsync(id, data, expectedVersion, options, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> PutBlobWithVersionAsync(
        string id,
        Stream source,
        long length,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateBlobStreamArguments(id, source, length, null, expectedVersion);

        return RunAsync(
            ops => ops.PutBlobAsync(id, source, length, null, expectedVersion, cancellationToken),
            cancellationToken);
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
        DocumentOperations.ValidateBlobStreamArguments(id, source, length, options, expectedVersion);

        return RunAsync(
            ops => ops.PutBlobAsync(id, source, length, options, expectedVersion, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<long?> BlobLengthAsync(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.BlobLengthAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenBlobReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ThrowIfDisposed();

        // Its own connection, not a rented one: the stream lives until the caller disposes it,
        // and a pooled slot held for that long by a caller who forgets would starve the store.
        // Bounded all the same, by a separate count of concurrently open streams — otherwise
        // nothing would stop a caller opening connections without limit.
        var slot = await _pool.RentBlobStreamSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var connection = await _pool.CreateUnpooledConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await BlobReadStream.OpenAsync(connection, id, slot, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Idempotent, so this is safe even though OpenAsync releases on its own failure paths.
            slot.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> GetBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.GetBlobAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.DeleteBlobAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteBlobWithVersionAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);
        DocumentOperations.ValidateExpectedVersion(expectedVersion);

        return RunAsync(
            ops => ops.DeleteBlobWithVersionAsync(id, expectedVersion, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<BlobMetadata?> GetBlobMetadataAsync(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.GetBlobMetadataAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BlobMetadata>> ListBlobsAsync(
        string? idPrefix = null,
        int skip = 0,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateListBlobsArguments(skip, take);

        return RunAsync(ops => ops.ListBlobsAsync(idPrefix, skip, take, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        DocumentOperations.ValidateId(id);

        return RunAsync(ops => ops.BlobExistsAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TResult> ExecuteRawAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        // Not 'await using': the callback has had the raw connection, so it may have left a
        // transaction on it — see PooledConnection.ReturnAfterExternalAccess.
        var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(lease.Connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.ReturnAfterExternalAccess();
        }
    }

    /// <inheritdoc />
    public async Task ExecuteRawAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        // See the generic overload: the callback owns the connection for its duration.
        var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(lease.Connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.ReturnAfterExternalAccess();
        }
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
    public Task<IDocumentTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        BeginTransactionAsync(TransactionMode.Deferred, cancellationToken);

    /// <inheritdoc />
    public async Task<IDocumentTransaction> BeginTransactionAsync(
        TransactionMode mode,
        CancellationToken cancellationToken = default)
    {
        // Ahead of the disposal guard, like every other argument check on this surface: an
        // unknown mode is a caller bug whatever state the store is in.
        if (mode is not (TransactionMode.Deferred or TransactionMode.Immediate))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transaction mode.");
        }

        ThrowIfDisposed();

        var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deferred stays the default: BEGIN takes no lock, so a read-only transaction never
            // blocks a writer. The cost is that a read-then-write transaction pins a snapshot at
            // its first read and fails the upgrade with SQLITE_BUSY_SNAPSHOT if anyone commits
            // in between — measured, and busy_timeout cannot retry it (Microsoft.Data.Sqlite
            // retries anyway until the connection's command timeout, so the caller stalls first;
            // the factory caps that at BusyTimeoutMs, but a connection string stating its own
            // Default Timeout keeps it).
            // Immediate takes the write lock before any snapshot exists, turning that into a
            // plain SQLITE_BUSY wait at BEGIN that busy_timeout does retry.
            var transaction = lease.Connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable,
                deferred: mode == TransactionMode.Deferred);

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
    public Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(action, TransactionMode.Deferred, cancellationToken);

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(
        Func<IDocumentTransaction, Task> action,
        TransactionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var transaction = await BeginTransactionAsync(mode, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public Task<int> MigrateAsync(
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default) =>
        MigrateAsync(migrations, MigrationOptions.Default, cancellationToken);

    /// <inheritdoc />
    public Task<int> MigrateAsync(
        IEnumerable<IMigration> migrations,
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        // Before the rent, not after: a bad argument should not first wait for a free
        // connection, and on a cancelled token that wait throws OperationCanceledException
        // instead of the argument exception.
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(options);

        // One lease for the whole run; each migration still commits in its own transaction.
        return RunMigrationAsync(
            runner => runner.ApplyMigrationsAsync(migrations, options, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MigrationHistoryRecord>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken = default) =>
        RunMigrationAsync(runner => runner.GetAppliedMigrationsAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<long> GetCurrentMigrationVersionAsync(CancellationToken cancellationToken = default) =>
        RunMigrationAsync(runner => runner.GetCurrentVersionAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<int> RollbackToVersionAsync(
        long targetVersion,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetVersion);
        ArgumentNullException.ThrowIfNull(migrations);

        return RunMigrationAsync(
            runner => runner.RollbackToVersionAsync(targetVersion, migrations, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Rents a connection and runs one migration operation on a runner built over it.
    /// </summary>
    private async Task<TResult> RunMigrationAsync<TResult>(
        Func<MigrationRunner, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // A migration's UpAsync/DownAsync runs arbitrary SQL on this connection, so it is checked
        // like an ExecuteRawAsync callback rather than like a store operation.
        var lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(new MigrationRunner(lease.Connection, _logger)).ConfigureAwait(false);
        }
        finally
        {
            lease.ReturnAfterExternalAccess();
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
        new(connection, _tableNamingConvention, _serializerOptions, _logger, inAmbientTransaction: false);

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

        // The pool closes in a finally: _disposed is already set, so a throw out of the
        // checkpoint would leave every pooled connection — and its file lock — open until the
        // process ends, with no second chance to close them. The checkpoint swallows its own
        // failures, so this guards against what is added to that method later.
        try
        {
            await PerformWalCheckpointAsync().ConfigureAwait(false);
        }
        finally
        {
            await _pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="DisposeAsync" />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // See DisposeAsync.
        try
        {
            PerformWalCheckpoint();
        }
        finally
        {
            _pool.Dispose();
        }
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
                _logger.LogDebugQuietly("Executing WAL checkpoint before disposal");
                // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                await lease.Connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)", CancellationToken.None)
                    .ConfigureAwait(false);
                _logger.LogInformationQuietly("WAL checkpoint completed successfully");
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarningQuietly(ex, "Failed to perform WAL checkpoint during disposal");
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
                _logger.LogDebugQuietly("Executing WAL checkpoint before disposal");
                // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                lease.Connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                _logger.LogInformationQuietly("WAL checkpoint completed successfully");
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarningQuietly(ex, "Failed to perform WAL checkpoint during disposal");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
