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
    /// The current time in Unix milliseconds, as SQLite computes it for blob timestamps.
    /// </summary>
    /// <remarks>
    /// Taken from the database rather than bound from the client, so every writer against one
    /// file stamps rows off the same clock. <c>unixepoch('subsec')</c> yields fractional seconds
    /// (SQLite 3.42+; the store requires 3.45+ for <c>jsonb</c> anyway), so the multiplication
    /// keeps millisecond resolution instead of truncating to whole seconds.
    /// </remarks>
    private const string NowMillis = "CAST(unixepoch('subsec') * 1000 AS INTEGER)";

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
    /// <remarks>
    /// <para>
    /// <c>data</c> is deliberately the <em>last</em> column. SQLite stores a row as one record and
    /// reads it front to back, so a column sitting after a multi-megabyte payload can only be
    /// reached by walking that payload's overflow pages. Measured against SQLite 3.53.3 over
    /// twenty 20 MB rows: listing the metadata took 232 ms per pass with <c>data</c> second and
    /// under a millisecond with it last, and a single-row metadata read 14 ms against 0.02 ms.
    /// <c>length(data)</c> is unaffected either way — it is answered from the record header
    /// (<c>OP_Column</c> carries <c>OPFLAG_LENGTHARG</c>), which is why no length column is
    /// stored.
    /// </para>
    /// <para>
    /// A table created before the metadata columns existed cannot reach this layout through
    /// <c>ALTER TABLE ADD COLUMN</c>, which only appends: see
    /// <see cref="GenerateBlobTableRebuildSteps"/>.
    /// </para>
    /// </remarks>
    public static string GenerateCreateBlobTableSql()
    {
        return $@"
            CREATE TABLE IF NOT EXISTS [{BlobTableName}] (
                id TEXT PRIMARY KEY,
                content_type TEXT NULL,
                created_at INTEGER NULL,
                updated_at INTEGER NULL,
                version INTEGER NOT NULL DEFAULT 1,
                data BLOB NOT NULL
            )";
    }

    /// <summary>
    /// The metadata columns a blob table created before they existed is missing, in the order
    /// they are added, with the definition each is added under.
    /// </summary>
    /// <remarks>
    /// Every definition is legal for <c>ALTER TABLE ADD COLUMN</c>, which requires a constant
    /// default: the timestamps are nullable because a row that already exists has no true
    /// creation time and back-filling <c>now()</c> would invent one, and <c>version</c> defaults
    /// to 1 so an existing row is compare-and-swappable immediately. The fresh table declares the
    /// same nullability, so an upgraded and a new database differ only in column order.
    /// </remarks>
    public static readonly (string Name, string Definition)[] BlobMetadataColumns =
    [
        ("content_type", "TEXT NULL"),
        ("created_at", "INTEGER NULL"),
        ("updated_at", "INTEGER NULL"),
        ("version", "INTEGER NOT NULL DEFAULT 1"),
    ];

    /// <summary>
    /// Generates SQL that counts how many of the blob table's columns carry a given name, for
    /// deciding whether the metadata upgrade still has work to do.
    /// </summary>
    public static string GenerateBlobColumnExistsSql()
    {
        return $"SELECT COUNT(*) FROM pragma_table_info('{BlobTableName}') WHERE name = @Name";
    }

    /// <summary>
    /// Generates SQL for reading the blob table's last declared column, which is what tells the
    /// current layout (payload last) from the one <c>ALTER TABLE ADD COLUMN</c> leaves behind.
    /// </summary>
    /// <remarks>
    /// Answers null when the table does not exist, since a missing table has no columns.
    /// </remarks>
    public static string GenerateBlobLastColumnSql()
    {
        return $"SELECT name FROM pragma_table_info('{BlobTableName}') ORDER BY cid DESC LIMIT 1";
    }

    /// <summary>
    /// Generates SQL adding one metadata column to a blob table created before it existed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The column is not one of <see cref="BlobMetadataColumns"/>.
    /// </exception>
    public static string GenerateAddBlobColumnSql(string columnName)
    {
        // Not merely validated as an identifier: the definition is looked up rather than taken
        // from the caller, so no caller-supplied fragment can reach the DDL.
        foreach (var (name, definition) in BlobMetadataColumns)
        {
            if (string.Equals(name, columnName, StringComparison.Ordinal))
            {
                return $"ALTER TABLE [{BlobTableName}] ADD COLUMN {name} {definition}";
            }
        }

        throw new ArgumentException(
            $"'{columnName}' is not a blob metadata column.", nameof(columnName));
    }

    /// <summary>
    /// Generates the statements that rebuild the blob table into the layout
    /// <see cref="GenerateCreateBlobTableSql"/> produces, preserving every row.
    /// </summary>
    /// <remarks>
    /// Copies every stored byte, so it is never run implicitly — 200 MB of payload took 823 ms
    /// (SQLite 3.53.3), and the copy needs room for both tables at once. The steps are meant to
    /// run inside one transaction, so a failure leaves the original table in place. Step one
    /// drops a scratch table left behind by an interrupted rebuild.
    /// </remarks>
    public static IReadOnlyList<string> GenerateBlobTableRebuildSteps()
    {
        const string scratchTable = BlobTableName + "_rebuild";

        return
        [
            $"DROP TABLE IF EXISTS [{scratchTable}]",
            $@"
            CREATE TABLE [{scratchTable}] (
                id TEXT PRIMARY KEY,
                content_type TEXT NULL,
                created_at INTEGER NULL,
                updated_at INTEGER NULL,
                version INTEGER NOT NULL DEFAULT 1,
                data BLOB NOT NULL
            )",
            $@"
            INSERT INTO [{scratchTable}] (id, content_type, created_at, updated_at, version, data)
            SELECT id, content_type, created_at, updated_at, version, data FROM [{BlobTableName}]",
            $"DROP TABLE [{BlobTableName}]",
            $"ALTER TABLE [{scratchTable}] RENAME TO [{BlobTableName}]",
        ];
    }

    /// <summary>
    /// Generates SQL for upserting a raw binary blob.
    /// </summary>
    /// <remarks>
    /// An overwrite clears the recorded content type unless the write states one: the stored type
    /// described the payload being replaced. <c>created_at</c> is left alone, so it keeps naming
    /// the first write (measured: the unqualified column in <c>DO UPDATE SET</c> reads the
    /// existing row, and one absent from the SET list is untouched).
    /// </remarks>
    public static string GeneratePutBlobSql()
    {
        return $@"
            INSERT INTO [{BlobTableName}] (id, content_type, created_at, updated_at, version, data)
            VALUES (@Id, @ContentType, {NowMillis}, {NowMillis}, 1, @Data)
            ON CONFLICT(id) DO UPDATE SET
                data = excluded.data,
                content_type = excluded.content_type,
                updated_at = excluded.updated_at,
                version = version + 1";
    }

    /// <summary>
    /// Generates SQL for inserting a raw binary blob only when the id is free, returning the
    /// version it was stored at — the compare-and-swap write with an expected version of 0.
    /// </summary>
    public static string GenerateInsertBlobIfAbsentSql()
    {
        return $@"
            INSERT INTO [{BlobTableName}] (id, content_type, created_at, updated_at, version, data)
            VALUES (@Id, @ContentType, {NowMillis}, {NowMillis}, 1, @Data)
            ON CONFLICT(id) DO NOTHING
            RETURNING version";
    }

    /// <summary>
    /// Generates SQL for overwriting a raw binary blob only when its stored version matches,
    /// returning the version it was stored at.
    /// </summary>
    public static string GenerateVersionedPutBlobSql()
    {
        return $@"
            UPDATE [{BlobTableName}] SET
                data = @Data,
                content_type = @ContentType,
                updated_at = {NowMillis},
                version = version + 1
            WHERE id = @Id AND version = @ExpectedVersion
            RETURNING version";
    }

    /// <summary>
    /// Generates SQL for deleting a raw binary blob only when its stored version matches.
    /// </summary>
    public static string GenerateVersionedDeleteBlobSql()
    {
        return $"DELETE FROM [{BlobTableName}] WHERE id = @Id AND version = @ExpectedVersion";
    }

    /// <summary>
    /// Generates SQL for reading a blob's metadata without reading the payload.
    /// </summary>
    /// <remarks>
    /// <c>typeof(data)</c> travels with the row because <c>length()</c> answers for a non-BLOB
    /// payload too — characters for TEXT, digits for a number — so the reported length would
    /// otherwise be a wrong answer rather than a detectable one.
    /// </remarks>
    public static string GenerateBlobMetadataSql()
    {
        return $@"
            SELECT id, typeof(data), length(data), content_type, created_at, updated_at, version
            FROM [{BlobTableName}]
            WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for listing blob metadata in id order, optionally restricted to a prefix
    /// range and paged.
    /// </summary>
    /// <remarks>
    /// The prefix is a half-open range on the primary key rather than a <c>LIKE</c> pattern —
    /// see <see cref="BlobIdPrefix"/> for why. <paramref name="hasUpperBound"/> is separate from
    /// <paramref name="hasPrefix"/> because a prefix ending at the maximum code point has a lower
    /// bound and no upper one.
    /// </remarks>
    public static string GenerateListBlobsSql(
        bool hasPrefix,
        bool hasUpperBound,
        bool hasSkip,
        bool hasTake)
    {
        var sql = new StringBuilder();
        sql.Append($@"
            SELECT id, typeof(data), length(data), content_type, created_at, updated_at, version
            FROM [{BlobTableName}]");

        if (hasPrefix)
        {
            sql.Append(" WHERE id >= @Prefix");
            if (hasUpperBound)
            {
                sql.Append(" AND id < @PrefixEnd");
            }
        }

        sql.Append(" ORDER BY id");

        // SQLite has no OFFSET without a LIMIT, and -1 is its own idiom for "no limit".
        if (hasTake)
        {
            sql.Append(" LIMIT @Take");
        }
        else if (hasSkip)
        {
            sql.Append(" LIMIT -1");
        }

        if (hasSkip)
        {
            sql.Append(" OFFSET @Skip");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Generates SQL for retrieving a raw binary blob by ID, with the storage class its
    /// <c>data</c> column actually holds.
    /// </summary>
    /// <remarks>
    /// <c>typeof(data)</c> leads the projection on every blob read so the payload can be
    /// rejected before it is used. A reader hands back SQLite's coerced bytes for a TEXT,
    /// INTEGER or REAL value without complaint, so nothing downstream can tell those from a
    /// real blob.
    /// </remarks>
    public static string GenerateGetBlobSql()
    {
        return $"SELECT typeof(data), data FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for retrieving the rowid of a blob by ID, which SQLite's incremental
    /// blob I/O addresses rows by, together with its storage class.
    /// </summary>
    /// <remarks>
    /// The storage class is read here because incremental blob I/O accepts a TEXT value and
    /// reads its UTF-8 bytes — only INTEGER and REAL make <c>SqliteBlob</c> refuse — so a
    /// non-BLOB payload has to be caught before the handle opens.
    /// </remarks>
    public static string GenerateBlobRowIdSql()
    {
        return $"SELECT rowid, typeof(data) FROM [{BlobTableName}] WHERE id = @Id";
    }

    /// <summary>
    /// Generates SQL for retrieving the byte length of a blob by ID, without reading it,
    /// together with its storage class.
    /// </summary>
    /// <remarks>
    /// <c>length()</c> counts characters on a TEXT value and digits on a number, so without the
    /// storage class a non-BLOB payload reports a plausible byte count that is not one.
    /// </remarks>
    public static string GenerateBlobLengthSql()
    {
        return $"SELECT typeof(data), length(data) FROM [{BlobTableName}] WHERE id = @Id";
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
            INSERT INTO [{BlobTableName}] (id, content_type, created_at, updated_at, version, data)
            VALUES (@Id, @ContentType, {NowMillis}, {NowMillis}, 1, zeroblob(@Len))
            ON CONFLICT(id) DO UPDATE SET
                data = zeroblob(@Len),
                content_type = excluded.content_type,
                updated_at = excluded.updated_at,
                version = version + 1
            RETURNING rowid, version";
    }

    /// <summary>
    /// Generates the <see cref="GenerateReserveBlobSql"/> statement for a compare-and-swap write
    /// with an expected version of 0: reserve the space only when the id is free.
    /// </summary>
    public static string GenerateReserveBlobIfAbsentSql()
    {
        return $@"
            INSERT INTO [{BlobTableName}] (id, content_type, created_at, updated_at, version, data)
            VALUES (@Id, @ContentType, {NowMillis}, {NowMillis}, 1, zeroblob(@Len))
            ON CONFLICT(id) DO NOTHING
            RETURNING rowid, version";
    }

    /// <summary>
    /// Generates the <see cref="GenerateReserveBlobSql"/> statement guarded by an expected
    /// version: reserve the space only when the stored version still matches.
    /// </summary>
    public static string GenerateVersionedReserveBlobSql()
    {
        return $@"
            UPDATE [{BlobTableName}] SET
                data = zeroblob(@Len),
                content_type = @ContentType,
                updated_at = {NowMillis},
                version = version + 1
            WHERE id = @Id AND version = @ExpectedVersion
            RETURNING rowid, version";
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
    /// <see cref="Exceptions.CorruptDataException"/> instead of being silently dropped.
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
    /// Generates SQL to look up an index by name.
    /// </summary>
    /// <remarks>
    /// Returns the index's stored <c>CREATE INDEX</c> text, so a caller can compare an existing
    /// index against the definition it is about to create rather than only learning that the
    /// name is taken. No row means no such index; a row whose <c>sql</c> is NULL is an index
    /// SQLite created itself (<c>sqlite_autoindex_*</c>), which has no <c>CREATE</c> statement.
    /// </remarks>
    public static string GenerateCheckIndexExistsSql()
    {
        return "SELECT sql FROM sqlite_master WHERE type='index' AND name=@IndexName";
    }

    /// <summary>
    /// Generates SQL for creating an index on a JSON path.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="indexName">The index name</param>
    /// <param name="jsonPath">The JSON path to index (e.g., '$.email'), below the document root</param>
    /// <param name="options">Uniqueness, collation, direction and partial filter, or null for none</param>
    /// <param name="ifNotExists">
    /// Whether to emit the <c>IF NOT EXISTS</c> token. False renders the form SQLite stores in
    /// <c>sqlite_master</c>, which is what an existing index is compared against.
    /// </param>
    public static string GenerateCreateJsonIndexSql(
        string tableName,
        string indexName,
        string jsonPath,
        IndexOptions? options = null,
        bool ifNotExists = true)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));

        // Assigned back over the parameter, not called for its side effect: the return value is the
        // canonical rendering, and the raw argument must not survive to be interpolated instead.
        jsonPath = ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false);

        return BuildCreateIndexSql(tableName, indexName, [jsonPath], options, ifNotExists);
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
    /// <param name="jsonPaths">The JSON paths to index, each below the document root</param>
    /// <param name="options">
    /// Uniqueness, collation, direction and partial filter, or null for none. Collation and
    /// direction apply to every indexed column.
    /// </param>
    /// <param name="ifNotExists">
    /// Whether to emit the <c>IF NOT EXISTS</c> token. False renders the form SQLite stores in
    /// <c>sqlite_master</c>, which is what an existing index is compared against.
    /// </param>
    public static string GenerateCreateCompositeJsonIndexSql(
        string tableName,
        string indexName,
        IEnumerable<string> jsonPaths,
        IndexOptions? options = null,
        bool ifNotExists = true)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));

        var paths = jsonPaths.Select(p => ValidateJsonPath(p, nameof(jsonPaths), allowRoot: false)).ToList();
        return BuildCreateIndexSql(tableName, indexName, paths, options, ifNotExists);
    }

    // Identifiers and paths arrive validated; the options carry two more interpolated pieces —
    // the collation name and the filter paths — so both are validated here, the one boundary
    // where that happens. Default options emit the statement these generators emitted before
    // the options existed: no UNIQUE, no COLLATE, no direction (SQLite's default is ascending)
    // and no WHERE.
    //
    // With ifNotExists false the result is the form SQLite stores in sqlite_master: the header
    // is reconstructed canonically there, and the only difference from the executed statement
    // is that the IF NOT EXISTS token is dropped. The comparison form is generated here rather
    // than cut out of the executed text, so the two cannot drift apart.
    private static string BuildCreateIndexSql(
        string tableName,
        string indexName,
        IReadOnlyList<string> validatedPaths,
        IndexOptions? options,
        bool ifNotExists = true)
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

        sb.Append("INDEX ");
        if (ifNotExists)
        {
            sb.Append("IF NOT EXISTS ");
        }

        sb.Append('[').Append(indexName)
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
        jsonPath = ValidateJsonPath(jsonPath, nameof(jsonPath));

        return $"SELECT id, json(data) as data FROM [{tableName}] WHERE json_extract(data, '{jsonPath}') = @Value";
    }

    /// <summary>
    /// Generates SQL for adding a virtual (generated) column based on a JSON path expression.
    /// The column is generated from json_extract(data, '$.path') and stored as a VIRTUAL column.
    /// </summary>
    /// <param name="tableName">The table name</param>
    /// <param name="columnName">The name for the new virtual column</param>
    /// <param name="jsonPath">The JSON path expression (e.g., '$.email'), below the document root</param>
    /// <param name="columnType">The SQLite column type for the virtual column (e.g., TEXT, INTEGER)</param>
    public static string GenerateAddVirtualColumnSql(
        string tableName,
        string columnName,
        string jsonPath,
        string columnType = "TEXT")
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(columnName, nameof(columnName));
        jsonPath = ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false);
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
    /// <param name="ifNotExists">
    /// Whether to emit the <c>IF NOT EXISTS</c> token. False renders the form SQLite stores in
    /// <c>sqlite_master</c>, which is what an existing index is compared against.
    /// </param>
    public static string GenerateCreateColumnIndexSql(
        string tableName,
        string indexName,
        string columnName,
        bool ifNotExists = true)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateIdentifier(indexName, nameof(indexName));
        ValidateIdentifier(columnName, nameof(columnName));

        var existsClause = ifNotExists ? "IF NOT EXISTS " : string.Empty;
        return $"CREATE INDEX {existsClause}[{indexName}] ON [{tableName}] ([{columnName}])";
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
    /// <param name="paramName">
    /// The name of the <em>caller's</em> parameter the operations came from — <c>patch</c> on the
    /// way in from <c>PatchAsync</c>. The generator is the only validator of the operation caps
    /// (nothing in <c>DocumentPatch&lt;T&gt;</c> counts them), so a cap rejection has to name an
    /// argument the caller actually passed rather than this method's own <c>operations</c>.
    /// </param>
    public static GeneratedQuery GeneratePatchSql(
        string tableName,
        IReadOnlyList<PatchOperation> operations,
        bool versioned,
        string paramName)
    {
        ValidateIdentifier(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            throw new ArgumentException("A patch needs at least one operation.", paramName);
        }

        var values = new List<object?>();
        var sb = new StringBuilder(160);
        sb.Append("UPDATE [").Append(tableName).Append("] SET data = ");

        AppendPatchExpression(sb, operations, values, paramName);

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
        List<object?> values,
        string paramName)
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
                        $"Unsupported patch operation '{operations[i].Kind}'.", paramName);
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
                paramName);
        }

        if (removeCount > MaxPatchRemoveOperations)
        {
            throw new ArgumentException(
                $"The patch removes {removeCount} paths, more than the supported maximum of " +
                $"{MaxPatchRemoveOperations}. Split it into several patches.",
                paramName);
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

            sb.Append(", '").Append(ValidateJsonPath(operation.JsonPath, paramName, allowRoot: false)).Append("', ");

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
                    .Append(ValidateJsonPath(operations[i].JsonPath, paramName, allowRoot: false))
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
    //
    // Internal rather than private for the one caller that has to run the rule before the
    // generator does: DocumentOperations.AddVirtualColumnAsync hoists it, because an existing
    // column short-circuits past the generator entirely.
    internal static string ValidateIdentifier(string identifier, string paramName)
    {
        var error = IdentifierError(identifier);
        if (error is not null)
        {
            throw new ArgumentException(error, paramName);
        }

        return identifier;
    }

    /// <summary>
    /// The non-throwing form of the identifier rule, for a caller that has no parameter to blame:
    /// <c>DocumentOperations.RequireDerivableName</c> screens the index name it is about to derive
    /// from a JSON path, and has to report the failure against the path the caller actually passed,
    /// while <c>TableNameCollisionGuard</c> screens the name the configured convention returned and
    /// reports it against the convention. It shares <see cref="IdentifierError" /> with
    /// <see cref="ValidateIdentifier" /> so the identifier rule keeps one owner.
    /// </summary>
    internal static bool IsValidIdentifier(string identifier) => IdentifierError(identifier) is null;

    // Null when the identifier is valid; otherwise the message describing why it is not.
    private static string? IdentifierError(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "A SQL identifier cannot be null or empty.";
        }

        if (!char.IsAsciiLetter(identifier[0]) && identifier[0] != '_')
        {
            return $"Invalid SQL identifier '{identifier}': it must start with an ASCII letter or an underscore.";
        }

        for (var i = 1; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return $"Invalid SQL identifier '{identifier}': only ASCII letters, digits and underscores are supported.";
            }
        }

        return null;
    }

    // Grammar: $(.member|."member"|[index])*. What a member may contain is stated once, by
    // PathMemberFault/MemberFault below, and how a member is *written* is stated once by
    // MemberNeedsQuoting/AppendCanonicalMember beside it. This walk calls both for each member it
    // tokenizes out; JsonPathResolver calls both for an already-isolated serialized name. Each
    // caller keeps its own message - see MemberFault's own comment for why the rule is what it is.
    //
    // THE RETURN VALUE IS THE CANONICAL RENDERING AND IS WHAT MUST BE INTERPOLATED. A member that
    // can be written unquoted is written unquoted, so a Tier 1 path comes back as the same string
    // instance and a default-configured store sees byte-identical SQL; a member that cannot is
    // written $."quoted". Interpolating the caller's original argument instead would let index
    // creation and query emit different text for one key, and SQLite matches an expression index
    // only when the indexed expression appears literally - so the index would silently stop being
    // used. Every generator in this file therefore assigns the result back over its own path
    // parameter, leaving no second variable holding the raw text.
    //
    // The rendering is idempotent: DocumentQuery and DocumentPatch validate at build time and the
    // generators validate again at generation time, so ValidateJsonPath(ValidateJsonPath(p)) must
    // equal ValidateJsonPath(p). It holds because an unquoted member never needs quoting (the scan
    // stops at '.' and '[', an empty one is refused, and one starting with '"' is read as quoted)
    // and a quoted member re-renders to exactly the text it was read from whenever it still needs
    // quoting.
    //
    // The bare root "$" is grammatically valid; whether it is *usable* splits by what the caller
    // does with the value it extracts, which is what allowRoot selects:
    //
    //   accepted — DocumentQuery predicates, ordering, and IndexFilter terms. Each only reads
    //     through the path, so json_extract(data, '$') over the whole serialized document is a
    //     blunt but legitimate filter.
    //   rejected — the patch targets, and the DDL that projects or indexes a path
    //     (CreateIndexAsync, CreateCompositeIndexAsync, AddVirtualColumnAsync).
    //
    // A patch is destructive at the root — measured against real SQLite, jsonb_set(data, '$', 5)
    // replaces the entire document with the scalar 5, reports success and bumps the version, after
    // which every read of that row throws DocumentSerializationException; jsonb_remove(data, '$')
    // yields NULL and surfaces a raw SqliteException off the data BLOB NOT NULL column, the one
    // shape in the patch API that leaks a provider error instead of validating up front.
    //
    // The DDL is not destructive but projects the whole document: the virtual column duplicates
    // every document on read, and the index keys on its serialized text. The auto-named index
    // forms already failed, but for the wrong reason — the derived name "idx_T_$" was rejected by
    // ValidateIdentifier and reported against an indexName the caller never passed — while the
    // explicitly named ones and the virtual column succeeded. Rejecting the root here makes all of
    // them fail the same way, against the path parameter that actually carries it.
    //
    // The root test is a length test, so the tokenizer preserves the split exactly: "$[0]" reaches
    // into the document and stays legal everywhere, and "$.\"\"" is the empty *key* rather than the
    // root and is legal wherever a key is.
    internal static string ValidateJsonPath(string jsonPath, string paramName, bool allowRoot = true)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath[0] != '$')
        {
            throw new ArgumentException(
                $"Invalid JSON path '{jsonPath}': it must start with '$'.",
                paramName);
        }

        if (!allowRoot && jsonPath.Length == 1)
        {
            throw new ArgumentException(
                "The document root '$' addresses the whole stored JSON value, which this operation " +
                "does not support: target a field or array element below it.",
                paramName);
        }

        // Materialized only when a segment's canonical rendering differs from the text it was read
        // from, so a Tier 1 path allocates nothing and is handed back as the very instance passed in.
        StringBuilder? canonical = null;
        var i = 1;
        while (i < jsonPath.Length)
        {
            var segmentStart = i;
            if (jsonPath[i] == '.')
            {
                i++;
                var member = ReadMember(jsonPath, ref i, paramName, out var quoted);

                // An unquoted member is already canonical by construction; a quoted one is exactly
                // when it still needs its quotes, since the only escapes accepted are the two this
                // renderer emits.
                if (canonical is null && (!quoted || MemberNeedsQuoting(member)))
                {
                    continue;
                }

                canonical ??= new StringBuilder(jsonPath.Length).Append(jsonPath, 0, segmentStart);
                canonical.Append('.');
                AppendCanonicalMember(canonical, member);
            }
            else if (jsonPath[i] == '[')
            {
                ReadIndexer(jsonPath, ref i, paramName);
                canonical?.Append(jsonPath, segmentStart, i - segmentStart);
            }
            else
            {
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': only '.member', '.\"member\"' and '[index]' segments " +
                    "are supported, so every segment after the '$' must begin with a '.' or a '['.",
                    paramName);
            }
        }

        return canonical?.ToString() ?? jsonPath;
    }

    // Reads the member that follows a '.', in either spelling, and applies the member rule to it.
    // The span is safe to hand back: it is cut from jsonPath itself, or from the string the quoted
    // reader built.
    private static ReadOnlySpan<char> ReadMember(
        string jsonPath,
        ref int i,
        string paramName,
        out bool quoted)
    {
        ReadOnlySpan<char> member;
        if (i < jsonPath.Length && jsonPath[i] == '"')
        {
            member = ReadQuotedMember(jsonPath, ref i, paramName);
            quoted = true;
        }
        else
        {
            var memberStart = i;
            while (i < jsonPath.Length && jsonPath[i] != '.' && jsonPath[i] != '[')
            {
                i++;
            }

            member = jsonPath.AsSpan(memberStart, i - memberStart);
            quoted = false;

            // The empty key is reachable, but only spelled out: SQLite errors on a bare "$." and
            // reading it as the empty key would silently turn a truncated path into a working one.
            if (member.IsEmpty)
            {
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': a '.' must be followed by a member name of one " +
                    "or more characters, or by a double-quoted member name. The empty key is " +
                    "written '$.\"\"'.",
                    paramName);
            }
        }

        switch (MemberFault(member))
        {
            case PathMemberFault.None:
                return member;

            case PathMemberFault.Apostrophe:
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': a member name cannot contain an apostrophe, " +
                    "which would close the SQL literal the path is written into.",
                    paramName);

            case PathMemberFault.QuoteInQuotedMember:
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': a member name with no unquoted spelling " +
                    "cannot also contain a '\"'. SQLite 3.45.x, the minimum this library " +
                    "supports, does not unescape a quoted member name, so the key would resolve " +
                    "on a newer engine and silently match nothing on the minimum. Address it " +
                    "through ExecuteRawAsync.",
                    paramName);

            default:
                throw new ArgumentException(
                    $"Invalid JSON path '{jsonPath}': a member name cannot contain U+0000, which " +
                    "truncates the SQL statement the path is written into. No quoting form can " +
                    "address such a key.",
                    paramName);
        }
    }

    private static void ReadIndexer(string jsonPath, ref int i, string paramName)
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

    // The escapes accepted are exactly the two AppendCanonicalMember emits. Anything else is
    // refused rather than guessed at: SQLite unescapes a quoted path label JSON-style, so reading
    // "\b" as a backslash and a 'b' would address a different key than SQLite does - silently.
    private static string ReadQuotedMember(string jsonPath, ref int i, string paramName)
    {
        var member = new StringBuilder();
        var j = i + 1;

        while (j < jsonPath.Length)
        {
            var c = jsonPath[j];
            if (c == '"')
            {
                i = j + 1;
                return member.ToString();
            }

            if (c == '\\')
            {
                j++;
                if (j >= jsonPath.Length)
                {
                    break;
                }

                if (jsonPath[j] is not ('"' or '\\'))
                {
                    throw new ArgumentException(
                        $"Invalid JSON path '{jsonPath}': a quoted member name supports only the '\\\"' and " +
                        "'\\\\' escapes.",
                        paramName);
                }
            }

            member.Append(jsonPath[j]);
            j++;
        }

        throw new ArgumentException(
            $"Invalid JSON path '{jsonPath}': a quoted member name must be closed by an unescaped '\"'.",
            paramName);
    }

    /// <summary>
    /// Which part of the JSON path member rule a member breaks, or <see cref="PathMemberFault.None" /> when it
    /// breaks none. The rule has two callers that report it differently on purpose, so the shared
    /// predicate hands back the reason and each caller words its own exception:
    /// <see cref="ValidateJsonPath" /> blames the path parameter and quotes the whole path, while
    /// <c>JsonPathResolver.ValidPathMember</c> blames the member that produced the name and varies
    /// its recovery advice by reason.
    /// </summary>
    internal enum PathMemberFault
    {
        /// <summary>The member satisfies the rule.</summary>
        None,

        /// <summary>The member contains an apostrophe.</summary>
        Apostrophe,

        /// <summary>The member contains U+0000.</summary>
        Nul,

        /// <summary>
        /// The member has no unquoted spelling and also contains a <c>"</c>, so reaching it needs
        /// an escape the minimum supported SQLite does not decode.
        /// </summary>
        QuoteInQuotedMember
    }

    // The one owner of the member rule, shared by ValidateJsonPath - which tokenizes each member
    // out of a whole path - and JsonPathResolver.ValidPathMember, which is handed a single
    // serialized name. It judges one isolated member and reports *which* rule was broken rather
    // than a bool, because the two callers' messages differ deliberately; it is the path-member
    // equivalent of IdentifierError above, which keeps the identifier rule single-owner the same
    // way. The member is taken as a span so slicing a path allocates nothing on this path.
    //
    // The offending character is the first one by position - both callers reported it that way
    // before the rule was consolidated, and JsonPathResolver's recovery advice splits on it, so
    // "$.a'b\0c" must still name the apostrophe and "$.a\0b'c" the U+0000.
    //
    // THE RULE: a member may hold any characters at all except U+0000 and an apostrophe - and,
    // when it has no unquoted spelling, except a '"' as well. Which members can be written
    // *unquoted* is a separate question, answered by MemberNeedsQuoting below; this rule consults
    // it, because the third exclusion applies only to the members that one sends through quotes.
    //
    // That third exclusion is a version refusal, measured on SQLite 3.45.1 (the floor
    // SqliteVersionGuard enforces, reached through the system libsqlite3) against 3.53.3 (the build
    // Microsoft.Data.Sqlite bundles and every test here runs on), for the key "a.b\"c" - a key that
    // genuinely needs the quotes and also carries a '"', so $."a.b\"c" is a spelling this library
    // really emits:
    //
    //   json_extract(doc, '$."a.b\"c"')       3.45.1 -> NULL             3.53.3 -> the value
    //   jsonb_set(jsonb(doc), same path, 99)  3.45.1 -> bad JSON path    3.53.3 -> writes the key
    //
    // 3.45.1 does not unescape a quoted path label at all, so on the declared minimum an index over
    // such a key would be a valid index over a path no row has - vacuous and silent, the exact
    // defect class JsonPathResolver was fixed to kill. The library therefore refuses the key on
    // every engine rather than letting it work on some and silently miss on others.
    //
    // A '"' in a member that does NOT need quoting is untouched: it renders unquoted, where the
    // character is ordinary and both builds resolve it ($.a"b, measured on both). A '\' inside a
    // quoted member is untouched too, and that is a measurement rather than an assumption:
    // $."a.b\\c" reads and writes its own key on 3.45.1 as well as on 3.53.3, through json_extract
    // and jsonb_set alike - the raw label 3.45.1 compares happens to coincide with the JSON-escaped
    // form there. So the refusal stays on the '"' and is not widened to escapes in general.
    //
    // The unquoted form mirrors SQLite's own unquoted path label, which terminates only at '.' and
    // at '['. Measured against 3.53.3 in json_extract, jsonb_set, jsonb_remove and json_each, all
    // unquoted: $.full-name, "$.a b", an accented key, an emoji key, $.2024, $.a$b, $.a]b, $.a"b
    // and keys carrying a newline or a tab all resolve, and an expression index over such a path is
    // still used (EXPLAIN QUERY PLAN -> SEARCH t USING INDEX ix (<expr>=?)). Identifier-shaped
    // members were the old rule and were far narrower than SQLite allows: JsonNamingPolicy
    // .KebabCaseLower turns "FullName" into "full-name", so a store on the BCL's own kebab-case
    // policy could use no typed query, index or patch API at all.
    //
    // The apostrophe stays rejected, and is the injection boundary: the path is interpolated into a
    // single-quoted SQL literal (json_extract(data, '...')), which an apostrophe would close. It is
    // deliberately *not* supported by doubling - SQLite only matches a query against an expression
    // index when the indexed expression appears literally, so the emitted text must stay
    // byte-identical to what CreateIndexAsync wrote, and a rewrite would break that. Quoting does
    // not rescue it either: the quotes sit *inside* that SQL literal.
    //
    // U+0000 is rejected for a different reason, and permanently. sqlite3_prepare reads a
    // NUL-terminated string, so a NUL in an interpolated path truncates the whole SQL statement at
    // that byte. The path always sits immediately after an opening apostrophe, so the truncated
    // prefix always ends inside an unterminated literal and SQLite always answers SQLITE_ERROR
    // "unrecognized token" - measured across every generator that interpolates a path (query
    // predicates, IN, json_each, ordering, count, exists, patch set, patch remove, create index,
    // create composite index, index filter terms, query-by-path, add virtual column): none of the
    // truncated prefixes is valid SQL. So the failure is loud, but it is a raw SqliteException
    // leaked from six typed APIs carrying a truncated, misleading message, for an argument the
    // validator should refuse up front - the same class as the jsonb_remove(data, '$') NOT NULL
    // leak the root guard above closes. (Bound as a parameter, which this library never does, the
    // quirk is worse and silent: json_extract(doc, @p) with "$.a\0b" reads key "a", and jsonb_set /
    // jsonb_remove write and remove it. Worth knowing when binding a path through ExecuteRawAsync.)
    //
    // U+0000 is NOT a Tier 2 shape, and quoting cannot rescue it - measured both ways: interpolated
    // $."a\0b" fails with "unrecognized token", bound $."a\0b" fails with "bad JSON path". Unquoted,
    // $."quoted" and $['bracket-quoted'] all fail, so such a key is unaddressable by every form.
    // json_each does list it, so one can exist in a stored document and simply cannot be reached.
    internal static PathMemberFault MemberFault(ReadOnlySpan<char> member)
    {
        var carriesQuote = false;
        foreach (var c in member)
        {
            switch (c)
            {
                case '\'':
                    return PathMemberFault.Apostrophe;
                case '\0':
                    return PathMemberFault.Nul;
                case '"':
                    carriesQuote = true;
                    break;
            }
        }

        // Tested after the scan so the two positional faults keep their precedence: a member
        // carrying both an apostrophe and a '"' is still blamed on the apostrophe.
        return carriesQuote && MemberNeedsQuoting(member)
            ? PathMemberFault.QuoteInQuotedMember
            : PathMemberFault.None;
    }

    // The second half of the member rule's owner: which members cannot be written unquoted, and
    // how a quoted one is spelled. Both entry points render through AppendCanonicalMember, so the
    // path an index is created over and the path a query interpolates are one string by
    // construction rather than by review.
    //
    // Three shapes need the quotes, each measured against real SQLite:
    //
    //   a key containing '.'  - "$.a.b" is unambiguously the nested path a -> b. Reaching the
    //     single key "a.b" is why Tier 2 is worth implementing rather than documenting: measured,
    //     json_extract returns NULL for it while jsonb_set and jsonb_remove SILENTLY NO-OP,
    //     returning the document unchanged, so a patch reports success and bumps the version
    //     having written nothing. Quoted, all three address the key.
    //   a key containing '['  - "$.a[0]" is the member a followed by an indexer.
    //   the empty key         - SQLite errors on a bare "$.".
    //
    // A member merely *starting* with '"' needs them too, because SQLite reads a '"' straight after
    // the '.' as opening a quoted label: measured, "$.\"lead" is answered with "bad JSON path". A
    // '"' anywhere else is an ordinary unquoted character and stays one, which is what keeps Tier 1
    // byte-identical.
    //
    // Inside the quotes, '"' and '\' are escaped JSON-style as \" and \\ - never by SQL doubling,
    // which SQLite reads as something else entirely: measured, '$."a""b"' silently resolves the
    // WRONG key rather than failing.
    //
    // The \" arm of AppendCanonicalMember below is defensive rather than reachable, and the reason
    // sits beside MemberFault: a member that needs these quotes and carries a '"' is refused there,
    // because 3.45.1 does not unescape a quoted label and such a key resolves to nothing on the
    // declared minimum. So only the \\ arm fires through either entry point. The \" arm stays
    // because this is the renderer, and a renderer that dropped the escape would be wrong on its
    // own terms; ReadQuotedMember keeps accepting \" for the same reason, so a caller who writes
    // the spelling out is answered by the version refusal rather than by "unsupported escape".
    internal static bool MemberNeedsQuoting(ReadOnlySpan<char> member) =>
        member.IsEmpty || member[0] == '"' || member.IndexOfAny('.', '[') >= 0;

    /// <summary>
    /// Appends a member in its canonical rendering — unquoted when it can be, <c>"quoted"</c> when
    /// it cannot. The single owner of that rendering, called by
    /// <see cref="ValidateJsonPath" /> and by <c>JsonPathResolver</c>.
    /// </summary>
    internal static void AppendCanonicalMember(StringBuilder sb, ReadOnlySpan<char> member)
    {
        if (!MemberNeedsQuoting(member))
        {
            sb.Append(member);
            return;
        }

        sb.Append('"');
        foreach (var c in member)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
    }

    // The type lands unquoted in ALTER TABLE ... ADD COLUMN, so a whitelist is the only option.
    // Internal for the same reason as ValidateIdentifier above: the virtual-column path hoists it.
    internal static string ValidateColumnType(string columnType)
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
