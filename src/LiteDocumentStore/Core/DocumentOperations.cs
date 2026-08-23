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

    internal DocumentOperations(
        SqliteConnection connection,
        ITableNamingConvention tableNamingConvention,
        JsonSerializerOptions serializerOptions,
        ILogger logger)
    {
        _connection = connection;
        _tableNamingConvention = tableNamingConvention;
        _serializerOptions = serializerOptions;
        _logger = logger;
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
        var sql = SqlGenerator.GenerateBulkUpsertSql(tableName, itemsList.Count);

        var parameters = new (string, object?)[itemsList.Count * 2];
        for (int i = 0; i < itemsList.Count; i++)
        {
            // Validate all items
            if (string.IsNullOrWhiteSpace(itemsList[i].id))
            {
                throw new ArgumentException($"ID at index {i} cannot be null or empty.", nameof(items));
            }
            if (itemsList[i].data == null)
            {
                throw new ArgumentException($"Data at index {i} cannot be null.", nameof(items));
            }

            var (id, data) = itemsList[i];
            var jsonBytes = JsonHelper.SerializeToUtf8Bytes(data, _serializerOptions);
            parameters[i * 2] = ($"Id{i}", id);
            parameters[(i * 2) + 1] = ($"Data{i}", jsonBytes);
        }

        return await _connection.ExecuteAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
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

        int affectedRows;
        if (expectedVersion == 0)
        {
            var sql = SqlGenerator.GenerateInsertIfAbsentSql(tableName);
            affectedRows = await _connection.ExecuteAsync(
                sql, cancellationToken, ("Id", id), ("Data", jsonBytes))
                .ConfigureAwait(false);
        }
        else
        {
            var sql = SqlGenerator.GenerateVersionedUpdateSql(tableName);
            affectedRows = await _connection.ExecuteAsync(
                sql, cancellationToken, ("Id", id), ("Data", jsonBytes), ("ExpectedVersion", expectedVersion))
                .ConfigureAwait(false);
        }

        if (affectedRows == 0)
        {
            var reason = expectedVersion == 0
                ? "the document already exists"
                : $"the stored version does not match the expected version {expectedVersion} (or the document does not exist)";
            throw new ConcurrencyException(
                $"Concurrency conflict writing document '{id}' in table '{tableName}': {reason}.",
                id, tableName);
        }

        return expectedVersion + 1;
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
        return document is null ? null : new VersionedDocument<T>(document, version);
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

        return JsonHelper.Deserialize<T>(json, _serializerOptions);
    }

    /// <inheritdoc cref="IDocumentOperations.GetAllAsync{T}" />
    public async Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken)
    {
        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetAllSql(tableName);

        var jsonResults = await _connection.QueryStringsAsync(sql, cancellationToken).ConfigureAwait(false);
        return DeserializeResults<T>(jsonResults);
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

        // Validate all IDs
        for (int i = 0; i < idsList.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(idsList[i]))
            {
                throw new ArgumentException($"ID at index {i} cannot be null or empty.", nameof(ids));
            }
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateBulkDeleteSql(tableName, idsList.Count);

        var parameters = new (string, object?)[idsList.Count];
        for (int i = 0; i < idsList.Count; i++)
        {
            parameters[i] = ($"Id{i}", idsList[i]);
        }

        return await _connection.ExecuteAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.ExistsAsync{T}" />
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

        var jsonResults = await _connection.QueryStringsAsync(sql, cancellationToken, ("Value", bound))
            .ConfigureAwait(false);
        return DeserializeResults<T>(jsonResults);
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

        var jsonResults = await _connection
            .QueryStringsAsync(generated.Sql, cancellationToken, BindPositionally(generated))
            .ConfigureAwait(false);
        return DeserializeResults<T>(jsonResults);
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

    /// <summary>
    /// Names the generator's values <c>p0..pN</c>, matching the <c>@p0..@pN</c> placeholders it
    /// emitted in the same left-to-right pass.
    /// </summary>
    private static (string Name, object? Value)[] BindPositionally(GeneratedQuery generated)
    {
        var values = generated.ParameterValues;
        var parameters = new (string Name, object? Value)[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = ("p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        }

        return parameters;
    }

    /// <inheritdoc cref="IDocumentOperations.CreateIndexAsync{T}" />
    public async Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathString = ExtractJsonPath(jsonPath);
        var finalIndexName = indexName ?? GenerateIndexName(tableName, pathString);

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

        var sql = SqlGenerator.GenerateCreateJsonIndexSql(tableName, finalIndexName, pathString);
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.CreateCompositeIndexAsync{T}" />
    public async Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
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

        var sql = SqlGenerator.GenerateCreateCompositeJsonIndexSql(tableName, finalIndexName, pathStrings);
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

    /// <inheritdoc cref="IDocumentOperations.CreateBlobTableAsync" />
    public async Task CreateBlobTableAsync(CancellationToken cancellationToken)
    {
        var sql = SqlGenerator.GenerateCreateBlobTableSql();
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDocumentOperations.PutBlobAsync" />
    public async Task PutBlobAsync(string id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        // Bind the underlying array directly when the memory spans a whole array
        // to avoid copying potentially large payloads.
        var payload = MemoryMarshal.TryGetArray(data, out var segment)
            && segment.Offset == 0
            && segment.Array is { } array
            && segment.Count == array.Length
                ? array
                : data.ToArray();

        var sql = SqlGenerator.GeneratePutBlobSql();
        await _connection.ExecuteAsync(sql, cancellationToken, ("Id", id), ("Data", payload))
            .ConfigureAwait(false);
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
    /// Deserializes JSON results to a list of typed objects.
    /// Uses a single-pass loop to avoid LINQ overhead and multiple enumerator allocations.
    /// </summary>
    private List<T> DeserializeResults<T>(List<string?> jsonResults)
    {
        var results = new List<T>(jsonResults.Count);

        foreach (var json in jsonResults)
        {
            if (JsonHelper.Deserialize<T>(json, _serializerOptions) is { } item)
            {
                results.Add(item);
            }
        }

        return results;
    }

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
