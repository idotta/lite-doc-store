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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

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
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetWithVersionSql(tableName);

        var row = await _connection.QueryFirstStringInt64Async(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);
        if (row is not { Text: { Length: > 0 } json, Number: var version })
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return null;
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetByIdSql(tableName);

        var json = await _connection.QueryFirstStringAsync(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(json))
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return default;
        }

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
                // A row that deserializes to null throws instead of being skipped, so a broken
                // row cannot masquerade as a missing document. The id cannot be null: an
                // 'id IN (...)' list never matches one.
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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be null or empty.", nameof(jsonPath));
        }

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
    public async Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathString = ExtractJsonPath(jsonPath);
        var finalIndexName = indexName ?? GenerateIndexName(tableName, pathString);

        // Generated before the pre-check so an invalid identifier, path or collation throws
        // whether or not the index happens to exist already: a bad argument is a bad argument.
        var sql = SqlGenerator.GenerateCreateJsonIndexSql(tableName, finalIndexName, pathString, options);

        // Check if index already exists
        var indexExists = await _connection.ExecuteScalarAsync<int>(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            cancellationToken,
            ("IndexName", finalIndexName)).ConfigureAwait(false);

        if (indexExists > 0)
        {
            _logger.LogDebug("Index {IndexName} already exists, skipping creation", finalIndexName);
            return;
        }

        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.CreateCompositeIndexAsync{T}(Expression{Func{T, object}}[], string, IndexOptions, CancellationToken)" />
    public async Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
        IndexOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPaths);
        if (jsonPaths.Length == 0)
        {
            throw new ArgumentException("At least one JSON path is required for composite index.", nameof(jsonPaths));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathStrings = jsonPaths.Select(ExtractJsonPath).ToList();
        var finalIndexName = indexName ?? GenerateCompositeIndexName(tableName, pathStrings);

        // Generated before the pre-check, for the reason in CreateIndexAsync.
        var sql = SqlGenerator.GenerateCreateCompositeJsonIndexSql(tableName, finalIndexName, pathStrings, options);

        // Check if index already exists
        var indexExists = await _connection.ExecuteScalarAsync<int>(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            cancellationToken,
            ("IndexName", finalIndexName)).ConfigureAwait(false);

        if (indexExists > 0)
        {
            _logger.LogDebug("Composite index {IndexName} already exists, skipping creation", finalIndexName);
            return;
        }

        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.AddVirtualColumnAsync{T}" />
    public async Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex,
        string columnType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ArgumentException("Column name cannot be null or empty.", nameof(columnName));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathString = ExtractJsonPath(jsonPath);

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

        // Create index on the virtual column if requested
        if (createIndex)
        {
            var indexName = $"idx_{tableName}_{columnName}";

            // Check if index already exists
            var indexExists = await _connection.ExecuteScalarAsync<int>(
                SqlGenerator.GenerateCheckIndexExistsSql(),
                cancellationToken,
                ("IndexName", indexName)).ConfigureAwait(false);

            if (indexExists > 0)
            {
                _logger.LogDebug("Index {IndexName} already exists, skipping creation", indexName);
            }
            else
            {
                var createIndexSql = SqlGenerator.GenerateCreateColumnIndexSql(tableName, indexName, columnName);
                await _connection.ExecuteAsync(createIndexSql, cancellationToken).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(indexName))
        {
            throw new ArgumentException("Index name cannot be null or empty.", nameof(indexName));
        }

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
        var indexName = GenerateIndexName(tableName, ExtractJsonPath(expression));

        return DropIndexAsync(indexName, cancellationToken);
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
                using var transaction = _connection.BeginTransaction(
                    IsolationLevel.Serializable, deferred: false);
                try
                {
                    missing = await MissingBlobColumnsAsync(cancellationToken).ConfigureAwait(false);
                    await AddBlobColumnsAsync(missing, cancellationToken).ConfigureAwait(false);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
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
            $"SELECT name FROM pragma_table_info('{SqlGenerator.BlobTableName}') ORDER BY cid DESC LIMIT 1",
            cancellationToken);

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

        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            foreach (var step in steps)
            {
                await _connection.ExecuteAsync(step, cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

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
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)data.Length, BlobLimits.MaxBlobLength, nameof(data));
        options?.Validate();

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var sql = SqlGenerator.GenerateGetBlobSql();
        var payload = await _connection.ExecuteScalarAsync<byte[]>(sql, cancellationToken, ("Id", id))
            .ConfigureAwait(false);

        if (payload is null)
        {
            _logger.LogDebug("Blob {Id} not found", id);
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
    /// that undoes just this write. Cleanup runs on <see cref="CancellationToken.None"/>, since
    /// the usual reason to be here is that the caller's token was cancelled.
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

        await _connection.ExecuteAsync(SqlGenerator.GenerateReleaseSavepointSql(savepoint), cancellationToken)
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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        return await _connection.QueryFirstInt64Async(
            SqlGenerator.GenerateBlobLengthSql(), cancellationToken, ("Id", id))
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.DeleteBlobAsync" />
    public async Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

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
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

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
    /// Reads <c>id, length(data), content_type, created_at, updated_at, version</c> rows.
    /// </summary>
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
            results.Add(new BlobInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetInt64(5)));
        }

        return results;
    }

    /// <inheritdoc cref="IDocumentOperations.BlobExistsAsync" />
    public async Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

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
            if (JsonHelper.Deserialize<T>(json, _serializerOptions) is not { } item)
            {
                throw NullDocument<T>(id, tableName);
            }

            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// The exception for a row that exists but whose stored JSON deserializes to null.
    /// </summary>
    private static SerializationException NullDocument<T>(string? id, string tableName) =>
        new($"Document '{id}' in table '{tableName}' deserialized to null as {typeof(T).Name}. " +
            "The stored JSON is null or empty; fix or remove the row.",
            typeof(T));

    /// <summary>
    /// Extracts the JSON path from a lambda expression.
    /// Supports simple property access (e.g., x => x.Email) and nested properties (e.g., x => x.Address.City).
    /// Uses property names as-is to match the default System.Text.Json serialization (PascalCase).
    /// Only reads member names from the expression tree (no compilation or closure evaluation),
    /// so it is AOT/trim safe.
    /// </summary>
    internal static string ExtractJsonPath<T>(Expression<Func<T, object>> expression)
    {
        var body = expression.Body;

        // Handle convert expressions (when boxing value types to object)
        if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            body = unary.Operand;
        }

        var members = new List<string>();
        var current = body;

        while (current is MemberExpression memberExpr)
        {
            members.Insert(0, memberExpr.Member.Name);
            current = memberExpr.Expression;
        }

        if (members.Count == 0)
        {
            throw new ArgumentException(
                "Expression must be a property access (e.g., x => x.Email or x => x.Address.City).",
                nameof(expression));
        }

        // Use property names as-is to match default System.Text.Json serialization (PascalCase)
        return "$." + string.Join(".", members);
    }

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
    /// Generates a composite index name from table name and multiple JSON paths.
    /// </summary>
    private static string GenerateCompositeIndexName(string tableName, IEnumerable<string> jsonPaths)
    {
        var pathsPart = string.Join("_", jsonPaths.Select(p => p.Replace("$.", "").Replace(".", "_")));
        return $"idx_{tableName}_composite_{pathsPart}";
    }
}
