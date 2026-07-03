using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Provides utilities for inspecting database schema information.
/// Create an instance with a connection to query table structures, indexes, and database statistics.
/// </summary>
public sealed class SchemaIntrospector
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// Initializes a new schema introspector with the specified connection.
    /// </summary>
    /// <param name="connection">The open SQLite connection</param>
    public SchemaIntrospector(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Gets information about all tables in the database.
    /// </summary>
    /// <returns>An enumerable of table information records</returns>
    public async Task<IEnumerable<TableInfo>> GetTablesAsync()
    {
        const string sql = @"
            SELECT name, type, sql
            FROM sqlite_master
            WHERE type = 'table'
            AND name NOT LIKE 'sqlite_%'
            ORDER BY name";

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var tables = new List<TableInfo>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            tables.Add(new TableInfo
            {
                Name = reader.GetString(0),
                Type = reader.GetString(1),
                Sql = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return tables;
    }

    /// <summary>
    /// Checks if a table exists in the database.
    /// </summary>
    /// <param name="tableName">The name of the table to check</param>
    /// <returns>True if the table exists, false otherwise</returns>
    public async Task<bool> TableExistsAsync(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        const string sql = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
            AND name = @TableName";

        var count = await _connection.ExecuteScalarAsync<int>(sql, ("TableName", tableName))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <summary>
    /// Gets information about all columns in a specific table.
    /// Uses PRAGMA table_xinfo to include generated/virtual columns.
    /// </summary>
    /// <param name="tableName">The name of the table</param>
    /// <returns>An enumerable of column information records</returns>
    public async Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        // Use table_xinfo instead of table_info to include generated columns.
        // PRAGMA arguments cannot be parameterized, so the identifier is interpolated. SQLite's
        // [ ] quoting has no escape for a literal ']', so use standard double-quote identifier
        // quoting and escape any embedded quote ("") to prevent breaking out of the identifier.
        var quotedTable = "\"" + tableName.Replace("\"", "\"\"") + "\"";
        var sql = $"PRAGMA table_xinfo({quotedTable})";

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        // Resolve column ordinals by name to be resilient to PRAGMA column ordering.
        var cidOrdinal = reader.GetOrdinal("cid");
        var nameOrdinal = reader.GetOrdinal("name");
        var typeOrdinal = reader.GetOrdinal("type");
        var notNullOrdinal = reader.GetOrdinal("notnull");
        var defaultOrdinal = reader.GetOrdinal("dflt_value");
        var pkOrdinal = reader.GetOrdinal("pk");
        var hiddenOrdinal = reader.GetOrdinal("hidden");

        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(new ColumnInfo
            {
                ColumnId = reader.GetInt64(cidOrdinal),
                Name = reader.GetString(nameOrdinal),
                Type = reader.GetString(typeOrdinal),
                NotNull = reader.GetInt64(notNullOrdinal) == 1,
                DefaultValue = reader.IsDBNull(defaultOrdinal) ? null : reader.GetValue(defaultOrdinal),
                IsPrimaryKey = reader.GetInt64(pkOrdinal) == 1,
                IsHidden = reader.GetInt64(hiddenOrdinal) != 0 // hidden=1 for virtual, hidden=2 for stored
            });
        }

        return columns;
    }

    /// <summary>
    /// Gets information about all indexes in the database or for a specific table.
    /// </summary>
    /// <param name="tableName">Optional table name to filter indexes</param>
    /// <returns>An enumerable of index information records</returns>
    public async Task<IEnumerable<IndexInfo>> GetIndexesAsync(string? tableName = null)
    {
        var sql = @"
            SELECT name, tbl_name, sql
            FROM sqlite_master
            WHERE type = 'index'
            AND name NOT LIKE 'sqlite_%'";

        var filterByTable = !string.IsNullOrEmpty(tableName);
        if (filterByTable)
        {
            sql += " AND tbl_name = @TableName";
        }

        sql += " ORDER BY name";

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (filterByTable)
        {
            command.Parameters.AddWithValue("@TableName", tableName);
        }

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var indexes = new List<IndexInfo>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            indexes.Add(new IndexInfo
            {
                Name = reader.GetString(0),
                TableName = reader.GetString(1),
                Sql = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return indexes;
    }

    /// <summary>
    /// Checks if an index exists in the database.
    /// </summary>
    /// <param name="indexName">The name of the index to check</param>
    /// <returns>True if the index exists, false otherwise</returns>
    public async Task<bool> IndexExistsAsync(string indexName)
    {
        ArgumentNullException.ThrowIfNull(indexName);

        const string sql = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
            AND name = @IndexName";

        var count = await _connection.ExecuteScalarAsync<int>(sql, ("IndexName", indexName))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <summary>
    /// Checks if a column exists in a specific table.
    /// </summary>
    /// <param name="tableName">The name of the table</param>
    /// <param name="columnName">The name of the column to check</param>
    /// <returns>True if the column exists, false otherwise</returns>
    public async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);

        var columns = await GetColumnsAsync(tableName).ConfigureAwait(false);
        return columns.Any(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the SQLite version being used.
    /// </summary>
    /// <returns>The SQLite version string</returns>
    public async Task<string> GetSqliteVersionAsync()
    {
        var version = await _connection.QueryFirstStringAsync("SELECT sqlite_version()").ConfigureAwait(false);
        return version ?? "Unknown";
    }

    /// <summary>
    /// Gets database statistics including page size, page count, and database size.
    /// </summary>
    /// <returns>Database statistics</returns>
    public async Task<DatabaseStatistics> GetDatabaseStatisticsAsync()
    {
        var pageCount = await _connection.ExecuteScalarAsync<long>("PRAGMA page_count").ConfigureAwait(false);
        var pageSize = await _connection.ExecuteScalarAsync<long>("PRAGMA page_size").ConfigureAwait(false);
        var freePages = await _connection.ExecuteScalarAsync<long>("PRAGMA freelist_count").ConfigureAwait(false);

        return new DatabaseStatistics
        {
            PageCount = pageCount,
            PageSize = pageSize,
            FreePages = freePages,
            DatabaseSizeBytes = pageCount * pageSize,
            UsedSizeBytes = (pageCount - freePages) * pageSize
        };
    }
}

/// <summary>
/// Contains information about a database table.
/// </summary>
public sealed class TableInfo
{
    /// <summary>
    /// Gets or sets the table name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the table type (typically "table").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CREATE TABLE SQL statement.
    /// </summary>
    public string? Sql { get; set; }
}

/// <summary>
/// Contains information about a table column.
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>
    /// Gets or sets the column ID (position in table).
    /// </summary>
    public long ColumnId { get; set; }

    /// <summary>
    /// Gets or sets the column name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the column type (e.g., TEXT, INTEGER, BLOB).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the column has a NOT NULL constraint.
    /// </summary>
    public bool NotNull { get; set; }

    /// <summary>
    /// Gets or sets the default value for the column, if any.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this column is part of the primary key.
    /// </summary>
    public bool IsPrimaryKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this column is hidden/generated.
    /// Virtual columns have hidden=1, stored generated columns have hidden=2.
    /// Regular columns have hidden=0.
    /// </summary>
    public bool IsHidden { get; set; }
}

/// <summary>
/// Contains information about a database index.
/// </summary>
public sealed class IndexInfo
{
    /// <summary>
    /// Gets or sets the index name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the table this index belongs to.
    /// </summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CREATE INDEX SQL statement.
    /// </summary>
    public string? Sql { get; set; }
}

/// <summary>
/// Contains database statistics information.
/// </summary>
public sealed class DatabaseStatistics
{
    /// <summary>
    /// Gets or sets the total number of pages in the database.
    /// </summary>
    public long PageCount { get; set; }

    /// <summary>
    /// Gets or sets the size of each page in bytes.
    /// </summary>
    public long PageSize { get; set; }

    /// <summary>
    /// Gets or sets the number of free pages.
    /// </summary>
    public long FreePages { get; set; }

    /// <summary>
    /// Gets or sets the total database size in bytes.
    /// </summary>
    public long DatabaseSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the used space in bytes.
    /// </summary>
    public long UsedSizeBytes { get; set; }
}
