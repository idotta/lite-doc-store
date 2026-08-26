using System.Globalization;
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
    /// The most bound parameters a generated statement may carry. SQLite's default
    /// SQLITE_MAX_VARIABLE_NUMBER is 999; a long <c>IN</c> list would otherwise fail at
    /// execution with an opaque error instead of at generation with a clear one.
    /// </summary>
    public const int MaxBoundParameters = 900;

    /// <summary>
    /// The most documents a single bulk upsert or delete statement may carry. Batch
    /// operations chunk to this size, keeping the bound parameter count (2N for an upsert)
    /// and the statement text well inside SQLITE_MAX_VARIABLE_NUMBER and
    /// SQLITE_MAX_SQL_LENGTH.
    /// </summary>
    public const int MaxBatchItemsPerStatement = 500;

    /// <summary>
    /// The most arguments a single SQL function call may take, matching SQLITE_MAX_FUNCTION_ARG
    /// in the bundled SQLite provider. It is a separate budget from
    /// <see cref="MaxBoundParameters"/> and a patch reaches it first: <c>jsonb_set</c> takes two
    /// arguments per set and <c>jsonb_remove</c> one per remove, on top of the document, while a
    /// remove binds no parameter at all.
    /// </summary>
    public const int MaxJsonFunctionArguments = 1000;

    /// <summary>
    /// The most paths one patch may set — <c>jsonb_set(data, path, value, ...)</c> spends two
    /// arguments per path on top of the document.
    /// </summary>
    public const int MaxPatchSetOperations = (MaxJsonFunctionArguments - 1) / 2;

    /// <summary>
    /// The most paths one patch may remove — <c>jsonb_remove(data, path, ...)</c> spends one
    /// argument per path on top of the document.
    /// </summary>
    public const int MaxPatchRemoveOperations = MaxJsonFunctionArguments - 1;

    /// <summary>
    /// Generates SQL for creating a table with JSONB storage.
    /// The version column backs optimistic concurrency: rows start at 1 and
    /// every write increments it. The column default is 1 as well, so a row inserted by raw
    /// SQL is CAS-able like any other.
    /// </summary>
    public static string GenerateCreateTableSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $@"
            CREATE TABLE IF NOT EXISTS [{tableName}] (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                version INTEGER NOT NULL DEFAULT 1
            )";
    }

    /// <summary>
    /// Generates SQL for dropping a document table, if it exists.
    /// </summary>
    /// <param name="tableName">The table name</param>
    public static string GenerateDropTableSql(string tableName) =>
        $"DROP TABLE IF EXISTS [{ValidateIdentifier(tableName, nameof(tableName))}]";

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
    /// an expected version of 0 ("must not exist"). Returns the stored version on
    /// insert and no row at all when the id already exists.
    /// </summary>
    public static string GenerateInsertIfAbsentSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $@"
            INSERT INTO [{tableName}] (id, data, version)
            VALUES (@Id, jsonb(@Data), 1)
            ON CONFLICT(id) DO NOTHING
            RETURNING version";
    }

    /// <summary>
    /// Generates SQL for a version-guarded update used by optimistic concurrency.
    /// Returns the new stored version, and no row at all when the id is missing or
    /// the stored version differs from the expected version.
    /// </summary>
    public static string GenerateVersionedUpdateSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $@"
            UPDATE [{tableName}] SET
                data = jsonb(@Data),
                version = version + 1
            WHERE id = @Id AND version = @ExpectedVersion
            RETURNING version";
    }

    /// <summary>
    /// Generates SQL for a version-guarded delete used by optimistic concurrency.
    /// Affects 0 rows when the id is missing or the stored version differs from
    /// the expected version.
    /// </summary>
    public static string GenerateVersionedDeleteSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"DELETE FROM [{tableName}] WHERE id = @Id AND version = @ExpectedVersion";
    }

    /// <summary>
    /// Generates SQL for reading just a document's version, used to report the stored version
    /// on a concurrency conflict.
    /// </summary>
    public static string GenerateGetVersionSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT version FROM [{tableName}] WHERE id = @Id";
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
    /// Generates SQL for retrieving the rowid of a blob by ID, which SQLite's incremental
    /// blob I/O addresses rows by.
    /// </summary>
    public static string GenerateBlobRowIdSql()
    {
        return $"SELECT rowid FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for retrieving the byte length of a blob by ID, without reading it.
    /// </summary>
    public static string GenerateBlobLengthSql()
    {
        return $"SELECT length(data) FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL that reserves a blob of exactly <c>@Len</c> zero bytes and returns its
    /// rowid, for a streamed write to fill through incremental blob I/O.
    /// </summary>
    /// <remarks>
    /// Incremental blob I/O cannot resize a blob, so the row has to be pre-sized with
    /// <c>zeroblob()</c> before the first byte is written. <c>RETURNING rowid</c> fires on the
    /// <c>DO UPDATE</c> branch as well as the insert, so an overwrite needs no second lookup
    /// (verified against SQLite 3.53.3).
    /// </remarks>
    public static string GenerateReserveBlobSql()
    {
        return $@"
            INSERT INTO [{BlobTableName}] (id, data)
            VALUES (@Id, zeroblob(@Len))
            ON CONFLICT(id) DO UPDATE SET
                data = zeroblob(@Len)
            RETURNING rowid";
    }

    /// <summary>
    /// Generates a <c>SAVEPOINT</c> statement, the nestable transaction the streamed blob write
    /// uses to undo itself inside a caller's transaction.
    /// </summary>
    public static string GenerateSavepointSql(string savepointName)
    {
        ValidateIdentifier(savepointName, nameof(savepointName));

        return $"SAVEPOINT [{savepointName}]";
    }

    /// <summary>
    /// Generates a <c>ROLLBACK TO</c> statement, undoing everything done since the savepoint
    /// without touching the enclosing transaction.
    /// </summary>
    public static string GenerateRollbackToSavepointSql(string savepointName)
    {
        ValidateIdentifier(savepointName, nameof(savepointName));

        return $"ROLLBACK TO [{savepointName}]";
    }

    /// <summary>
    /// Generates a <c>RELEASE</c> statement, discarding a savepoint. Needed after a
    /// <c>ROLLBACK TO</c> as well, which rewinds to the savepoint but does not pop it.
    /// </summary>
    public static string GenerateReleaseSavepointSql(string savepointName)
    {
        ValidateIdentifier(savepointName, nameof(savepointName));

        return $"RELEASE [{savepointName}]";
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
    /// Generates SQL for retrieving all documents from a table, each with its id.
    /// </summary>
    /// <remarks>
    /// The id travels with the document so a row that deserializes to null can be named in a
    /// <see cref="Exceptions.SerializationException"/> instead of being silently dropped.
    /// </remarks>
    public static string GenerateGetAllSql(string tableName)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        return $"SELECT id, json(data) as data FROM [{tableName}]";
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
    /// Generates SQL for deleting every document in a table.
    /// </summary>
    /// <param name="tableName">The table name</param>
    public static string GenerateDeleteAllSql(string tableName) =>
        $"DELETE FROM [{ValidateIdentifier(tableName, nameof(tableName))}]";

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
    /// <param name="options">Uniqueness, collation, direction and partial filter, or null for none</param>
    public static string GenerateCreateJsonIndexSql(
        string tableName,
        string indexName,
        string jsonPath,
        IndexOptions? options = null)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));
        ValidateJsonPath(jsonPath, nameof(jsonPath));

        return BuildCreateIndexSql(tableName, indexName, [jsonPath], options);
    }

    /// <summary>
    /// Generates SQL for dropping an index, if it exists.
    /// </summary>
    /// <param name="indexName">The index name</param>
    public static string GenerateDropIndexSql(string indexName) =>
        $"DROP INDEX IF EXISTS [{ValidateIdentifier(indexName, nameof(indexName))}]";

    /// <summary>
    /// Generates SQL for creating a composite index on multiple JSON paths.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="jsonPaths">The JSON paths to index</param>
    /// <param name="options">
    /// Uniqueness, collation, direction and partial filter, or null for none. Collation and
    /// direction apply to every indexed column.
    /// </param>
    public static string GenerateCreateCompositeJsonIndexSql(
        string tableName,
        string indexName,
        IEnumerable<string> jsonPaths,
        IndexOptions? options = null)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));

        var paths = jsonPaths.Select(p => ValidateJsonPath(p, nameof(jsonPaths))).ToList();
        return BuildCreateIndexSql(tableName, indexName, paths, options);
    }

    // Identifiers and paths arrive validated; the options carry two more interpolated pieces —
    // the collation name and the filter paths — so both are validated here, the one boundary
    // where that happens. Default options emit the statement these generators emitted before
    // the options existed: no UNIQUE, no COLLATE, no direction (SQLite's default is ascending)
    // and no WHERE.
    private static string BuildCreateIndexSql(
        string tableName,
        string indexName,
        IReadOnlyList<string> validatedPaths,
        IndexOptions? options)
    {
        var columnSuffix = new StringBuilder();
        if (options?.Collation is { } collation)
        {
            columnSuffix.Append(" COLLATE ").Append(ValidateIdentifier(collation, "options.Collation"));
        }

        if (options?.Descending == true)
        {
            columnSuffix.Append(" DESC");
        }

        var columns = string.Join(", ", validatedPaths.Select(p =>
            $"json_extract(data, '{p}'){columnSuffix}"));

        var sb = new StringBuilder("CREATE ");
        if (options?.Unique == true)
        {
            sb.Append("UNIQUE ");
        }

        sb.Append("INDEX IF NOT EXISTS [").Append(indexName)
          .Append("] ON [").Append(tableName).Append("] (").Append(columns).Append(')');

        if (options?.Filter is { } filter)
        {
            sb.Append(" WHERE ");
            for (var i = 0; i < filter.Terms.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" AND ");
                }

                var term = filter.Terms[i];
                sb.Append("json_extract(data, '")
                  .Append(ValidateJsonPath(term.JsonPath, "options.Filter"))
                  .Append("') IS ")
                  .Append(term.RequiresNull ? "NULL" : "NOT NULL");
            }
        }

        return sb.ToString();
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

        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxBatchItemsPerStatement);

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

        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxBatchItemsPerStatement);

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
    /// Generates SQL for retrieving multiple documents by their IDs using a single statement.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="count">The number of items to retrieve</param>
    public static string GenerateBulkGetSql(string tableName, int count)
    {
        ValidateIdentifier(tableName, nameof(tableName));

        if (count <= 0)
        {
            throw new ArgumentException("Count must be greater than zero.", nameof(count));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxBatchItemsPerStatement);

        // Use StringBuilder to avoid O(n) string allocations
        // Estimated size: ~6 chars per param + ~70 chars for statement
        var sb = new StringBuilder(70 + (count * 6));
        sb.Append("SELECT id, json(data) as data FROM [").Append(tableName).Append("] WHERE id IN (");

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

        return $"SELECT id, json(data) as data FROM [{tableName}] WHERE json_extract(data, '{jsonPath}') = @Value";
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

    /// <summary>
    /// Generates the SELECT for a structured <see cref="DocumentQuery{T}"/>: the document
    /// projection, the <c>AND</c>-combined predicates, the orderings and the limit/offset.
    /// </summary>
    /// <remarks>
    /// Takes structured predicates, never a caller-supplied SQL fragment. Values are bound as
    /// <c>@p0..@pN</c> and returned alongside the SQL, assigned in one left-to-right pass so
    /// the statement and the parameter order cannot drift apart.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="predicates">The filters to combine with <c>AND</c></param>
    /// <param name="orderings">The <c>ORDER BY</c> terms, in order</param>
    /// <param name="skip">The <c>OFFSET</c>, or null for none</param>
    /// <param name="take">The <c>LIMIT</c>, or null for none</param>
    public static GeneratedQuery GenerateQuerySql(
        string tableName,
        IReadOnlyList<QueryPredicate> predicates,
        IReadOnlyList<QueryOrdering> orderings,
        int? skip,
        int? take)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(orderings);

        var sb = new StringBuilder(128);
        sb.Append("SELECT id, json(data) as data FROM [").Append(tableName).Append(']');

        var values = AppendWhere(sb, predicates);
        AppendOrderBy(sb, orderings);
        AppendLimitOffset(sb, skip, take);

        return new GeneratedQuery(sb.ToString(), values);
    }

    /// <summary>
    /// Generates the row count for a structured <see cref="DocumentQuery{T}"/>'s predicates.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="GenerateQuerySql"/>: structured input only, values bound as
    /// <c>@p0..@pN</c> and returned with the SQL.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="predicates">The filters to combine with <c>AND</c></param>
    public static GeneratedQuery GenerateFilteredCountSql(
        string tableName,
        IReadOnlyList<QueryPredicate> predicates)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(predicates);

        var sb = new StringBuilder(64);
        sb.Append("SELECT COUNT(*) FROM [").Append(tableName).Append(']');

        var values = AppendWhere(sb, predicates);
        return new GeneratedQuery(sb.ToString(), values);
    }

    /// <summary>
    /// Generates the existence test for a structured <see cref="DocumentQuery{T}"/>'s predicates.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="GenerateFilteredCountSql"/> — structured input only, values
    /// bound as <c>@p0..@pN</c> and returned with the SQL — but the statement stops at the first
    /// match, so a large matching set costs no more than a small one.
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="predicates">The filters to combine with <c>AND</c></param>
    public static GeneratedQuery GenerateFilteredExistsSql(
        string tableName,
        IReadOnlyList<QueryPredicate> predicates)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(predicates);

        var sb = new StringBuilder(80);
        sb.Append("SELECT EXISTS(SELECT 1 FROM [").Append(tableName).Append(']');

        var values = AppendWhere(sb, predicates);
        sb.Append(" LIMIT 1)");

        return new GeneratedQuery(sb.ToString(), values);
    }

    /// <summary>
    /// Generates the field-level update for a <see cref="DocumentPatch{T}"/>, bumping the
    /// version and returning the version SQLite stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>jsonb_set</c> / <c>jsonb_remove</c>, never their <c>json_*</c> siblings: those return
    /// JSON <i>text</i>, which would silently de-binary the <c>data</c> column and break the
    /// JSONB contract every read (<c>SELECT json(data)</c>) depends on.
    /// </para>
    /// <para>
    /// Sets are applied before removes, since removes wrap the set expression. Paths are
    /// interpolated after validation — the same reason as everywhere else here, that SQLite
    /// matches an expression index only against a literal expression — and values are bound
    /// <c>@p0..@pN</c> in one left-to-right pass with the SQL.
    /// </para>
    /// <para>
    /// Within each function SQLite applies the paths sequentially left to right, each one seeing
    /// the document as the previous ones left it. The builder rejects only an exactly repeated
    /// path, so <i>related</i> paths still compose in call order: removing <c>$.Items[0]</c>
    /// before <c>$.Items[1]</c> shifts the array under the second path, and setting <c>$.A</c>
    /// before <c>$.A.B</c> writes into the value the first set just installed.
    /// </para>
    /// <para>
    /// The operation counts are capped by <see cref="MaxPatchSetOperations"/> and
    /// <see cref="MaxPatchRemoveOperations"/>, which come from SQLITE_MAX_FUNCTION_ARG rather
    /// than from <see cref="MaxBoundParameters"/> — a patch reaches the argument budget first,
    /// and a remove binds no parameter at all, so the parameter cap cannot be reached from here.
    /// </para>
    /// </remarks>
    /// <param name="tableName">The table name</param>
    /// <param name="operations">The changes to apply; at least one</param>
    /// <param name="versioned">
    /// True to guard the update with <c>AND version = @ExpectedVersion</c> for a compare-and-swap
    /// patch; false to patch whichever version is stored
    /// </param>
    public static GeneratedQuery GeneratePatchSql(
        string tableName,
        IReadOnlyList<PatchOperation> operations,
        bool versioned)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            throw new ArgumentException("A patch needs at least one operation.", nameof(operations));
        }

        var values = new List<object?>();
        var sb = new StringBuilder(160);
        sb.Append("UPDATE [").Append(tableName).Append("] SET data = ");

        AppendPatchExpression(sb, operations, values);

        sb.Append(", version = version + 1 WHERE id = @Id");
        if (versioned)
        {
            sb.Append(" AND version = @ExpectedVersion");
        }

        sb.Append(" RETURNING version");

        return new GeneratedQuery(sb.ToString(), values);
    }

    // jsonb_remove(jsonb_set(data, '$.A', @p0, '$.B', json(@p1)), '$.C') — either call is
    // elided when the patch has no operation of that kind.
    private static void AppendPatchExpression(
        StringBuilder sb,
        IReadOnlyList<PatchOperation> operations,
        List<object?> values)
    {
        var setCount = 0;
        var removeCount = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            switch (operations[i].Kind)
            {
                case PatchOperationKind.Set:
                    setCount++;
                    break;
                case PatchOperationKind.Remove:
                    removeCount++;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported patch operation '{operations[i].Kind}'.", nameof(operations));
            }
        }

        // SQLITE_MAX_FUNCTION_ARG, not SQLITE_MAX_VARIABLE_NUMBER, is what a patch runs into
        // first: a set binds one parameter but spends two function arguments, and a remove
        // binds none at all. Both would otherwise fail at execution with SQLite's opaque
        // "too many arguments on function jsonb_set".
        if (setCount > MaxPatchSetOperations)
        {
            throw new ArgumentException(
                $"The patch sets {setCount} paths, more than the supported maximum of " +
                $"{MaxPatchSetOperations}. Split it into several patches.",
                nameof(operations));
        }

        if (removeCount > MaxPatchRemoveOperations)
        {
            throw new ArgumentException(
                $"The patch removes {removeCount} paths, more than the supported maximum of " +
                $"{MaxPatchRemoveOperations}. Split it into several patches.",
                nameof(operations));
        }

        var hasSets = setCount > 0;
        var hasRemoves = removeCount > 0;

        if (hasRemoves)
        {
            sb.Append("jsonb_remove(");
        }

        if (hasSets)
        {
            sb.Append("jsonb_set(");
        }

        sb.Append("data");

        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation.Kind != PatchOperationKind.Set)
            {
                continue;
            }

            sb.Append(", '").Append(ValidateJsonPath(operation.JsonPath, nameof(operations))).Append("', ");

            var parameter = NextParameter(values, operation.Value);
            if (operation.AsJson)
            {
                sb.Append("json(").Append(parameter).Append(')');
            }
            else
            {
                sb.Append(parameter);
            }
        }

        if (hasSets)
        {
            sb.Append(')');
        }

        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Kind == PatchOperationKind.Remove)
            {
                sb.Append(", '")
                    .Append(ValidateJsonPath(operations[i].JsonPath, nameof(operations)))
                    .Append('\'');
            }
        }

        if (hasRemoves)
        {
            sb.Append(')');
        }
    }

    private static List<object?> AppendWhere(StringBuilder sb, IReadOnlyList<QueryPredicate> predicates)
    {
        var values = new List<object?>();
        if (predicates.Count == 0)
        {
            return values;
        }

        sb.Append(" WHERE ");
        for (var i = 0; i < predicates.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" AND ");
            }

            AppendPredicate(sb, predicates[i], values);
        }

        if (values.Count > MaxBoundParameters)
        {
            throw new ArgumentException(
                $"The query binds {values.Count} parameters, more than the supported maximum of " +
                $"{MaxBoundParameters}. Split it, or narrow the 'In' lists.",
                nameof(predicates));
        }

        return values;
    }

    private static void AppendPredicate(StringBuilder sb, QueryPredicate predicate, List<object?> values)
    {
        var path = ValidateJsonPath(predicate.JsonPath, nameof(predicate));

        switch (predicate.Operator)
        {
            case QueryOperator.IsNull:
                AppendExtract(sb, path).Append(" IS NULL");
                break;

            case QueryOperator.IsNotNull:
                AppendExtract(sb, path).Append(" IS NOT NULL");
                break;

            case QueryOperator.In:
                AppendInList(sb, path, predicate.Values, values);
                break;

            case QueryOperator.ArrayContains:
                sb.Append("EXISTS (SELECT 1 FROM json_each(data, '").Append(path)
                    .Append("') WHERE value = ").Append(NextParameter(values, predicate.Value)).Append(')');
                break;

            default:
                AppendExtract(sb, path).Append(' ').Append(ToSqlOperator(predicate.Operator)).Append(' ')
                    .Append(NextParameter(values, predicate.Value));
                break;
        }
    }

    private static void AppendInList(
        StringBuilder sb,
        string jsonPath,
        IReadOnlyList<object?> inValues,
        List<object?> values)
    {
        if (inValues.Count == 0)
        {
            throw new ArgumentException(
                $"The 'In' predicate on '{jsonPath}' has no values.",
                nameof(inValues));
        }

        AppendExtract(sb, jsonPath).Append(" IN (");
        for (var i = 0; i < inValues.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(NextParameter(values, inValues[i]));
        }

        sb.Append(')');
    }

    private static void AppendOrderBy(StringBuilder sb, IReadOnlyList<QueryOrdering> orderings)
    {
        if (orderings.Count == 0)
        {
            return;
        }

        sb.Append(" ORDER BY ");
        for (var i = 0; i < orderings.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            AppendExtract(sb, ValidateJsonPath(orderings[i].JsonPath, nameof(orderings)))
                .Append(orderings[i].Descending ? " DESC" : " ASC");
        }
    }

    // SQLite only accepts OFFSET after a LIMIT, so a skip without a take emits LIMIT -1
    // ("no limit").
    private static void AppendLimitOffset(StringBuilder sb, int? skip, int? take)
    {
        if (take.HasValue)
        {
            sb.Append(" LIMIT ").Append(take.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (skip.HasValue)
        {
            sb.Append(" LIMIT -1");
        }

        if (skip.HasValue)
        {
            sb.Append(" OFFSET ").Append(skip.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    // Do not bind the path — see GenerateQueryByJsonPathSql: SQLite matches an expression
    // index only when the indexed expression appears literally. It is validated instead.
    private static StringBuilder AppendExtract(StringBuilder sb, string jsonPath) =>
        sb.Append("json_extract(data, '").Append(jsonPath).Append("')");

    private static string NextParameter(List<object?> values, object? value)
    {
        var index = values.Count;
        values.Add(value);
        return "@p" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToSqlOperator(QueryOperator op) => op switch
    {
        QueryOperator.Equal => "=",
        QueryOperator.NotEqual => "<>",
        QueryOperator.GreaterThan => ">",
        QueryOperator.GreaterThanOrEqual => ">=",
        QueryOperator.LessThan => "<",
        QueryOperator.LessThanOrEqual => "<=",
        QueryOperator.Like => "LIKE",
        QueryOperator.Glob => "GLOB",
        _ => throw new ArgumentException($"Unsupported query operator '{op}'.", nameof(op))
    };

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
    internal static string ValidateJsonPath(string jsonPath, string paramName)
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
