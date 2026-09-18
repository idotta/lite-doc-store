using System.Buffers;
using System.Data;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// Every document operation, executed against one supplied connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the single implementation shared by <see cref="DocumentStore"/> (which rents a
/// connection per operation) and <see cref="DocumentStoreTransaction"/> (which holds one
/// connection for the transaction's lifetime). Commands are created through
/// <see cref="SqliteConnection.CreateCommand"/>, so they enlist in that connection's active
/// transaction automatically.
/// </para>
/// <para>
/// It is a struct so the per-operation rent path allocates nothing beyond the command itself.
/// </para>
/// <para>
/// Every operation takes a required cancellation token: the public defaults live on
/// <see cref="IDocumentOperations"/>, and making them explicit here keeps a caller's token
/// from being dropped by an overload resolving to a default.
/// </para>
/// </remarks>
internal readonly struct DocumentOperations
{
    private readonly SqliteConnection _connection;
    private readonly ITableNamingConvention _tableNamingConvention;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ILogger _logger;
    private readonly bool _inAmbientTransaction;

    // The buffer a streamed blob write copies through. 80 KB is Stream.CopyTo's own default and
    // is rented, so a large payload never sizes an allocation to itself.
    private const int BlobCopyBufferSize = 81920;

    internal DocumentOperations(
        SqliteConnection connection,
        ITableNamingConvention tableNamingConvention,
        JsonSerializerOptions serializerOptions,
        ILogger logger,
        bool inAmbientTransaction)
    {
        _connection = connection;
        _tableNamingConvention = tableNamingConvention;
        _serializerOptions = serializerOptions;
        _logger = logger;
        _inAmbientTransaction = inAmbientTransaction;
    }

    // ---- Argument validation -------------------------------------------------------------
    //
    // These run at both boundaries, and the duplication is the point. DocumentStoreTransaction
    // calls straight into the operations below, so the guards have to stay here; DocumentStore
    // rents a pooled connection first, so a guard that only lived here would make a bad argument
    // wait for a free connection — on a saturated pool that surfaces as a TimeoutException
    // blaming undisposed transactions, or as an OperationCanceledException, instead of the
    // ArgumentException the caller earned. Sharing one helper per guard is what keeps the two
    // copies from drifting. Every one of them is pure and idempotent, so running it twice on the
    // store path costs nothing but the check.

    /// <summary>Rejects a null, empty or whitespace document or blob id.</summary>
    internal static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }
    }

    /// <summary>Rejects a negative expected version on a compare-and-swap operation.</summary>
    internal static void ValidateExpectedVersion(long expectedVersion) =>
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

    /// <summary>Rejects a null, empty or whitespace generated-column name.</summary>
    internal static void ValidateColumnName(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ArgumentException("Column name cannot be null or empty.", nameof(columnName));
        }
    }

    /// <summary>Rejects a null, empty or whitespace index name.</summary>
    internal static void ValidateIndexName(string? indexName)
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            throw new ArgumentException("Index name cannot be null or empty.", nameof(indexName));
        }
    }

    /// <summary>Rejects a blank JSON path argument on the DDL overloads.</summary>
    internal static void ValidateJsonPathArgument(string? jsonPath) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

    /// <summary>Rejects a blank JSON path argument on the simple query overload.</summary>
    internal static void ValidateQueryJsonPath(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be null or empty.", nameof(jsonPath));
        }
    }

    /// <summary>Rejects a null, empty or blank-element composite index path array.</summary>
    internal static void ValidateJsonPathArguments(string[] jsonPaths)
    {
        ArgumentNullException.ThrowIfNull(jsonPaths);
        if (jsonPaths.Length == 0)
        {
            throw new ArgumentException("At least one JSON path is required for composite index.", nameof(jsonPaths));
        }

        foreach (var path in jsonPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(jsonPaths));
        }
    }

    /// <inheritdoc cref="ValidateJsonPathArguments" />
    internal static void ValidateJsonPathExpressions<T>(Expression<Func<T, object>>[] jsonPaths)
    {
        ArgumentNullException.ThrowIfNull(jsonPaths);
        if (jsonPaths.Length == 0)
        {
            throw new ArgumentException("At least one JSON path is required for composite index.", nameof(jsonPaths));
        }

        foreach (var path in jsonPaths)
        {
            ArgumentNullException.ThrowIfNull(path, nameof(jsonPaths));
        }
    }

    /// <summary>Rejects a byte-array blob write the store cannot honour.</summary>
    internal static void ValidateBlobWriteArguments(
        string? id,
        ReadOnlyMemory<byte> data,
        BlobWriteOptions? options)
    {
        ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)data.Length, BlobLimits.MaxBlobLength, nameof(data));
        options?.Validate();
    }

    /// <summary>
    /// Rejects a streamed blob write the store cannot honour, before anything is written.
    /// </summary>
    /// <remarks>
    /// Only inspects the source's capabilities and, when it is seekable, its length and position
    /// — never its bytes — so it is safe to run on both boundaries.
    /// </remarks>
    internal static void ValidateBlobStreamArguments(
        string? id,
        Stream source,
        long length,
        BlobWriteOptions? options,
        long? expectedVersion)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, BlobLimits.MaxBlobLength);
        options?.Validate();

        if (expectedVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream is not readable.", nameof(source));
        }

        // A seekable source can be measured before anything is written, so a wrong length fails
        // the call rather than the copy. A non-seekable one cannot: see CopyExactlyAsync, which
        // reads exactly 'length' bytes and never probes past them.
        if (source.CanSeek)
        {
            var available = source.Length - source.Position;
            if (available != length)
            {
                throw new ArgumentException(
                    $"The source stream holds {available} bytes from its current position, " +
                    $"but {length} were declared.",
                    nameof(length));
            }
        }
    }

    /// <summary>Rejects a negative paging window on a blob listing.</summary>
    internal static void ValidateListBlobsArguments(int skip, int? take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }
    }

    /// <inheritdoc cref="IDocumentOperations.CreateTableAsync{T}" />
    public async Task CreateTableAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateCreateTableSql(tableName);

        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.UpsertAsync{T}" />
    public async Task<int> UpsertAsync<T>(string id, T data, CancellationToken cancellationToken)
    {
        ValidateId(id);

        ArgumentNullException.ThrowIfNull(data);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var jsonBytes = JsonHelper.SerializeToUtf8Bytes(data, _serializerOptions);
        var sql = SqlGenerator.GenerateUpsertSql(tableName);

        return await _connection.ExecuteAsync(sql, cancellationToken, ("Id", id), ("Data", jsonBytes))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.UpsertManyAsync{T}" />
    public async Task<int> UpsertManyAsync<T>(
        IEnumerable<(string id, T data)> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemsList = items.ToList();
        if (itemsList.Count == 0)
        {
            _logger.LogDebug("UpsertManyAsync called with empty collection, skipping");
            return 0;
        }

        var tableName = _tableNamingConvention.GetTableName<T>();

        // Validate and serialize every item up front, so a bad item anywhere in the batch
        // throws before the first chunk is written.
        var seen = new Dictionary<string, int>(itemsList.Count, StringComparer.Ordinal);
        var payloads = new (string Id, byte[] Data)[itemsList.Count];
        for (int i = 0; i < itemsList.Count; i++)
        {
            var (id, data) = itemsList[i];
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException($"ID at index {i} cannot be null or empty.", nameof(items));
            }
            if (data == null)
            {
                throw new ArgumentException($"Data at index {i} cannot be null.", nameof(items));
            }
            if (!seen.TryAdd(id, i))
            {
                throw new ArgumentException(
                    $"Duplicate ID '{id}' at indexes {seen[id]} and {i}. A single batch cannot " +
                    "write the same document twice; de-duplicate the input first.",
                    nameof(items));
            }

            payloads[i] = (id, JsonHelper.SerializeToUtf8Bytes(data, _serializerOptions));
        }

        return await RunBatchAsync(
            payloads.Length,
            (offset, count) =>
            {
                var sql = SqlGenerator.GenerateBulkUpsertSql(tableName, count);
                var parameters = new (string, object?)[count * 2];
                for (int i = 0; i < count; i++)
                {
                    var (id, data) = payloads[offset + i];
                    parameters[i * 2] = ($"Id{i}", id);
                    parameters[(i * 2) + 1] = ($"Data{i}", data);
                }

                return (sql, parameters);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.UpsertWithVersionAsync{T}" />
    public async Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        ArgumentNullException.ThrowIfNull(data);
        ValidateExpectedVersion(expectedVersion);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var jsonBytes = JsonHelper.SerializeToUtf8Bytes(data, _serializerOptions);

        long? newVersion;
        if (expectedVersion == 0)
        {
            newVersion = await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateInsertIfAbsentSql(tableName),
                cancellationToken, ("Id", id), ("Data", jsonBytes))
                .ConfigureAwait(false);

            // No row back means the id is taken. A row left at version 0 by raw SQL (the old
            // column default) is still CAS-able: the version-guarded update matches 0 and lifts
            // it to 1, so such a row is not stuck outside the concurrency model forever.
            newVersion ??= await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateVersionedUpdateSql(tableName),
                cancellationToken, ("Id", id), ("Data", jsonBytes), ("ExpectedVersion", 0L))
                .ConfigureAwait(false);
        }
        else
        {
            newVersion = await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateVersionedUpdateSql(tableName),
                cancellationToken, ("Id", id), ("Data", jsonBytes), ("ExpectedVersion", expectedVersion))
                .ConfigureAwait(false);
        }

        if (newVersion is null)
        {
            throw await BuildConflictAsync(
                "writing", "document", id, tableName, expectedVersion,
                insertAttempt: expectedVersion == 0, cancellationToken).ConfigureAwait(false);
        }

        return newVersion.Value;
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteWithVersionAsync{T}" />
    public async Task DeleteWithVersionAsync<T>(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        ValidateExpectedVersion(expectedVersion);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateVersionedDeleteSql(tableName);

        var affectedRows = await _connection.ExecuteAsync(
            sql, cancellationToken, ("Id", id), ("ExpectedVersion", expectedVersion))
            .ConfigureAwait(false);

        if (affectedRows == 0)
        {
            throw await BuildConflictAsync(
                "deleting", "document", id, tableName, expectedVersion,
                insertAttempt: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="IDocumentOperations.PatchAsync{T}" />
    public Task<long> PatchAsync<T>(
        string id,
        DocumentPatch<T> patch,
        CancellationToken cancellationToken) =>
        PatchCoreAsync(id, patch, expectedVersion: null, cancellationToken);

    /// <inheritdoc cref="IDocumentOperations.PatchWithVersionAsync{T}" />
    public Task<long> PatchWithVersionAsync<T>(
        string id,
        DocumentPatch<T> patch,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateExpectedVersion(expectedVersion);
        return PatchCoreAsync(id, patch, expectedVersion, cancellationToken);
    }

    /// <summary>
    /// Applies a patch as one statement, optionally guarded by an expected version.
    /// </summary>
    /// <remarks>
    /// A patch cannot insert — it carries no full document — so a missing row is a conflict
    /// rather than a no-op, unlike <see cref="DeleteAsync{T}"/>.
    /// </remarks>
    private async Task<long> PatchCoreAsync<T>(
        string id,
        DocumentPatch<T> patch,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        ArgumentNullException.ThrowIfNull(patch);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var generated = SqlGenerator.GeneratePatchSql(tableName, patch.Operations, expectedVersion.HasValue);

        var parameters = expectedVersion.HasValue
            ? BindPositionally(generated, ("Id", id), ("ExpectedVersion", expectedVersion.Value))
            : BindPositionally(generated, ("Id", id));

        var newVersion = await _connection
            .QueryFirstInt64Async(generated.Sql, cancellationToken, parameters)
            .ConfigureAwait(false);

        if (newVersion is null)
        {
            throw await BuildConflictAsync(
                "patching", "document", id, tableName, expectedVersion,
                insertAttempt: false, cancellationToken).ConfigureAwait(false);
        }

        return newVersion.Value;
    }

    /// <summary>
    /// Builds the <see cref="ConcurrencyException"/> for a version-guarded write or delete that
    /// affected no row, reading the stored version so the caller can see both sides.
    /// </summary>
    /// <remarks>
    /// The extra SELECT runs only on the conflict path, so the happy path pays nothing for it.
    /// It is a separate statement, so outside a transaction it observes the row as it stands
    /// afterwards rather than at the instant the guard rejected the operation.
    /// <para>
    /// <c>insertAttempt</c> is true only for a write that requested an insert, so a taken id
    /// reads as <see cref="ConcurrencyConflictKind.AlreadyExists"/>. A version-guarded delete
    /// passes false: expected version 0 there means "delete the row still at 0" (a legacy row
    /// written by raw SQL under the old column default), not "insert".
    /// </para>
    /// <para>
    /// A null <paramref name="expectedVersion"/> is an unguarded operation — an unversioned
    /// patch. It has no version to mismatch, so no row back means no such document and the
    /// stored-version read is skipped entirely.
    /// </para>
    /// </remarks>
    private async Task<ConcurrencyException> BuildConflictAsync(
        string verb,
        string entity,
        string id,
        string tableName,
        long? expectedVersion,
        bool insertAttempt,
        CancellationToken cancellationToken)
    {
        var actualVersion = expectedVersion is null
            ? null
            : await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateGetVersionSql(tableName), cancellationToken, ("Id", id))
                .ConfigureAwait(false);

        // An insert that reached here found the id taken at a version the 0-guard could not
        // match — so it is reported as already existing, unless the row is gone by the time
        // this reads it (a concurrent delete), which reads as not found.
        var kind = actualVersion is null
            ? ConcurrencyConflictKind.DocumentNotFound
            : insertAttempt
                ? ConcurrencyConflictKind.AlreadyExists
                : ConcurrencyConflictKind.VersionMismatch;

        var reason = kind switch
        {
            ConcurrencyConflictKind.DocumentNotFound => $"the {entity} does not exist",
            ConcurrencyConflictKind.AlreadyExists =>
                $"the {entity} already exists at version {actualVersion}",
            _ => $"the stored version {actualVersion} does not match the expected version {expectedVersion}",
        };

        return new ConcurrencyException(
            $"Concurrency conflict {verb} {entity} '{id}' in table '{tableName}': {reason}.",
            id, tableName, expectedVersion, actualVersion, kind);
    }

    /// <inheritdoc cref="IDocumentOperations.GetWithVersionAsync{T}" />
    public async Task<VersionedDocument<T>?> GetWithVersionAsync<T>(
        string id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetWithVersionSql(tableName);

        var row = await _connection.QueryFirstStringInt64Async(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return null;
        }

        var (json, version) = row.Value;

        // The row exists. A projection carrying no payload is a corrupt row, and testing the text
        // for emptiness instead of testing row presence reported it as not found.
        EnsureDocumentPayload<T>(json, id, tableName);

        var document = JsonHelper.Deserialize<T>(json, _serializerOptions);
        if (document is null)
        {
            // Returning null here would read as "not found" and hide a real row.
            throw NullDocument<T>(id, tableName);
        }

        return new VersionedDocument<T>(document, version);
    }

    /// <inheritdoc cref="IDocumentOperations.GetAsync{T}" />
    public async Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetByIdSql(tableName);

        // Row presence, not the projected text, decides "not found": QueryFirstStringAsync maps
        // both no-row and a NULL data column to null, so it cannot tell them apart.
        var (json, found) = await _connection.QueryFirstStringRowAsync(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);

        if (!found)
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return default;
        }

        // Checked before deserializing: JsonHelper maps empty JSON to default(T), which for a
        // value type is not null, so the guard below would let a corrupt row read back as 0.
        EnsureDocumentPayload<T>(json, id, tableName);

        var document = JsonHelper.Deserialize<T>(json, _serializerOptions);
        if (document is null)
        {
            // The row exists; returning default would be indistinguishable from not found.
            throw NullDocument<T>(id, tableName);
        }

        return document;
    }

    /// <inheritdoc cref="IDocumentOperations.GetAllAsync{T}" />
    public async Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetAllSql(tableName);

        var rows = await _connection.QueryStringPairsAsync(sql, cancellationToken).ConfigureAwait(false);
        return DeserializeResults<T>(rows, tableName);
    }

    /// <inheritdoc cref="IDocumentOperations.GetManyAsync{T}" />
    public async Task<IReadOnlyDictionary<string, T>> GetManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idsList = ids.ToList();
        if (idsList.Count == 0)
        {
            _logger.LogDebug("GetManyAsync called with empty collection, skipping");
            return ReadOnlyDictionary<string, T>.Empty;
        }

        // Validated like DeleteManyAsync, and repeats dropped for the same reason: an
        // 'id IN (...)' list is unambiguous, and the result is keyed by id anyway.
        var distinctIds = new List<string>(idsList.Count);
        var seen = new HashSet<string>(idsList.Count, StringComparer.Ordinal);
        for (int i = 0; i < idsList.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(idsList[i]))
            {
                throw new ArgumentException($"ID at index {i} cannot be null or empty.", nameof(ids));
            }
            if (seen.Add(idsList[i]))
            {
                distinctIds.Add(idsList[i]);
            }
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var documents = new Dictionary<string, T>(distinctIds.Count, StringComparer.Ordinal);

        // Chunked like a batch write, but not through RunBatchAsync: that sums affected-row
        // counts, which a read does not produce, and a read needs no enclosing transaction.
        const int chunkSize = SqlGenerator.MaxBatchItemsPerStatement;
        for (int offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, distinctIds.Count - offset);
            var sql = SqlGenerator.GenerateBulkGetSql(tableName, count);
            var parameters = new (string, object?)[count];
            for (int i = 0; i < count; i++)
            {
                parameters[i] = ($"Id{i}", distinctIds[offset + i]);
            }

            var rows = await _connection.QueryStringPairsAsync(sql, cancellationToken, parameters)
                .ConfigureAwait(false);

            foreach (var (id, json) in rows)
            {
                // A row that reads back as nothing throws instead of being skipped, so a broken
                // row cannot masquerade as a missing document. The id cannot be null: an
                // 'id IN (...)' list never matches one.
                EnsureDocumentPayload<T>(json, id, tableName);

                if (JsonHelper.Deserialize<T>(json, _serializerOptions) is not { } document)
                {
                    throw NullDocument<T>(id, tableName);
                }

                documents[id!] = document;
            }
        }

        if (documents.Count < distinctIds.Count)
        {
            _logger.LogDebug(
                "GetManyAsync found {FoundCount} of {RequestedCount} documents in table {TableName}",
                documents.Count, distinctIds.Count, tableName);
        }

        return documents;
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteAsync{T}" />
    public async Task<bool> DeleteAsync<T>(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateDeleteSql(tableName);

        var affectedRows = await _connection.ExecuteAsync(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
        var deleted = affectedRows > 0;

        if (!deleted)
        {
            _logger.LogDebug("Document {Id} not found in table {TableName} (nothing to delete)", id, tableName);
        }

        return deleted;
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteManyAsync{T}" />
    public async Task<int> DeleteManyAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idsList = ids.ToList();
        if (idsList.Count == 0)
        {
            _logger.LogDebug("DeleteManyAsync called with empty collection, skipping");
            return 0;
        }

        // Validate every ID up front. Repeats are dropped rather than rejected: an
        // 'id IN (...)' list is unambiguous, and the deleted-row count is unaffected.
        var distinctIds = new List<string>(idsList.Count);
        var seen = new HashSet<string>(idsList.Count, StringComparer.Ordinal);
        for (int i = 0; i < idsList.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(idsList[i]))
            {
                throw new ArgumentException($"ID at index {i} cannot be null or empty.", nameof(ids));
            }
            if (seen.Add(idsList[i]))
            {
                distinctIds.Add(idsList[i]);
            }
        }

        var tableName = _tableNamingConvention.GetTableName<T>();

        return await RunBatchAsync(
            distinctIds.Count,
            (offset, count) =>
            {
                var sql = SqlGenerator.GenerateBulkDeleteSql(tableName, count);
                var parameters = new (string, object?)[count];
                for (int i = 0; i < count; i++)
                {
                    parameters[i] = ($"Id{i}", distinctIds[offset + i]);
                }

                return (sql, parameters);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a batch as chunks of at most
    /// <see cref="SqlGenerator.MaxBatchItemsPerStatement"/> items and returns the total
    /// affected rows.
    /// </summary>
    /// <remarks>
    /// A multi-chunk batch is wrapped in a transaction so it stays all-or-nothing. Inside an
    /// ambient transaction nothing is started — the chunks enlist in it and are committed or
    /// rolled back with it. A single-chunk batch is one statement, already atomic.
    /// </remarks>
    private async Task<int> RunBatchAsync(
        int totalItems,
        Func<int, int, (string Sql, (string, object?)[] Parameters)> chunkFactory,
        CancellationToken cancellationToken)
    {
        const int chunkSize = SqlGenerator.MaxBatchItemsPerStatement;

        // Copied to a local: this is a struct, so the local function below cannot capture 'this'.
        var connection = _connection;

        if (totalItems <= chunkSize || _inAmbientTransaction)
        {
            return await ExecuteChunksAsync().ConfigureAwait(false);
        }

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affected = await ExecuteChunksAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;

        async Task<int> ExecuteChunksAsync()
        {
            var affectedRows = 0;
            for (int offset = 0; offset < totalItems; offset += chunkSize)
            {
                var (sql, parameters) = chunkFactory(offset, Math.Min(chunkSize, totalItems - offset));
                affectedRows += await connection
                    .ExecuteAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
            }

            return affectedRows;
        }
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteAllAsync{T}" />
    public async Task<int> DeleteAllAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateDeleteAllSql(tableName);

        return await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.ExistsAsync{T}(string, CancellationToken)" />
    public async Task<bool> ExistsAsync<T>(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateExistsSql(tableName);

        return await _connection.ExecuteScalarAsync<bool>(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.CountAsync{T}(CancellationToken)" />
    public async Task<long> CountAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateCountSql(tableName);

        return await _connection.ExecuteScalarAsync<long>(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.QueryAsync{T, TValue}" />
    public async Task<IEnumerable<T>> QueryAsync<T, TValue>(
        string jsonPath,
        TValue value,
        CancellationToken cancellationToken)
    {
        ValidateQueryJsonPath(jsonPath);

        ArgumentNullException.ThrowIfNull(value);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateQueryByJsonPathSql(tableName, jsonPath);

        // Same binding hazard as the structured API, so the same normalizer — otherwise a
        // DateTime, Guid, decimal, float, byte[] or huge ulong here matches nothing.
        var bound = DocumentQuery<T>.NormalizeBoundValue(value);

        var rows = await _connection.QueryStringPairsAsync(sql, cancellationToken, ("Value", bound))
            .ConfigureAwait(false);
        return DeserializeResults<T>(rows, tableName);
    }

    /// <inheritdoc cref="IDocumentOperations.QueryAsync{T}(DocumentQuery{T}, CancellationToken)" />
    public async Task<IEnumerable<T>> QueryAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var generated = SqlGenerator.GenerateQuerySql(
            tableName,
            query.Predicates,
            query.Orderings,
            query.SkipCount,
            query.TakeCount);

        var rows = await _connection
            .QueryStringPairsAsync(generated.Sql, cancellationToken, BindPositionally(generated))
            .ConfigureAwait(false);
        return DeserializeResults<T>(rows, tableName);
    }

    /// <inheritdoc cref="IDocumentOperations.CountAsync{T}(DocumentQuery{T}, CancellationToken)" />
    public async Task<long> CountAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var generated = SqlGenerator.GenerateFilteredCountSql(tableName, query.Predicates);

        return await _connection
            .ExecuteScalarAsync<long>(generated.Sql, cancellationToken, BindPositionally(generated))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.ExistsAsync{T}(DocumentQuery{T}, CancellationToken)" />
    public async Task<bool> ExistsAsync<T>(DocumentQuery<T> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var generated = SqlGenerator.GenerateFilteredExistsSql(tableName, query.Predicates);

        return await _connection
            .ExecuteScalarAsync<bool>(generated.Sql, cancellationToken, BindPositionally(generated))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Names the generator's values <c>p0..pN</c>, matching the <c>@p0..@pN</c> placeholders it
    /// emitted in the same left-to-right pass, followed by any named parameters the statement
    /// binds on top of them (a patch's <c>@Id</c> and <c>@ExpectedVersion</c>).
    /// </summary>
    private static (string Name, object? Value)[] BindPositionally(
        GeneratedQuery generated,
        params (string Name, object? Value)[] named)
    {
        var values = generated.ParameterValues;
        var parameters = new (string Name, object? Value)[values.Count + named.Length];

        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = ("p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        }

        named.CopyTo(parameters, values.Count);
        return parameters;
    }

    /// <inheritdoc cref="IDocumentOperations.CreateIndexAsync{T}(Expression{Func{T, object}}, string, IndexOptions, CancellationToken)" />
    public Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        return CreateIndexAsync<T>(
            ExtractJsonPath(jsonPath, nameof(jsonPath)),
            indexName,
            options,
            cancellationToken);
    }

    /// <inheritdoc cref="IDocumentOperations.CreateIndexAsync{T}(string, string, IndexOptions, CancellationToken)" />
    public async Task CreateIndexAsync<T>(
        string jsonPath,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ValidateJsonPathArgument(jsonPath);

        var tableName = _tableNamingConvention.GetTableName<T>();

        // Validated before the name is derived from it: a bad path otherwise reaches the
        // generator inside the derived index name and is reported against indexName. The root
        // is rejected here rather than only in the generator for the same reason — auto-naming
        // would otherwise turn it into "idx_T_$" and blame indexName.
        var pathString = SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false);

        string finalIndexName;
        if (indexName is null)
        {
            RequireDerivableName(tableName, pathString, nameof(jsonPath));
            finalIndexName = GenerateIndexName(tableName, pathString);
        }
        else
        {
            finalIndexName = indexName;
        }

        // Generated before the pre-check so an invalid identifier, path or collation throws
        // whether or not the index happens to exist already: a bad argument is a bad argument.
        var sql = SqlGenerator.GenerateCreateJsonIndexSql(tableName, finalIndexName, pathString, options);

        var created = await CreateIndexUnlessIdenticalAsync(
            finalIndexName,
            sql,
            SqlGenerator.GenerateCreateJsonIndexSql(
                tableName, finalIndexName, pathString, options, ifNotExists: false),
            cancellationToken).ConfigureAwait(false);

        if (!created)
        {
            _logger.LogDebug("Index {IndexName} already exists, skipping creation", finalIndexName);
        }
    }

    /// <inheritdoc cref="IDocumentOperations.CreateCompositeIndexAsync{T}(Expression{Func{T, object}}[], string, IndexOptions, CancellationToken)" />
    public Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ValidateJsonPathExpressions(jsonPaths);

        var pathStrings = new string[jsonPaths.Length];
        for (var i = 0; i < jsonPaths.Length; i++)
        {
            pathStrings[i] = ExtractJsonPath(jsonPaths[i], nameof(jsonPaths));
        }

        return CreateCompositeIndexAsync<T>(pathStrings, indexName, options, cancellationToken);
    }

    /// <inheritdoc cref="IDocumentOperations.CreateCompositeIndexAsync{T}(string[], string, IndexOptions, CancellationToken)" />
    public async Task CreateCompositeIndexAsync<T>(
        string[] jsonPaths,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ValidateJsonPathArguments(jsonPaths);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathStrings = new List<string>(jsonPaths.Length);

        // Validated before the name is derived from them, for the reason in CreateIndexAsync.
        foreach (var path in jsonPaths)
        {
            pathStrings.Add(SqlGenerator.ValidateJsonPath(path, nameof(jsonPaths), allowRoot: false));
        }

        string finalIndexName;
        if (indexName is null)
        {
            foreach (var path in pathStrings)
            {
                RequireDerivableName(tableName, path, nameof(jsonPaths));
            }

            finalIndexName = GenerateCompositeIndexName(tableName, pathStrings);
        }
        else
        {
            finalIndexName = indexName;
        }

        // Generated before the pre-check, for the reason in CreateIndexAsync.
        var sql = SqlGenerator.GenerateCreateCompositeJsonIndexSql(tableName, finalIndexName, pathStrings, options);

        var created = await CreateIndexUnlessIdenticalAsync(
            finalIndexName,
            sql,
            SqlGenerator.GenerateCreateCompositeJsonIndexSql(
                tableName, finalIndexName, pathStrings, options, ifNotExists: false),
            cancellationToken).ConfigureAwait(false);

        if (!created)
        {
            _logger.LogDebug("Composite index {IndexName} already exists, skipping creation", finalIndexName);
        }
    }

    /// <inheritdoc cref="IDocumentOperations.AddVirtualColumnAsync{T}(Expression{Func{T, object}}, string, bool, string, CancellationToken)" />
    public Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex,
        string columnType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        return AddVirtualColumnAsync<T>(
            ExtractJsonPath(jsonPath, nameof(jsonPath)),
            columnName,
            createIndex,
            columnType,
            cancellationToken);
    }

    /// <inheritdoc cref="IDocumentOperations.AddVirtualColumnAsync{T}(string, string, bool, string, CancellationToken)" />
    public async Task AddVirtualColumnAsync<T>(
        string jsonPath,
        string columnName,
        bool createIndex,
        string columnType,
        CancellationToken cancellationToken)
    {
        ValidateJsonPathArgument(jsonPath);
        ValidateColumnName(columnName);

        var tableName = _tableNamingConvention.GetTableName<T>();

        // Validated here and not only in the generator: an existing column short-circuits past
        // the generator entirely, so the root, the column name and the column type would be
        // accepted or rejected by database state rather than by the argument. The generator's
        // own order is kept, so the fault reported first is the same on both branches.
        SqlGenerator.ValidateIdentifier(columnName, nameof(columnName));
        var pathString = SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false);
        SqlGenerator.ValidateColumnType(columnType);

        // The index name and its stored form are derived up front so the definition can be
        // checked before any DDL runs. The ALTER below commits immediately outside an ambient
        // transaction, so refusing the index afterwards would leave the generated column added
        // and the call failed — half applied, and not undoable by the caller catching it. The
        // preflight sits ahead of the column check rather than between it and the ALTER because
        // that makes "refuse before any work" true by construction instead of by the
        // short-circuit happening to skip the ALTER: the call now fails identically whether or
        // not the column is already there, and in both cases with nothing written.
        var indexName = createIndex ? $"idx_{tableName}_{columnName}" : null;
        var expectedIndexSql = indexName is null
            ? null
            : SqlGenerator.GenerateCreateColumnIndexSql(tableName, indexName, columnName, ifNotExists: false);

        if (indexName is not null)
        {
            await PreflightIndexDefinitionAsync(indexName, expectedIndexSql!, cancellationToken)
                .ConfigureAwait(false);
        }

        // Check if column already exists using SchemaIntrospector
        var introspector = new SchemaIntrospector(_connection);
        var columnExists = await introspector.ColumnExistsAsync(tableName, columnName, cancellationToken)
            .ConfigureAwait(false);

        if (columnExists)
        {
            _logger.LogDebug("Column {ColumnName} already exists in table {TableName}, skipping creation",
                columnName, tableName);
        }
        else
        {
            var addColumnSql = SqlGenerator.GenerateAddVirtualColumnSql(tableName, columnName, pathString, columnType);
            await _connection.ExecuteAsync(addColumnSql, cancellationToken).ConfigureAwait(false);
        }

        // Create index on the virtual column if requested. The preflight above already refused a
        // name held by a different definition; this repeats the check because another connection
        // can still claim it in between — a residual the store cannot close without wrapping the
        // whole operation in a transaction, and narrower than the pre-existing state the
        // preflight covers.
        if (indexName is not null)
        {
            var created = await CreateIndexUnlessIdenticalAsync(
                indexName,
                SqlGenerator.GenerateCreateColumnIndexSql(tableName, indexName, columnName),
                expectedIndexSql!,
                cancellationToken).ConfigureAwait(false);

            if (!created)
            {
                _logger.LogDebug("Index {IndexName} already exists, skipping creation", indexName);
            }
        }
    }

    /// <inheritdoc cref="IDocumentOperations.DropTableAsync{T}" />
    public async Task DropTableAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateDropTableSql(tableName);

        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.DropIndexAsync(string, CancellationToken)" />
    public async Task DropIndexAsync(string indexName, CancellationToken cancellationToken)
    {
        ValidateIndexName(indexName);

        var sql = SqlGenerator.GenerateDropIndexSql(indexName);
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.DropIndexAsync{T}" />
    public Task DropIndexAsync<T>(
        Expression<Func<T, object>> expression,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // Derived through the same two steps as CreateIndexAsync, so this drops exactly the
        // index that call creates for the same path. An explicitly named index has to go
        // through the string overload.
        var tableName = _tableNamingConvention.GetTableName<T>();
        var jsonPath = ExtractJsonPath(expression, nameof(expression));

        // Screened for the same reason CreateIndexAsync screens it, and reported against the
        // expression because that is the only parameter this overload has: the path grammar admits
        // a member a SQL identifier does not, so under a kebab-case naming policy "$.full-name"
        // derives "idx_T_full-name" and ValidateIdentifier would reject it against an indexName no
        // caller passed. Such an index cannot have been created by the expression overload either,
        // so there is nothing this refusal makes undroppable — the string overload names it.
        RequireDerivableName(tableName, jsonPath, nameof(expression));

        return DropIndexAsync(GenerateIndexName(tableName, jsonPath), cancellationToken);
    }

    /// <inheritdoc cref="IDocumentOperations.CreateBlobTableAsync" />
    public async Task CreateBlobTableAsync(CancellationToken cancellationToken)
    {
        var sql = SqlGenerator.GenerateCreateBlobTableSql();
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        await EnsureBlobMetadataColumnsAsync(cancellationToken).ConfigureAwait(false);

        // ALTER TABLE only appends, so an upgraded table keeps its payload column ahead of the
        // metadata — which is the slow layout, and only a rebuild can change it.
        if (await BlobTableNeedsRebuildAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "The {Table} table stores its payload column ahead of its metadata columns, so " +
                "reading blob metadata has to walk each payload's overflow pages. Call " +
                "RebuildBlobTableAsync to copy the table into the current layout.",
                SqlGenerator.BlobTableName);
        }
    }

    /// <summary>
    /// Adds the metadata columns to a blob table created before they existed.
    /// </summary>
    /// <remarks>
    /// The blob table is reserved and store-owned, so it is upgraded in place rather than through
    /// an <see cref="IMigration"/> a consumer would have to notice and register: the call that
    /// already creates the table is the natural place for it, and it is idempotent. Outside a
    /// caller's transaction the columns are added under <c>BEGIN IMMEDIATE</c> with the check
    /// repeated inside it, so two processes starting together cannot both issue the ALTER —
    /// the same shape the migration runner uses for its own legacy history
    /// table. Inside one, the caller's transaction already serializes it.
    /// </remarks>
    private async Task EnsureBlobMetadataColumnsAsync(CancellationToken cancellationToken)
    {
        var missing = await MissingBlobColumnsAsync(cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            if (_inAmbientTransaction)
            {
                await AddBlobColumnsAsync(missing, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // No catch: disposing an uncommitted transaction rolls it back, and an explicit
                // Rollback here would only be a second attempt at the same statement.
                using var transaction = _connection.BeginTransaction(
                    IsolationLevel.Serializable, deferred: false);
                missing = await MissingBlobColumnsAsync(cancellationToken).ConfigureAwait(false);
                await AddBlobColumnsAsync(missing, cancellationToken).ConfigureAwait(false);
                transaction.Commit();
            }
        }
    }

    private async Task<List<string>> MissingBlobColumnsAsync(CancellationToken cancellationToken)
    {
        var sql = SqlGenerator.GenerateBlobColumnExistsSql();
        var missing = new List<string>();

        foreach (var (name, _) in SqlGenerator.BlobMetadataColumns)
        {
            var count = await _connection.ExecuteScalarAsync<long>(sql, cancellationToken, ("Name", name))
                .ConfigureAwait(false);
            if (count == 0)
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    private async Task AddBlobColumnsAsync(List<string> columns, CancellationToken cancellationToken)
    {
        foreach (var column in columns)
        {
            _logger.LogInformation(
                "Upgrading the {Table} table: adding the {Column} column",
                SqlGenerator.BlobTableName, column);

            await _connection.ExecuteAsync(
                SqlGenerator.GenerateAddBlobColumnSql(column), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports whether the blob table carries its payload column ahead of the metadata.
    /// </summary>
    /// <remarks>
    /// Only meaningful once the metadata columns exist: a table that still has none of them ends
    /// in <c>data</c> too, and would read as current. Callers run
    /// <see cref="EnsureBlobMetadataColumnsAsync"/> first.
    /// </remarks>
    public async Task<bool> BlobTableNeedsRebuildAsync(CancellationToken cancellationToken)
    {
        var lastColumn = await LastBlobColumnAsync(cancellationToken).ConfigureAwait(false);

        // No columns at all means no table: nothing to rebuild.
        return lastColumn is not null && !string.Equals(lastColumn, "data", StringComparison.Ordinal);
    }

    private Task<string?> LastBlobColumnAsync(CancellationToken cancellationToken) =>
        _connection.QueryFirstStringAsync(
            SqlGenerator.GenerateBlobLastColumnSql(), cancellationToken);

    /// <summary>
    /// Copies the blob table into the layout <see cref="SqlGenerator.GenerateCreateBlobTableSql"/>
    /// produces, so metadata reads stop walking payload pages.
    /// </summary>
    /// <returns>False when the table already has that layout and nothing was copied</returns>
    public async Task<bool> RebuildBlobTableAsync(CancellationToken cancellationToken)
    {
        // No table at all: nothing to rebuild, and nothing to add columns to either.
        if (await LastBlobColumnAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        // A table that predates the metadata columns also ends in 'data', so the layout check
        // alone would call it current and this would return false while every metadata read
        // still failed with "no such column". Adding the columns first is idempotent and makes
        // the returned value mean what it says.
        await EnsureBlobMetadataColumnsAsync(cancellationToken).ConfigureAwait(false);

        if (!await BlobTableNeedsRebuildAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogInformation("Rebuilding the {Table} table into the current layout",
            SqlGenerator.BlobTableName);

        var steps = SqlGenerator.GenerateBlobTableRebuildSteps();

        if (_inAmbientTransaction)
        {
            foreach (var step in steps)
            {
                await _connection.ExecuteAsync(step, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        // See EnsureBlobMetadataColumnsAsync: disposal rolls back an uncommitted transaction.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        foreach (var step in steps)
        {
            await _connection.ExecuteAsync(step, cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    /// <inheritdoc cref="IDocumentOperations.PutBlobAsync(string, ReadOnlyMemory{byte}, CancellationToken)" />
    public async Task PutBlobAsync(
        string id,
        ReadOnlyMemory<byte> data,
        BlobWriteOptions? options,
        CancellationToken cancellationToken)
    {
        var payload = ValidateBlobWrite(id, data, options);

        await _connection.ExecuteAsync(
            SqlGenerator.GeneratePutBlobSql(), cancellationToken,
            ("Id", id), ("ContentType", options?.ContentType), ("Data", payload))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.PutBlobWithVersionAsync(string, ReadOnlyMemory{byte}, long, CancellationToken)" />
    public async Task<long> PutBlobWithVersionAsync(
        string id,
        ReadOnlyMemory<byte> data,
        long expectedVersion,
        BlobWriteOptions? options,
        CancellationToken cancellationToken)
    {
        var payload = ValidateBlobWrite(id, data, options);
        ValidateExpectedVersion(expectedVersion);

        var contentType = options?.ContentType;
        long? newVersion;

        if (expectedVersion == 0)
        {
            newVersion = await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateInsertBlobIfAbsentSql(), cancellationToken,
                ("Id", id), ("ContentType", contentType), ("Data", payload))
                .ConfigureAwait(false);

            // Same lift as a document: a row left at version 0 by raw SQL is matched by the
            // 0-guarded update and raised to 1 rather than being stuck outside the model.
            newVersion ??= await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateVersionedPutBlobSql(), cancellationToken,
                ("Id", id), ("ContentType", contentType), ("Data", payload), ("ExpectedVersion", 0L))
                .ConfigureAwait(false);
        }
        else
        {
            newVersion = await _connection.QueryFirstInt64Async(
                SqlGenerator.GenerateVersionedPutBlobSql(), cancellationToken,
                ("Id", id), ("ContentType", contentType), ("Data", payload),
                ("ExpectedVersion", expectedVersion))
                .ConfigureAwait(false);
        }

        if (newVersion is null)
        {
            throw await BuildConflictAsync(
                "writing", "blob", id, SqlGenerator.BlobTableName, expectedVersion,
                insertAttempt: expectedVersion == 0, cancellationToken).ConfigureAwait(false);
        }

        return newVersion.Value;
    }

    /// <summary>
    /// Validates a byte-array blob write and returns the array to bind.
    /// </summary>
    private static byte[] ValidateBlobWrite(string id, ReadOnlyMemory<byte> data, BlobWriteOptions? options)
    {
        ValidateBlobWriteArguments(id, data, options);

        // Bind the underlying array directly when the memory spans a whole array
        // to avoid copying potentially large payloads.
        return MemoryMarshal.TryGetArray(data, out var segment)
            && segment.Offset == 0
            && segment.Array is { } array
            && segment.Count == array.Length
                ? array
                : data.ToArray();
    }

    /// <inheritdoc cref="IDocumentOperations.GetBlobAsync" />
    public async Task<byte[]?> GetBlobAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var sql = SqlGenerator.GenerateGetBlobSql();

        // Row presence, not the payload, decides "not found": a row that exists and holds no
        // readable blob is corrupt, and returning null for it was indistinguishable from absent.
        var (storedTypeName, payload, found) = await _connection
            .QueryFirstBlobRowAsync(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);

        if (!found)
        {
            _logger.LogDebug("Blob {Id} not found", id);
            return null;
        }

        EnsureBlobPayload(storedTypeName, id);
        return payload;
    }

    /// <inheritdoc cref="IDocumentOperations.PutBlobAsync(string, Stream, long, CancellationToken)" />
    public async Task<long> PutBlobAsync(
        string id,
        Stream source,
        long length,
        BlobWriteOptions? options,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateBlobStreamArguments(id, source, length, options, expectedVersion);

        // Reserve and fill are two statements, and a failure between them would otherwise leave
        // the id holding zero bytes, destroying whatever it held before.
        if (_inAmbientTransaction)
        {
            return await PutBlobInSavepointAsync(id, source, length, options, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var transaction = await _connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var version = await PutBlobCoreAsync(id, source, length, options, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>
    /// Runs the write inside a savepoint, so a failure undoes it without touching the caller's
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Rolling the whole transaction back is not this method's call to make — the caller may have
    /// other work in it — but leaving the failure in place is not an option either: the reserve
    /// statement has already replaced the payload with zero bytes, so a caller who catches the
    /// exception and commits would persist a corrupt blob. The savepoint is the only construct
    /// that undoes just this write.
    /// <para>
    /// The statements divide by whether cancelling them can still be honest. The opening
    /// <c>SAVEPOINT</c> and the write itself take the caller's token: nothing is committed yet, so
    /// reporting cancellation there is true. The failure cleanup runs on
    /// <see cref="CancellationToken.None"/>, since the usual reason to be in the <c>catch</c> is
    /// that the caller's token was cancelled and the rollback must still happen. <strong>So does the
    /// successful release</strong>, which is the less obvious half: once every declared byte is written,
    /// the release is the point of no cancellation — the work is already in the caller's
    /// transaction and only the savepoint marker remains. Honouring the token there would report
    /// failure while leaving the write committable, so a caller who caught the exception and
    /// committed their other work would persist a write they were told had failed.
    /// </para>
    /// </remarks>
    private async Task<long> PutBlobInSavepointAsync(
        string id,
        Stream source,
        long length,
        BlobWriteOptions? options,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        // Generated, never caller-supplied, and validated by SqlGenerator like any identifier.
        // The prefix matters: a savepoint name may not start with a digit.
        var savepoint = $"blob_{Guid.NewGuid():N}";

        await _connection.ExecuteAsync(SqlGenerator.GenerateSavepointSql(savepoint), cancellationToken)
            .ConfigureAwait(false);

        long version;
        try
        {
            version = await PutBlobCoreAsync(id, source, length, options, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _connection.ExecuteAsync(
                    SqlGenerator.GenerateRollbackToSavepointSql(savepoint), CancellationToken.None)
                    .ConfigureAwait(false);
                await _connection.ExecuteAsync(
                    SqlGenerator.GenerateReleaseSavepointSql(savepoint), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                // The original failure is the one the caller needs; this one only explains why the
                // partial write may still be sitting in their transaction.
                _logger.LogWarning(
                    cleanupFailure,
                    "Failed to roll back the savepoint for blob {Id} after a failed streamed write",
                    id);
            }

            throw;
        }

        // CancellationToken.None, like the failure cleanup above: every declared byte is already
        // written, so this release is the point of no cancellation. Honouring the token here would
        // report failure to the caller while leaving the write sitting committable in their
        // transaction.
        await _connection.ExecuteAsync(SqlGenerator.GenerateReleaseSavepointSql(savepoint), CancellationToken.None)
            .ConfigureAwait(false);

        return version;
    }

    private async Task<long> PutBlobCoreAsync(
        string id,
        Stream source,
        long length,
        BlobWriteOptions? options,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var contentType = options?.ContentType;
        (long RowId, long Version)? reserved;

        if (expectedVersion is null)
        {
            reserved = await _connection.QueryFirstInt64PairAsync(
                SqlGenerator.GenerateReserveBlobSql(), cancellationToken,
                ("Id", id), ("ContentType", contentType), ("Len", length))
                .ConfigureAwait(false);

            if (reserved is null)
            {
                throw new InvalidOperationException($"Failed to reserve {length} bytes for blob '{id}'.");
            }
        }
        else
        {
            reserved = await ReserveVersionedBlobAsync(
                id, length, contentType, expectedVersion.Value, cancellationToken).ConfigureAwait(false);

            if (reserved is null)
            {
                throw await BuildConflictAsync(
                    "writing", "blob", id, SqlGenerator.BlobTableName, expectedVersion,
                    insertAttempt: expectedVersion == 0, cancellationToken).ConfigureAwait(false);
            }
        }

        await using var destination = new SqliteBlob(
            _connection, SqlGenerator.BlobTableName, "data", reserved.Value.RowId, readOnly: false);

        await CopyExactlyAsync(source, destination, length, cancellationToken).ConfigureAwait(false);

        return reserved.Value.Version;
    }

    /// <summary>
    /// Reserves the row for a version-guarded streamed write, mirroring the byte-array path:
    /// expected version 0 inserts only when the id is free, then falls back to the 0-guarded
    /// update that lifts a legacy row.
    /// </summary>
    private async Task<(long RowId, long Version)?> ReserveVersionedBlobAsync(
        string id,
        long length,
        string? contentType,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (expectedVersion != 0)
        {
            return await _connection.QueryFirstInt64PairAsync(
                SqlGenerator.GenerateVersionedReserveBlobSql(), cancellationToken,
                ("Id", id), ("ContentType", contentType), ("Len", length),
                ("ExpectedVersion", expectedVersion))
                .ConfigureAwait(false);
        }

        var inserted = await _connection.QueryFirstInt64PairAsync(
            SqlGenerator.GenerateReserveBlobIfAbsentSql(), cancellationToken,
            ("Id", id), ("ContentType", contentType), ("Len", length))
            .ConfigureAwait(false);

        return inserted ?? await _connection.QueryFirstInt64PairAsync(
            SqlGenerator.GenerateVersionedReserveBlobSql(), cancellationToken,
            ("Id", id), ("ContentType", contentType), ("Len", length), ("ExpectedVersion", 0L))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes exactly <paramref name="length"/> bytes from <paramref name="source"/>, failing
    /// if it ends first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain <see cref="Stream.CopyToAsync(Stream)"/> would keep reading until the source ended
    /// and run off the end of a blob that cannot grow, surfacing the provider's "size of a blob
    /// may not be changed" error rather than the caller's mistake. Bounding the copy also leaves
    /// a short source detectable, which a copy that stopped at EOF would not be.
    /// </para>
    /// <para>
    /// It deliberately does <em>not</em> read past <paramref name="length"/> to check for a
    /// longer source. On a live network stream or pipe that read blocks until the peer sends more
    /// or closes — indefinitely, and even for a zero-length blob — and it would swallow a byte
    /// belonging to whatever follows in a framed or concatenated stream. A source that can be
    /// measured is measured up front instead, by the caller.
    /// </para>
    /// </remarks>
    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(BlobCopyBufferSize, Math.Max(1, length)));

        try
        {
            long copied = 0;
            while (copied < length)
            {
                var wanted = (int)Math.Min(buffer.Length, length - copied);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"The source stream ended after {copied} bytes, but {length} were declared.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc cref="IDocumentOperations.BlobLengthAsync" />
    public async Task<long?> BlobLengthAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        // Row presence decides "not found" here too, and the storage class has to be checked
        // even though the payload is never read: length() counts characters on a TEXT value and
        // digits on a number, so a non-blob row reported a plausible byte count that was not one.
        var (storedTypeName, length, found) = await _connection
            .QueryFirstInt64RowAsync(SqlGenerator.GenerateBlobLengthSql(), cancellationToken, ("Id", id))
            .ConfigureAwait(false);

        if (!found)
        {
            _logger.LogDebug("Blob {Id} not found", id);
            return null;
        }

        EnsureBlobPayload(storedTypeName, id);
        return length;
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteBlobAsync" />
    public async Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var sql = SqlGenerator.GenerateDeleteBlobSql();
        var affectedRows = await _connection.ExecuteAsync(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
        return affectedRows > 0;
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteBlobWithVersionAsync" />
    public async Task DeleteBlobWithVersionAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        ValidateExpectedVersion(expectedVersion);

        var affectedRows = await _connection.ExecuteAsync(
            SqlGenerator.GenerateVersionedDeleteBlobSql(), cancellationToken,
            ("Id", id), ("ExpectedVersion", expectedVersion))
            .ConfigureAwait(false);

        if (affectedRows == 0)
        {
            // insertAttempt is false even at expected version 0: on a delete that means "the row
            // still sitting at 0", never "insert".
            throw await BuildConflictAsync(
                "deleting", "blob", id, SqlGenerator.BlobTableName, expectedVersion,
                insertAttempt: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="IDocumentOperations.GetBlobInfoAsync" />
    public async Task<BlobInfo?> GetBlobInfoAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var results = await ReadBlobInfosAsync(
            SqlGenerator.GenerateBlobInfoSql(), cancellationToken, ("Id", id)).ConfigureAwait(false);

        if (results.Count == 0)
        {
            _logger.LogDebug("Blob {Id} not found", id);
            return null;
        }

        return results[0];
    }

    /// <inheritdoc cref="IDocumentOperations.ListBlobsAsync" />
    public async Task<IReadOnlyList<BlobInfo>> ListBlobsAsync(
        string? idPrefix,
        int skip,
        int? take,
        CancellationToken cancellationToken)
    {
        ValidateListBlobsArguments(skip, take);

        var parameters = new List<(string, object?)>();
        var hasPrefix = !string.IsNullOrEmpty(idPrefix);
        var hasUpperBound = false;

        if (hasPrefix)
        {
            parameters.Add(("Prefix", idPrefix));
            if (BlobIdPrefix.TryGetUpperBound(idPrefix!, out var upperBound))
            {
                hasUpperBound = true;
                parameters.Add(("PrefixEnd", upperBound));
            }
        }

        if (take is not null)
        {
            parameters.Add(("Take", (long)take.Value));
        }

        if (skip > 0)
        {
            parameters.Add(("Skip", (long)skip));
        }

        var sql = SqlGenerator.GenerateListBlobsSql(hasPrefix, hasUpperBound, skip > 0, take is not null);
        return await ReadBlobInfosAsync(sql, cancellationToken, [.. parameters]).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>id, typeof(data), length(data), content_type, created_at, updated_at,
    /// version</c> rows.
    /// </summary>
    /// <remarks>
    /// The id travels with each row so a corrupt one can be named — the reason the listing
    /// projects it at all. A single unreadable row fails the whole listing rather than being
    /// skipped, matching <c>GetAllAsync</c>: returning fewer rows than the table holds is data
    /// loss the caller cannot detect.
    /// </remarks>
    private async Task<List<BlobInfo>> ReadBlobInfosAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<BlobInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            EnsureBlobPayload(reader.IsDBNull(1) ? null : reader.GetString(1), id);

            results.Add(new BlobInfo(
                id,
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetInt64(6)));
        }

        return results;
    }

    /// <inheritdoc cref="IDocumentOperations.BlobExistsAsync" />
    public async Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);

        var sql = SqlGenerator.GenerateBlobExistsSql();
        return await _connection.ExecuteScalarAsync<bool>(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.GetTableName{T}" />
    public string GetTableName<T>() => _tableNamingConvention.GetTableName<T>();

    /// <inheritdoc cref="IDocumentOperations.SerializeDocument{T}" />
    public byte[] SerializeDocument<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return JsonHelper.SerializeToUtf8Bytes(value, _serializerOptions);
    }

    /// <inheritdoc cref="IDocumentOperations.DeserializeDocument{T}" />
    public T? DeserializeDocument<T>(string? json) => JsonHelper.Deserialize<T>(json, _serializerOptions);

    /// <summary>
    /// Deserializes <c>(id, json(data))</c> rows to a list of typed objects.
    /// Uses a single-pass loop to avoid LINQ overhead and multiple enumerator allocations.
    /// </summary>
    /// <remarks>
    /// A row that deserializes to null throws rather than being dropped: silently returning
    /// fewer documents than the table holds is data loss the caller cannot detect.
    /// </remarks>
    private List<T> DeserializeResults<T>(List<(string? First, string? Second)> rows, string tableName)
    {
        var results = new List<T>(rows.Count);

        foreach (var (id, json) in rows)
        {
            EnsureDocumentPayload<T>(json, id, tableName);

            if (JsonHelper.Deserialize<T>(json, _serializerOptions) is not { } item)
            {
                throw NullDocument<T>(id, tableName);
            }

            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// Validates that a document row's <c>json(data)</c> projection carries a payload at all,
    /// throwing when it is SQL NULL, empty, or the JSON literal <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every document read funnels through here <em>before</em> deserializing, and the ordering
    /// is what makes the contract independent of <typeparamref name="T"/>.
    /// <c>JsonHelper</c> maps a null or empty projection to <c>default(T)</c> — for a value-type
    /// <typeparamref name="T"/> a real value, which the <c>is not { }</c> guard downstream
    /// happily matches — so checking only the deserialized document added a fabricated zero row
    /// to a collection read instead of reporting the corrupt one.
    /// </para>
    /// <para>
    /// The JSON literal <c>null</c> is rejected here for the same reason from the other side: a
    /// reference type deserializes it to null and was reported as corrupt, while a value type
    /// made <c>System.Text.Json</c> refuse the conversion outright, so the one row surfaced as
    /// two different exceptions depending on the type it was read as. Neither shape is reachable
    /// through a store write — <c>SerializeDocument</c> rejects a null document — so only raw
    /// SQL can produce one.
    /// </para>
    /// </remarks>
    private static void EnsureDocumentPayload<T>(string? json, string? id, string tableName)
    {
        if (string.IsNullOrEmpty(json) || string.Equals(json, "null", StringComparison.Ordinal))
        {
            throw NullDocument<T>(id, tableName);
        }
    }

    /// <summary>
    /// The exception for a document row that exists but reads back as nothing: a SQL NULL
    /// <c>data</c> column, an empty <c>json(data)</c> projection, or stored JSON that
    /// deserializes to null.
    /// </summary>
    private static CorruptDataException NullDocument<T>(string? id, string tableName) =>
        new($"Document '{id}' in table '{tableName}' has no readable payload as {typeof(T).Name}. " +
            "The stored data is SQL NULL, empty, or JSON null; fix or remove the row.",
            id,
            tableName,
            typeof(T));

    /// <summary>
    /// Validates that a blob row's <c>data</c> column holds a BLOB, throwing when it holds any
    /// other storage class.
    /// </summary>
    /// <remarks>
    /// Every blob read that consumes the payload — or reports its length, which
    /// <c>length()</c> answers for TEXT and numbers too — funnels through here, so the five
    /// read paths cannot drift apart again. An empty blob (<c>x''</c> or <c>zeroblob(0)</c>) is
    /// a BLOB and passes.
    /// </remarks>
    internal static void EnsureBlobPayload(string? storedTypeName, string id)
    {
        if (storedTypeName == SqliteStorageClass.Blob)
        {
            return;
        }

        throw new CorruptDataException(
            $"Blob '{id}' in table '{SqlGenerator.BlobTableName}' has no readable payload: its " +
            $"data column holds {storedTypeName ?? "an unknown storage class"}, not a blob. " +
            "Overwrite or remove the row.",
            id,
            SqlGenerator.BlobTableName,
            targetType: null,
            storedTypeName: storedTypeName);
    }

    /// <summary>
    /// Extracts the JSON path from a lambda expression.
    /// Supports simple property access (e.g., x => x.Email) and nested properties (e.g., x => x.Address.City).
    /// Every segment is resolved through the store's own <see cref="JsonSerializerOptions"/>, so the
    /// path names what the documents actually carry rather than the CLR member name.
    /// Only reads member names from the expression tree (no compilation or closure evaluation),
    /// so it is AOT/trim safe.
    /// </summary>
    private string ExtractJsonPath<T>(Expression<Func<T, object>> expression, string paramName) =>
        JsonPathResolver.Resolve(expression, _serializerOptions, paramName);

    /// <summary>
    /// Generates an index name from table name and JSON path.
    /// </summary>
    private static string GenerateIndexName(string tableName, string jsonPath)
    {
        // Remove special characters and convert to valid index name
        var pathPart = jsonPath.Replace("$.", "").Replace(".", "_");
        return $"idx_{tableName}_{pathPart}";
    }

    /// <summary>
    /// Creates an index unless one of that name already carries exactly this definition, and
    /// refuses the name when it is held by a different one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The look-up and the creation are two statements, so the name can be claimed between them.
    /// The executed statement keeps <c>IF NOT EXISTS</c> — two callers racing to create the
    /// <em>same</em> index should both succeed, and absorbing that is correct — but that same
    /// token makes a create against a <em>differently</em>-defined index of that name a silent
    /// no-op, which is the failure this whole guard exists to kill, reached by a race instead of
    /// by a same-process collision. So the stored definition is re-read after the create and
    /// compared again: an identical concurrent creation is absorbed, a conflicting one is
    /// refused. Dropping <c>IF NOT EXISTS</c> instead would make the identical race fail, which
    /// is the wrong trade.
    /// </para>
    /// <para>
    /// A name that is absent on the second read was created and then dropped again by another
    /// connection. Nothing claims it, so there is nothing to refuse.
    /// </para>
    /// </remarks>
    /// <param name="indexName">The index name</param>
    /// <param name="createSql">The statement to execute, <c>IF NOT EXISTS</c> included</param>
    /// <param name="expectedStoredSql">
    /// The same definition in the form SQLite stores, which is what both comparisons use
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when the create was issued, false when an identical index already existed</returns>
    private async Task<bool> CreateIndexUnlessIdenticalAsync(
        string indexName,
        string createSql,
        string expectedStoredSql,
        CancellationToken cancellationToken)
    {
        var existing = await _connection.QueryFirstStringRowAsync(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            cancellationToken,
            ("IndexName", indexName)).ConfigureAwait(false);

        if (existing.Found)
        {
            EnsureIndexDefinitionMatches(indexName, existing.Text, expectedStoredSql);
            return false;
        }

        await _connection.ExecuteAsync(createSql, cancellationToken).ConfigureAwait(false);

        var stored = await _connection.QueryFirstStringRowAsync(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            cancellationToken,
            ("IndexName", indexName)).ConfigureAwait(false);

        if (stored.Found)
        {
            EnsureIndexDefinitionMatches(indexName, stored.Text, expectedStoredSql);
        }

        return true;
    }

    /// <summary>
    /// Reads the definition an index of this name already carries, if any, and refuses the name
    /// when it is not the one being asked for.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="CreateIndexUnlessIdenticalAsync"/> so a caller that commits other
    /// DDL first can refuse <em>before</em> doing so rather than after.
    /// </remarks>
    private async Task PreflightIndexDefinitionAsync(
        string indexName,
        string expectedStoredSql,
        CancellationToken cancellationToken)
    {
        var existing = await _connection.QueryFirstStringRowAsync(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            cancellationToken,
            ("IndexName", indexName)).ConfigureAwait(false);

        if (existing.Found)
        {
            EnsureIndexDefinitionMatches(indexName, existing.Text, expectedStoredSql);
        }
    }

    /// <summary>
    /// Refuses to skip index creation when the index already sitting under that name is not the
    /// one the caller asked for.
    /// </summary>
    /// <remarks>
    /// The derived-name scheme is not injective — <c>$.A.B</c> and <c>$.A_B</c> flatten alike,
    /// a composite of <c>["$.A","$.B"]</c> collides with a single <c>$.A.B</c>, and a virtual
    /// column's <c>idx_{table}_{column}</c> collides with the expression index for the same
    /// member — so the <c>sqlite_master</c> pre-check would otherwise turn a collision into a
    /// silently skipped creation, leaving every query over the losing path on a table scan.
    /// Making the scheme injective would cost a name nobody can read in a SQL client, so the
    /// residual collision is made loud instead, the way
    /// <c>TableNameCollisionGuard</c> does for table names.
    /// </remarks>
    /// <param name="indexName">The index name both definitions claim</param>
    /// <param name="storedSql">
    /// The <c>CREATE INDEX</c> text SQLite has stored for it, or null for an index SQLite
    /// created itself
    /// </param>
    /// <param name="expectedSql">The stored form of the definition the caller asked for</param>
    private static void EnsureIndexDefinitionMatches(string indexName, string? storedSql, string expectedSql)
    {
        if (string.Equals(storedSql, expectedSql, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Index '{indexName}' already exists with a different definition. " +
            $"Existing: {storedSql ?? "<an internal index with no CREATE statement>"}. " +
            $"Requested: {expectedSql}. " +
            "Two different JSON paths can derive the same index name, and changing IndexOptions " +
            "does not change the name either; drop the existing index before creating this one.");
    }

    /// <summary>
    /// Refuses to derive a name from a path the derivation cannot express.
    /// </summary>
    /// <remarks>
    /// The flattening keeps a path's characters verbatim apart from its separators, so an
    /// indexer segment carries its brackets into the name and <c>ValidateIdentifier</c> rejects
    /// it — reported against the index name the caller never passed. Rewriting the brackets is
    /// not the fix: the scheme already maps distinct paths onto one name, and the
    /// <c>sqlite_master</c> pre-check turns a collision into a silently skipped creation, so
    /// widening it trades a loud error for a quiet one.
    ///
    /// The indexer is not the only shape: the member grammar admits any character but an
    /// apostrophe, a <c>.</c> and a <c>[</c>, which is wider than a SQL identifier, so a
    /// kebab-cased serialized name reaches the derivation too. An expression-derived path cannot
    /// carry an indexer but can carry such a member, which is why both shapes are screened here.
    /// </remarks>
    private static void RequireDerivableName(string tableName, string jsonPath, string paramName)
    {
        if (jsonPath.Contains('[', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"No index name can be derived from '{jsonPath}': a path with an array indexer needs " +
                "an explicit index name.",
                paramName);
        }

        // The path grammar admits any member character but an apostrophe, a '.' and a '[', which is
        // wider than a SQL identifier: under JsonNamingPolicy.KebabCaseLower, "$.full-name" derives
        // "idx_T_full-name", which ValidateIdentifier rejects against an indexName the caller never
        // passed - the exact mis-attribution this helper exists to prevent. Screened through
        // SqlGenerator so the identifier rule keeps one owner rather than being re-implemented here.
        //
        // The single-path derivation is the probe even for a composite index, because both
        // derivations apply the same transform to each path ("$." stripped, '.' folded to '_'), so a
        // character one rejects the other rejects too.
        if (!SqlGenerator.IsValidIdentifier(GenerateIndexName(tableName, jsonPath)))
        {
            throw new ArgumentException(
                $"No index name can be derived from '{jsonPath}': the derived name " +
                $"'{GenerateIndexName(tableName, jsonPath)}' is not a valid SQL identifier. Pass an " +
                "explicit index name.",
                paramName);
        }
    }

    /// <summary>
    /// Generates a composite index name from table name and multiple JSON paths.
    /// </summary>
    private static string GenerateCompositeIndexName(string tableName, IEnumerable<string> jsonPaths)
    {
        var pathsPart = string.Join("_", jsonPaths.Select(p => p.Replace("$.", "").Replace(".", "_")));
        return $"idx_{tableName}_composite_{pathsPart}";
    }
}
