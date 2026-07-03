using System.Data;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore;

/// <summary>
/// A high-performance document store for storing JSON objects in SQLite.
/// Uses raw ADO.NET (Microsoft.Data.Sqlite) with explicit parameter binding and JSONB
/// storage (SQLite 3.45+). Can optionally own and manage the lifecycle of its SqliteConnection.
/// </summary>
internal sealed class DocumentStore : IDocumentStore
{
    private readonly SqliteConnection _connection;
    private readonly ITableNamingConvention _tableNamingConvention;
    private readonly ILogger<DocumentStore> _logger;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly bool _ownsConnection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new document store with the specified connection and dependencies.
    /// </summary>
    /// <param name="connection">The open SQLite connection</param>
    /// <param name="tableNamingConvention">Table naming convention (defaults to DefaultTableNamingConvention)</param>
    /// <param name="logger">Logger for diagnostics (optional)</param>
    /// <param name="ownsConnection">Whether this store owns and should dispose the connection (default: false)</param>
    /// <param name="serializerOptions">
    /// JSON serializer options. For AOT, back these with a source-generated JsonSerializerContext.
    /// When null, a reflection-based fallback is used (non-AOT only).
    /// </param>
    public DocumentStore(
        SqliteConnection connection,
        ITableNamingConvention? tableNamingConvention = null,
        ILogger<DocumentStore>? logger = null,
        bool ownsConnection = false,
        JsonSerializerOptions? serializerOptions = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _tableNamingConvention = tableNamingConvention ?? new DefaultTableNamingConvention();
        _logger = logger ?? NullLogger<DocumentStore>.Instance;
        _serializerOptions = serializerOptions ?? JsonHelper.CreateDefaultReflectionOptions();
        _ownsConnection = ownsConnection;
    }

    /// <summary>
    /// Gets a value indicating whether this store owns and manages the connection lifecycle.
    /// </summary>
    public bool OwnsConnection => _ownsConnection;

    /// <summary>
    /// Gets the underlying SQLite connection for advanced operations.
    /// </summary>
    public SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection;
        }
    }

    /// <summary>
    /// Ensures the connection is in an open state before performing database operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the connection is not open.</exception>
    private void EnsureConnectionOpen()
    {
        if (_connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"Connection is not open. Current state: {_connection.State}. " +
                "Please ensure the connection is opened before using the DocumentStore.");
        }
    }

    /// <inheritdoc />
    public async Task CreateTableAsync<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateCreateTableSql(tableName);

        await _connection.ExecuteAsync(sql).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpsertAsync<T>(string id, T data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(data);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var jsonBytes = JsonHelper.SerializeToUtf8Bytes(data, _serializerOptions);
        var sql = SqlGenerator.GenerateUpsertSql(tableName);

        return await _connection.ExecuteAsync(sql, ("Id", id), ("Data", jsonBytes)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

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

        return await _connection.ExecuteAsync(sql, parameters).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> UpsertWithVersionAsync<T>(string id, T data, long expectedVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

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
            affectedRows = await _connection.ExecuteAsync(sql, ("Id", id), ("Data", jsonBytes))
                .ConfigureAwait(false);
        }
        else
        {
            var sql = SqlGenerator.GenerateVersionedUpdateSql(tableName);
            affectedRows = await _connection.ExecuteAsync(
                sql, ("Id", id), ("Data", jsonBytes), ("ExpectedVersion", expectedVersion))
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

    /// <inheritdoc />
    public async Task<VersionedDocument<T>?> GetWithVersionAsync<T>(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetWithVersionSql(tableName);

        var row = await _connection.QueryFirstStringInt64Async(sql, ("Id", id)).ConfigureAwait(false);
        if (row is not { Text: { Length: > 0 } json, Number: var version })
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return null;
        }

        var document = JsonHelper.Deserialize<T>(json, _serializerOptions);
        return document is null ? null : new VersionedDocument<T>(document, version);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetByIdSql(tableName);

        var json = await _connection.QueryFirstStringAsync(sql, ("Id", id)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(json))
        {
            _logger.LogDebug("Document {Id} not found in table {TableName}", id, tableName);
            return default;
        }

        return JsonHelper.Deserialize<T>(json, _serializerOptions);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> GetAllAsync<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateGetAllSql(tableName);

        var jsonResults = await _connection.QueryStringsAsync(sql).ConfigureAwait(false);
        return DeserializeResults<T>(jsonResults);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync<T>(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateDeleteSql(tableName);

        var affectedRows = await _connection.ExecuteAsync(sql, ("Id", id)).ConfigureAwait(false);
        var deleted = affectedRows > 0;

        if (!deleted)
        {
            _logger.LogDebug("Document {Id} not found in table {TableName} (nothing to delete)", id, tableName);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync<T>(IEnumerable<string> ids)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

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

        return await _connection.ExecuteAsync(sql, parameters).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync<T>(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateExistsSql(tableName);

        return await _connection.ExecuteScalarAsync<bool>(sql, ("Id", id)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateCountSql(tableName);

        return await _connection.ExecuteScalarAsync<long>(sql).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be null or empty.", nameof(jsonPath));
        }

        ArgumentNullException.ThrowIfNull(value);

        var tableName = _tableNamingConvention.GetTableName<T>();
        var sql = SqlGenerator.GenerateQueryByJsonPathSql(tableName, jsonPath);

        var jsonResults = await _connection.QueryStringsAsync(sql, ("Value", value)).ConfigureAwait(false);
        return DeserializeResults<T>(jsonResults);
    }

    /// <summary>
    /// Deserializes JSON results to a list of typed objects.
    /// Uses a single-pass loop to avoid LINQ overhead and multiple enumerator allocations.
    /// </summary>
    private List<T> DeserializeResults<T>(IReadOnlyCollection<string?> jsonResults)
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

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> action)
    {
        await ExecuteInTransactionCoreAsync(action).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(Func<Task> action)
    {
        await ExecuteInTransactionCoreAsync(_ => action()).ConfigureAwait(false);
    }

    /// <summary>
    /// Core transaction execution logic.
    /// </summary>
    private async Task ExecuteInTransactionCoreAsync(Func<IDbTransaction, Task> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        // BeginTransaction requires the connection to be open and throws if a transaction is
        // already active (SQLite supports one transaction per connection). Commands created via
        // SqliteConnection.CreateCommand automatically enlist in the active transaction, so the
        // document operations invoked inside the action participate without extra wiring.
        using var transaction = _connection.BeginTransaction();
        try
        {
            await action(transaction).ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync<T>(System.Linq.Expressions.Expression<Func<T, object>> jsonPath, string? indexName = null)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathString = ExtractJsonPath(jsonPath);
        var finalIndexName = indexName ?? GenerateIndexName(tableName, pathString);

        // Check if index already exists
        var indexExists = await _connection.ExecuteScalarAsync<int>(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            ("IndexName", finalIndexName)).ConfigureAwait(false);

        if (indexExists > 0)
        {
            _logger.LogDebug("Index {IndexName} already exists, skipping creation", finalIndexName);
            return;
        }

        var sql = SqlGenerator.GenerateCreateJsonIndexSql(tableName, finalIndexName, pathString);
        await _connection.ExecuteAsync(sql).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateCompositeIndexAsync<T>(System.Linq.Expressions.Expression<Func<T, object>>[] jsonPaths, string? indexName = null)
    {
        ArgumentNullException.ThrowIfNull(jsonPaths);
        if (jsonPaths.Length == 0)
        {
            throw new ArgumentException("At least one JSON path is required for composite index.", nameof(jsonPaths));
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathStrings = jsonPaths.Select(ExtractJsonPath).ToList();
        var finalIndexName = indexName ?? GenerateCompositeIndexName(tableName, pathStrings);

        // Check if index already exists
        var indexExists = await _connection.ExecuteScalarAsync<int>(
            SqlGenerator.GenerateCheckIndexExistsSql(),
            ("IndexName", finalIndexName)).ConfigureAwait(false);

        if (indexExists > 0)
        {
            _logger.LogDebug("Composite index {IndexName} already exists, skipping creation", finalIndexName);
            return;
        }

        var sql = SqlGenerator.GenerateCreateCompositeJsonIndexSql(tableName, finalIndexName, pathStrings);
        await _connection.ExecuteAsync(sql).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the JSON path from a lambda expression.
    /// Supports simple property access (e.g., x => x.Email) and nested properties (e.g., x => x.Address.City).
    /// Uses property names as-is to match the default System.Text.Json serialization (PascalCase).
    /// Only reads member names from the expression tree (no compilation or closure evaluation),
    /// so it is AOT/trim safe.
    /// </summary>
    private static string ExtractJsonPath<T>(System.Linq.Expressions.Expression<Func<T, object>> expression)
    {
        var body = expression.Body;

        // Handle convert expressions (when boxing value types to object)
        if (body is System.Linq.Expressions.UnaryExpression unary && unary.NodeType == System.Linq.Expressions.ExpressionType.Convert)
        {
            body = unary.Operand;
        }

        var members = new List<string>();
        var current = body;

        while (current is System.Linq.Expressions.MemberExpression memberExpr)
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

    /// <inheritdoc />
    public async Task AddVirtualColumnAsync<T>(
        System.Linq.Expressions.Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT")
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ArgumentException("Column name cannot be null or empty.", nameof(columnName));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var tableName = _tableNamingConvention.GetTableName<T>();
        var pathString = ExtractJsonPath(jsonPath);

        // Check if column already exists using SchemaIntrospector
        var introspector = new SchemaIntrospector(_connection);
        var columnExists = await introspector.ColumnExistsAsync(tableName, columnName).ConfigureAwait(false);

        if (columnExists)
        {
            _logger.LogDebug("Column {ColumnName} already exists in table {TableName}, skipping creation",
                columnName, tableName);
        }
        else
        {
            var addColumnSql = SqlGenerator.GenerateAddVirtualColumnSql(tableName, columnName, pathString, columnType);
            await _connection.ExecuteAsync(addColumnSql).ConfigureAwait(false);
        }

        // Create index on the virtual column if requested
        if (createIndex)
        {
            var indexName = $"idx_{tableName}_{columnName}";

            // Check if index already exists
            var indexExists = await _connection.ExecuteScalarAsync<int>(
                SqlGenerator.GenerateCheckIndexExistsSql(),
                ("IndexName", indexName)).ConfigureAwait(false);

            if (indexExists > 0)
            {
                _logger.LogDebug("Index {IndexName} already exists, skipping creation", indexName);
            }
            else
            {
                var createIndexSql = SqlGenerator.GenerateCreateColumnIndexSql(tableName, indexName, columnName);
                await _connection.ExecuteAsync(createIndexSql).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task CreateBlobTableAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        var sql = SqlGenerator.GenerateCreateBlobTableSql();
        await _connection.ExecuteAsync(sql).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PutBlobAsync(string id, ReadOnlyMemory<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

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
        await _connection.ExecuteAsync(sql, ("Id", id), ("Data", payload)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBlobAsync(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var sql = SqlGenerator.GenerateGetBlobSql();
        var payload = await _connection.ExecuteScalarAsync<byte[]>(sql, ("Id", id)).ConfigureAwait(false);

        if (payload is null)
        {
            _logger.LogDebug("Blob {Id} not found", id);
        }

        return payload;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteBlobAsync(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var sql = SqlGenerator.GenerateDeleteBlobSql();
        var affectedRows = await _connection.ExecuteAsync(sql, ("Id", id)).ConfigureAwait(false);
        return affectedRows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> BlobExistsAsync(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnectionOpen();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("ID cannot be null or empty.", nameof(id));
        }

        var sql = SqlGenerator.GenerateBlobExistsSql();
        return await _connection.ExecuteScalarAsync<bool>(sql, ("Id", id)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // Don't check _disposed here - we want to return false instead of throwing
            if (_disposed)
            {
                _logger.LogWarning("Health check failed: DocumentStore is disposed");
                return false;
            }

            // Check connection state
            if (_connection.State != ConnectionState.Open)
            {
                _logger.LogWarning("Health check failed: Connection is not open (state: {State})", _connection.State);
                return false;
            }

            // Verify SQLite version supports JSONB (3.45+)
            var versionString = await _connection.QueryFirstStringAsync(
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
            await _connection.ExecuteScalarAsync<long>("SELECT 1").ConfigureAwait(false);

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
    /// Disposes the document store and, if owned, the underlying connection.
    /// Performs a WAL checkpoint if the connection is owned and in WAL mode to ensure data durability.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsConnection)
        {
            await PerformWalCheckpointAsync().ConfigureAwait(false);
            _logger.LogDebug("Disposing owned connection");
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the document store and, if owned, the underlying connection.
    /// Performs a WAL checkpoint if the connection is owned and in WAL mode to ensure data durability.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsConnection)
        {
            PerformWalCheckpoint();
            _logger.LogDebug("Disposing owned connection");
            _connection.Dispose();
        }
    }

    /// <summary>
    /// Performs a WAL checkpoint to flush Write-Ahead Log to the database file for durability.
    /// Only executes if the connection is in a valid state and journal mode is WAL.
    /// </summary>
    private async Task PerformWalCheckpointAsync()
    {
        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                // Check if we're in WAL mode
                var journalMode = await _connection.QueryFirstStringAsync(
                    "PRAGMA journal_mode").ConfigureAwait(false);

                if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Executing WAL checkpoint before disposal");
                    // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                    await _connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)")
                        .ConfigureAwait(false);
                    _logger.LogInformation("WAL checkpoint completed successfully");
                }
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarning(ex, "Failed to perform WAL checkpoint during disposal");
        }
    }

    /// <summary>
    /// Performs a WAL checkpoint to flush Write-Ahead Log to the database file for durability (synchronous version).
    /// Only executes if the connection is in a valid state and journal mode is WAL.
    /// </summary>
    private void PerformWalCheckpoint()
    {
        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                // Check if we're in WAL mode
                var journalMode = _connection.QueryFirstString("PRAGMA journal_mode");

                if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Executing WAL checkpoint before disposal");
                    // PRAGMA wal_checkpoint(TRUNCATE) ensures all WAL frames are checkpointed and the WAL file is truncated
                    _connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                    _logger.LogInformation("WAL checkpoint completed successfully");
                }
            }
        }
        catch (Exception ex)
        {
            // Don't throw during disposal - log and continue
            _logger.LogWarning(ex, "Failed to perform WAL checkpoint during disposal");
        }
    }
}
