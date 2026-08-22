using System.Text;

namespace LiteDocumentStore;

/// <summary>
/// Internal helper class for generating SQL statements.
/// Extracted for testability and maintainability.
/// </summary>
/// <remarks>
/// Values are always bound as parameters. Identifiers, JSON paths and column types cannot be,
/// so they are interpolated — and validated here, the only place that happens.
/// </remarks>
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
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT json(data) as data FROM [{tableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for retrieving all documents from a table.
    /// </summary>
    public static string GenerateGetAllSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT json(data) as data FROM [{tableName}]";
    }

    /// <summary>
    /// Generates SQL for deleting a document by ID.
    /// </summary>
    public static string GenerateDeleteSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"DELETE FROM [{tableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL to check if a document exists by ID.
    /// </summary>
    public static string GenerateExistsSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT EXISTS(SELECT 1 FROM [{tableName}] WHERE id = @Id)";
    }

    /// <summary>
    /// Generates SQL to count all documents in a table.
    /// </summary>
    public static string GenerateCountSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));
        ValidateJsonPath(jsonPath, nameof(jsonPath));

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
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));

        var extractClauses = string.Join(", ", jsonPaths.Select(p =>
            $"json_extract(data, '{ValidateJsonPath(p, nameof(jsonPaths))}')"));
        return $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] ({extractClauses})";
    }

    /// <summary>
    /// Generates SQL for bulk upserting multiple documents using a single statement.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="count">The number of items to upsert</param>
    public static string GenerateBulkUpsertSql(string tableName, int count)
    {
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));

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
    /// <remarks>
    /// Do not bind the path. SQLite matches an expression index only when the expression
    /// appears literally, so <c>json_extract(data, @Path)</c> would silently stop every index
    /// from <c>CreateIndexAsync</c> being used. It is validated instead.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="jsonPath">The JSON path to query (e.g., '$.email')</param>
    public static string GenerateQueryByJsonPathSql(string tableName, string jsonPath)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateJsonPath(jsonPath, nameof(jsonPath));

        return $"SELECT json(data) as data FROM [{tableName}] WHERE json_extract(data, '{jsonPath}') = @Value";
    }

    /// <summary>
    /// Generates SQL for querying documents with a custom WHERE clause.
    /// </summary>
    /// <remarks>
    /// Dead code, and <paramref name="whereClause"/> is an unvalidatable raw fragment — delete
    /// it rather than wire it up. <c>ExecuteRawAsync</c> is the escape hatch for predicates.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="whereClause">The WHERE clause (without the WHERE keyword)</param>
    public static string GenerateQueryWithWhereSql(string tableName, string whereClause)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT json(data) as data FROM [{tableName}] WHERE {whereClause}";
    }

    /// <summary>
    /// Generates SQL for selecting specific JSON fields using json_extract().
    /// </summary>
    /// <remarks>
    /// Dead code left from the projection APIs removed for AOT. Field names and paths are raw
    /// fragments here — delete it rather than wire it up.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="fieldSelections">Dictionary of field name to JSON path mappings</param>
    /// <returns>SQL SELECT statement with json_extract() for each field</returns>
    public static string GenerateSelectFieldsSql(string tableName, Dictionary<string, string> fieldSelections)
    {
        ValidateIdentifier(tableName, nameof(tableName));

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
    /// <remarks>Dead code. See <see cref="GenerateSelectFieldsSql"/>.</remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="fieldSelections">Dictionary of field name to JSON path mappings</param>
    /// <param name="whereClause">The WHERE clause (without the WHERE keyword)</param>
    /// <returns>SQL SELECT statement with json_extract() for each field and WHERE clause</returns>
    public static string GenerateSelectFieldsWithWhereSql(
        string tableName,
        Dictionary<string, string> fieldSelections,
        string whereClause)
    {
        ValidateIdentifier(tableName, nameof(tableName));

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
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(columnName, nameof(columnName));
        ValidateJsonPath(jsonPath, nameof(jsonPath));
        var validatedType = ValidateColumnType(columnType);

        // VIRTUAL columns are computed on read and don't take up storage space
        // STORED columns are computed on write and stored, but take space
        // We use VIRTUAL as it's more storage-efficient for JSON extraction
        return $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {validatedType} GENERATED ALWAYS AS (json_extract(data, '{jsonPath}')) VIRTUAL";
    }

    /// <summary>
    /// Generates SQL for creating an index on a virtual column.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="columnName">The column name to index</param>
    public static string GenerateCreateColumnIndexSql(string tableName, string indexName, string columnName)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));
        ValidateIdentifier(columnName, nameof(columnName));

        return $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] ([{columnName}])";
    }

    // Table, index and column names, restricted to [A-Za-z_][A-Za-z0-9_]*. Bracket quoting
    // alone is not enough: a ] in the name closes it early and the rest is parsed as SQL.
    // Returns the input so calls can be inlined into interpolation.
    private static string ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("A SQL identifier cannot be null or empty.", paramName);
        }

        if (!char.IsAsciiLetter(identifier[0]) && identifier[0] != '_')
        {
            throw new ArgumentException(
                $"Invalid SQL identifier '{identifier}': it must start with an ASCII letter or an underscore.",
                paramName);
        }

        for (var i = 1; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    $"Invalid SQL identifier '{identifier}': only ASCII letters, digits and underscores are supported.",
                    paramName);
            }
        }

        return identifier;
    }

    // Grammar: $(.member|[index])*  — a ' in the path would close the SQL literal it lands in.
    private static string ValidateJsonPath(string jsonPath, string paramName)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath[0] != '$')
        {
            throw new ArgumentException(
                $"Invalid JSON path '{jsonPath}': it must start with '$'.",
                paramName);
        }

        var i = 1;
        while (i < jsonPath.Length)
        {
            if (jsonPath[i] == '.')
            {
                i++;
                if (i >= jsonPath.Length || (!char.IsAsciiLetter(jsonPath[i]) && jsonPath[i] != '_'))
                {
                    throw new ArgumentException(
                        $"Invalid JSON path '{jsonPath}': a '.' must be followed by a member name starting with an ASCII letter or an underscore.",
                        paramName);
                }

                while (i < jsonPath.Length && (char.IsAsciiLetterOrDigit(jsonPath[i]) || jsonPath[i] == '_'))
                {
                    i++;
                }
            }
            else if (jsonPath[i] == '[')
            {
                i++;
                var digitStart = i;
                while (i < jsonPath.Length && char.IsAsciiDigit(jsonPath[i]))
                {
                    i++;
                }

                if (i == digitStart || i >= jsonPath.Length || jsonPath[i] != ']')
                {
                    throw new ArgumentException(
                        $"Invalid JSON path '{jsonPath}': an indexer must be a decimal number in brackets, for example '[0]'.",
                        paramName);
                }

                i++;
            }
            else
            {
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': only '.member' and '[index]' segments are supported.",
                    paramName);
            }
        }

        return jsonPath;
    }

    // The type lands unquoted in ALTER TABLE ... ADD COLUMN, so a whitelist is the only option.
    private static string ValidateColumnType(string columnType)
    {
        return columnType?.ToUpperInvariant() switch
        {
            "TEXT" => "TEXT",
            "INTEGER" => "INTEGER",
            "REAL" => "REAL",
            "BLOB" => "BLOB",
            "NUMERIC" => "NUMERIC",
            _ => throw new ArgumentException(
                $"Unsupported column type '{columnType}'. Supported types are TEXT, INTEGER, REAL, BLOB and NUMERIC.",
                nameof(columnType))
        };
    }
}
