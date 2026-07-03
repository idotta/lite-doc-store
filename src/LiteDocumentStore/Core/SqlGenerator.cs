using System.Text;

namespace LiteDocumentStore;

/// <summary>
/// Internal helper class for generating SQL statements. 
/// Extracted for testability and maintainability.
/// </summary>
internal static class SqlGenerator
{
    /// <summary>
    /// The reserved table name used for raw binary blob storage.
    /// </summary>
    public const string BlobTableName = "__store_blobs";

    /// <summary>
    /// Generates SQL for creating a table with JSONB storage.
    /// The version column backs optimistic concurrency: rows start at 1 and
    /// every write increments it.
    /// </summary>
    public static string GenerateCreateTableSql(string tableName)
    {
        return $@"
            CREATE TABLE IF NOT EXISTS [{tableName}] (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                version INTEGER NOT NULL DEFAULT 0
            )";
    }

    /// <summary>
    /// Generates SQL for upserting a document using JSONB format (last-writer-wins).
    /// Inserts start the version at 1; updates increment it so versions stay coherent
    /// with the optimistic-concurrency operations.
    /// </summary>
    public static string GenerateUpsertSql(string tableName)
    {
        return $@"
            INSERT INTO [{tableName}] (id, data, version)
            VALUES (@Id, jsonb(@Data), 1)
            ON CONFLICT(id) DO UPDATE SET
                data = jsonb(@Data),
                version = version + 1";
    }

    /// <summary>
    /// Generates SQL for an insert-only write used by optimistic concurrency with
    /// an expected version of 0 ("must not exist"). Affects 0 rows when the id
    /// already exists.
    /// </summary>
    public static string GenerateInsertIfAbsentSql(string tableName)
    {
        return $@"
            INSERT INTO [{tableName}] (id, data, version)
            VALUES (@Id, jsonb(@Data), 1)
            ON CONFLICT(id) DO NOTHING";
    }

    /// <summary>
    /// Generates SQL for a version-guarded update used by optimistic concurrency.
    /// Affects 0 rows when the id is missing or the stored version differs from
    /// the expected version.
    /// </summary>
    public static string GenerateVersionedUpdateSql(string tableName)
    {
        return $@"
            UPDATE [{tableName}] SET
                data = jsonb(@Data),
                version = version + 1
            WHERE id = @Id AND version = @ExpectedVersion";
    }

    /// <summary>
    /// Generates SQL for retrieving a document together with its version.
    /// </summary>
    public static string GenerateGetWithVersionSql(string tableName)
    {
        return $"SELECT json(data) as data, version FROM [{tableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for creating the shared blob table.
    /// </summary>
    public static string GenerateCreateBlobTableSql()
    {
        return $@"
            CREATE TABLE IF NOT EXISTS [{BlobTableName}] (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL
            )";
    }

    /// <summary>
    /// Generates SQL for upserting a raw binary blob.
    /// </summary>
    public static string GeneratePutBlobSql()
    {
        return $@"
            INSERT INTO [{BlobTableName}] (id, data)
            VALUES (@Id, @Data)
            ON CONFLICT(id) DO UPDATE SET
                data = @Data";
    }

    /// <summary>
    /// Generates SQL for retrieving a raw binary blob by ID.
    /// </summary>
    public static string GenerateGetBlobSql()
    {
        return $"SELECT data FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for deleting a raw binary blob by ID.
    /// </summary>
    public static string GenerateDeleteBlobSql()
    {
        return $"DELETE FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL to check if a raw binary blob exists by ID.
    /// </summary>
    public static string GenerateBlobExistsSql()
    {
        return $"SELECT EXISTS(SELECT 1 FROM [{BlobTableName}] WHERE id = @Id)";
    }

    /// <summary>
    /// Generates SQL for retrieving a document by ID.
    /// </summary>
    public static string GenerateGetByIdSql(string tableName)
    {
        return $"SELECT json(data) as data FROM [{tableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for retrieving all documents from a table.
    /// </summary>
    public static string GenerateGetAllSql(string tableName)
    {
        return $"SELECT json(data) as data FROM [{tableName}]";
    }

    /// <summary>
    /// Generates SQL for deleting a document by ID.
    /// </summary>
    public static string GenerateDeleteSql(string tableName)
    {
        return $"DELETE FROM [{tableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL to check if a document exists by ID.
    /// </summary>
    public static string GenerateExistsSql(string tableName)
    {
        return $"SELECT EXISTS(SELECT 1 FROM [{tableName}] WHERE id = @Id)";
    }

    /// <summary>
    /// Generates SQL to count all documents in a table.
    /// </summary>
    public static string GenerateCountSql(string tableName)
    {
        return $"SELECT COUNT(*) FROM [{tableName}]";
    }

    /// <summary>
    /// Generates SQL to check if an index exists.
    /// </summary>
    public static string GenerateCheckIndexExistsSql()
    {
        return "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@IndexName";
    }

    /// <summary>
    /// Generates SQL for creating an index on a JSON path.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="jsonPath">The JSON path to index (e.g., '$.email')</param>
    public static string GenerateCreateJsonIndexSql(string tableName, string indexName, string jsonPath)
    {
        return $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] (json_extract(data, '{jsonPath}'))";
    }

    /// <summary>
    /// Generates SQL for creating a composite index on multiple JSON paths.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="jsonPaths">The JSON paths to index</param>
    public static string GenerateCreateCompositeJsonIndexSql(string tableName, string indexName, IEnumerable<string> jsonPaths)
    {
        var extractClauses = string.Join(", ", jsonPaths.Select(p => $"json_extract(data, '{p}')"));
        return $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] ({extractClauses})";
    }

    /// <summary>
    /// Generates SQL for bulk upserting multiple documents using a single statement.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="count">The number of items to upsert</param>
    public static string GenerateBulkUpsertSql(string tableName, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentException("Count must be greater than zero.", nameof(count));
        }

        // Use StringBuilder to avoid O(n) string allocations
        // Estimated size: ~45 chars per value clause + ~130 chars for statement
        var sb = new StringBuilder(130 + (count * 45));
        sb.Append("INSERT INTO [").Append(tableName).Append("] (id, data, version) VALUES ");

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append("(@Id").Append(i).Append(", jsonb(@Data").Append(i).Append("), 1)");
        }

        sb.Append(" ON CONFLICT(id) DO UPDATE SET data = excluded.data, version = version + 1");
        return sb.ToString();
    }

    /// <summary>
    /// Generates SQL for bulk deleting multiple documents by their IDs using a single statement.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="count">The number of items to delete</param>
    public static string GenerateBulkDeleteSql(string tableName, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentException("Count must be greater than zero.", nameof(count));
        }

        // Use StringBuilder to avoid O(n) string allocations
        // Estimated size: ~6 chars per param + ~50 chars for statement
        var sb = new StringBuilder(50 + (count * 6));
        sb.Append("DELETE FROM [").Append(tableName).Append("] WHERE id IN (");

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append("@Id").Append(i);
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Generates SQL for querying documents by a JSON path and value.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="jsonPath">The JSON path to query (e.g., '$.email')</param>
    public static string GenerateQueryByJsonPathSql(string tableName, string jsonPath)
    {
        return $"SELECT json(data) as data FROM [{tableName}] WHERE json_extract(data, '{jsonPath}') = @Value";
    }

    /// <summary>
    /// Generates SQL for querying documents with a custom WHERE clause.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="whereClause">The WHERE clause (without the WHERE keyword)</param>
    public static string GenerateQueryWithWhereSql(string tableName, string whereClause)
    {
        return $"SELECT json(data) as data FROM [{tableName}] WHERE {whereClause}";
    }

    /// <summary>
    /// Generates SQL for selecting specific JSON fields using json_extract().
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="fieldSelections">Dictionary of field name to JSON path mappings</param>
    /// <returns>SQL SELECT statement with json_extract() for each field</returns>
    public static string GenerateSelectFieldsSql(string tableName, Dictionary<string, string> fieldSelections)
    {
        if (fieldSelections == null || fieldSelections.Count == 0)
        {
            throw new ArgumentException("At least one field selection is required.", nameof(fieldSelections));
        }

        var selectClauses = fieldSelections.Select(kvp =>
            $"json_extract(data, '{kvp.Value}') as {kvp.Key}");

        return $"SELECT {string.Join(", ", selectClauses)} FROM [{tableName}]";
    }

    /// <summary>
    /// Generates SQL for selecting specific JSON fields with a WHERE clause.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="fieldSelections">Dictionary of field name to JSON path mappings</param>
    /// <param name="whereClause">The WHERE clause (without the WHERE keyword)</param>
    /// <returns>SQL SELECT statement with json_extract() for each field and WHERE clause</returns>
    public static string GenerateSelectFieldsWithWhereSql(
        string tableName,
        Dictionary<string, string> fieldSelections,
        string whereClause)
    {
        if (fieldSelections == null || fieldSelections.Count == 0)
        {
            throw new ArgumentException("At least one field selection is required.", nameof(fieldSelections));
        }

        var selectClauses = fieldSelections.Select(kvp =>
            $"json_extract(data, '{kvp.Value}') as {kvp.Key}");

        return $"SELECT {string.Join(", ", selectClauses)} FROM [{tableName}] WHERE {whereClause}";
    }

    /// <summary>
    /// Generates SQL for adding a virtual (generated) column based on a JSON path expression.
    /// The column is generated from json_extract(data, '$.path') and stored as a VIRTUAL column.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="columnName">The name for the new virtual column</param>
    /// <param name="jsonPath">The JSON path expression (e.g., '$.email')</param>
    /// <param name="columnType">The SQLite column type for the virtual column (e.g., TEXT, INTEGER)</param>
    public static string GenerateAddVirtualColumnSql(
        string tableName,
        string columnName,
        string jsonPath,
        string columnType = "TEXT")
    {
        // VIRTUAL columns are computed on read and don't take up storage space
        // STORED columns are computed on write and stored, but take space
        // We use VIRTUAL as it's more storage-efficient for JSON extraction
        return $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {columnType} GENERATED ALWAYS AS (json_extract(data, '{jsonPath}')) VIRTUAL";
    }

    /// <summary>
    /// Generates SQL for creating an index on a virtual column.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="columnName">The column name to index</param>
    public static string GenerateCreateColumnIndexSql(string tableName, string indexName, string columnName)
    {
        return $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] ([{columnName}])";
    }
}
