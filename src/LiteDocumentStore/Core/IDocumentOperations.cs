using System.Linq.Expressions;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// The document operations available both directly on an <see cref="IDocumentStore"/> and
/// inside an <see cref="IDocumentTransaction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Called on the store, each operation runs on its own connection and commits immediately.
/// Called on a transaction, every operation runs on that transaction's connection and is
/// committed or rolled back with it.
/// </para>
/// <para>
/// <b>What a cancellation token does here.</b> On the store it cancels the wait for a free
/// pooled connection, and it is passed to the ADO command. Microsoft.Data.Sqlite performs
/// SQLite I/O synchronously, so it cannot interrupt a statement already executing — a
/// cancelled token is observed before the command starts, not part-way through it.
/// </para>
/// </remarks>
public interface IDocumentOperations
{
    /// <summary>
    /// Creates the document table for <typeparamref name="T"/> if it does not exist.
    /// Schema: <c>id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1</c>.
    /// </summary>
    /// <typeparam name="T">The document type whose table should be created</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task CreateTableAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces a document, incrementing its version.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="data">The document to store</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of affected rows</returns>
    Task<int> UpsertAsync<T>(string id, T data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces many documents in a single statement.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="items">The id/document pairs to store</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of affected rows</returns>
    Task<int> UpsertManyAsync<T>(
        IEnumerable<(string id, T data)> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap write for optimistic concurrency.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="data">The document to store</param>
    /// <param name="expectedVersion">
    /// The version the caller expects to overwrite. Pass 0 to insert a document that must not
    /// already exist — a row left at version 0 by raw SQL is updated instead, lifting it to 1.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The stored version of the document after the write</returns>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the stored version does not match <paramref name="expectedVersion"/>, or
    /// when <paramref name="expectedVersion"/> is 0 and the document already exists. The
    /// exception carries both versions and a <see cref="Exceptions.ConcurrencyConflictKind"/>.
    /// </exception>
    Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap delete for optimistic concurrency: removes the document only when its
    /// stored version still matches, so a read-modify-delete cannot drop a concurrent update.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="expectedVersion">The version the caller expects to delete</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the document does not exist or its stored version does not match
    /// <paramref name="expectedVersion"/>. The exception carries both versions and a
    /// <see cref="Exceptions.ConcurrencyConflictKind"/>.
    /// </exception>
    Task DeleteWithVersionAsync<T>(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies field-level changes to a stored document in a single statement, leaving every
    /// field the patch does not name untouched.
    /// </summary>
    /// <remarks>
    /// This is the alternative to a read-modify-write cycle, which reserializes the whole
    /// document and therefore overwrites a concurrent writer's edits to unrelated fields. All
    /// of the patch's operations apply in one statement and bump the version once.
    /// A patch carries no full document, so it cannot insert: a missing id is a conflict, not
    /// a silent no-op.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="patch">The changes to apply</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The stored version of the document after the patch</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or empty</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="patch"/> is null</exception>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when no document with that id exists, carrying
    /// <see cref="Exceptions.ConcurrencyConflictKind.DocumentNotFound"/>.
    /// </exception>
    Task<long> PatchAsync<T>(
        string id,
        DocumentPatch<T> patch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap form of <see cref="PatchAsync{T}"/>: applies the patch only when the
    /// stored version still matches.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="patch">The changes to apply</param>
    /// <param name="expectedVersion">The version the caller expects to patch</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The stored version of the document after the patch</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or empty</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="patch"/> is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="expectedVersion"/> is negative
    /// </exception>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the document does not exist or its stored version does not match
    /// <paramref name="expectedVersion"/>. The exception carries both versions and a
    /// <see cref="Exceptions.ConcurrencyConflictKind"/>.
    /// </exception>
    Task<long> PatchWithVersionAsync<T>(
        string id,
        DocumentPatch<T> patch,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a document together with its current version, for read-modify-write cycles.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The document and its version, or null when not found</returns>
    /// <exception cref="Exceptions.SerializationException">
    /// Thrown when the row exists but its stored JSON deserializes to null, which would
    /// otherwise be indistinguishable from not found.
    /// </exception>
    Task<VersionedDocument<T>?> GetWithVersionAsync<T>(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a document by id.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The document, or default when not found</returns>
    /// <exception cref="Exceptions.SerializationException">
    /// Thrown when the row exists but its stored JSON deserializes to null.
    /// </exception>
    Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every document of the given type.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>All stored documents</returns>
    Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves several documents by id in one statement per chunk, keyed by id.
    /// </summary>
    /// <remarks>
    /// Ids that are not stored are simply absent from the result, so the dictionary may hold
    /// fewer entries than <paramref name="ids"/> has elements. Duplicates are collapsed, and an
    /// empty input performs no round trip at all. A large input is read in several statements,
    /// so the result is only a point-in-time snapshot when the call is made on an
    /// <see cref="IDocumentTransaction"/>.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="ids">The document identifiers to read</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The documents that were found, keyed by their id</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="ids"/> is null</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any id is null, empty or whitespace
    /// </exception>
    Task<IReadOnlyDictionary<string, T>> GetManyAsync<T>(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document by id.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when a row was deleted</returns>
    Task<bool> DeleteAsync<T>(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes many documents in a single statement.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="ids">The document identifiers to delete</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of deleted rows</returns>
    Task<int> DeleteManyAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every document of the given type, leaving the table itself in place.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of deleted rows</returns>
    Task<int> DeleteAllAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a document exists.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when the document exists</returns>
    Task<bool> ExistsAsync<T>(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the documents of the given type.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of stored documents</returns>
    Task<long> CountAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries documents by a JSON path and value, using
    /// <c>WHERE json_extract(data, '$.Path') = @Value</c>.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <typeparam name="TValue">The compared value's type</typeparam>
    /// <param name="jsonPath">The JSON path, e.g. <c>$.Email</c></param>
    /// <param name="value">The value to match</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The matching documents</returns>
    Task<IEnumerable<T>> QueryAsync<T, TValue>(
        string jsonPath,
        TValue value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries documents with a structured <see cref="DocumentQuery{T}"/> — comparisons,
    /// <c>LIKE</c>/<c>GLOB</c>, <c>IN</c>, null tests and array membership, combined with
    /// <c>AND</c>, plus ordering and paging.
    /// </summary>
    /// <remarks>
    /// The query carries data, not SQL: every value is bound as a parameter and every JSON path
    /// is validated before it reaches the statement.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="query">The query specification, built from <see cref="DocumentQuery{T}"/></param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The matching documents</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is null</exception>
    Task<IEnumerable<T>> QueryAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the documents matching a structured <see cref="DocumentQuery{T}"/>. Ordering and
    /// paging on the query are ignored — only its predicates apply.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="query">The query specification, built from <see cref="DocumentQuery{T}"/></param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of matching documents</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is null</exception>
    Task<long> CountAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any document matches a structured <see cref="DocumentQuery{T}"/>.
    /// Ordering and paging on the query are ignored — only its predicates apply.
    /// </summary>
    /// <remarks>
    /// Cheaper than <see cref="CountAsync{T}(DocumentQuery{T}, CancellationToken)"/> when the
    /// match is large: the statement stops at the first matching row instead of counting them all.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="query">The query specification, built from <see cref="DocumentQuery{T}"/></param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when at least one document matches</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is null</exception>
    Task<bool> ExistsAsync<T>(
        DocumentQuery<T> query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an index over a JSON path, if it does not already exist.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPath">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an index over a JSON path with explicit DDL options (unique, collation,
    /// direction, partial filter), if an index of that name does not already exist.
    /// </summary>
    /// <remarks>
    /// An overload rather than an extra parameter on
    /// <see cref="CreateIndexAsync{T}(Expression{Func{T, object}}, string, CancellationToken)"/>:
    /// inserting one before the trailing token would break every caller passing the token
    /// positionally. Creation is skipped when the name exists, options and all, so changing an
    /// existing index's options means dropping it first.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPath">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    /// <param name="options">The index DDL options</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="IndexOptions.Collation"/> is not a valid SQL identifier
    /// </exception>
    Task CreateIndexAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a composite index over several JSON paths, if it does not already exist.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPaths">Property-access expressions, in index column order</param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a composite index over several JSON paths with explicit DDL options, if an index
    /// of that name does not already exist.
    /// </summary>
    /// <remarks>
    /// <see cref="IndexOptions.Collation"/> and <see cref="IndexOptions.Descending"/> apply to
    /// <b>every</b> indexed column; a mixed per-column direction stays an
    /// <c>ExecuteRawAsync</c> job. An overload for the reason
    /// <see cref="CreateIndexAsync{T}(Expression{Func{T, object}}, string, IndexOptions, CancellationToken)"/>
    /// is one.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPaths">Property-access expressions, in index column order</param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    /// <param name="options">The index DDL options</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="jsonPaths"/> is empty, or when
    /// <see cref="IndexOptions.Collation"/> is not a valid SQL identifier
    /// </exception>
    Task CreateCompositeIndexAsync<T>(
        Expression<Func<T, object>>[] jsonPaths,
        string? indexName,
        IndexOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a generated (virtual) column projecting a JSON path, so that raw SQL can index
    /// and seek on it.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPath">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="columnName">The generated column's name</param>
    /// <param name="createIndex">Whether to also index the column</param>
    /// <param name="columnType">The column's SQLite type (default TEXT)</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the document table for <typeparamref name="T"/> if it exists, discarding every
    /// document in it.
    /// </summary>
    /// <remarks>Idempotent: dropping a table that does not exist is a no-op.</remarks>
    /// <typeparam name="T">The document type whose table should be dropped</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task DropTableAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an index by name if it exists.
    /// </summary>
    /// <remarks>Idempotent: dropping an index that does not exist is a no-op.</remarks>
    /// <param name="indexName">The index name</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="indexName"/> is null, empty or whitespace
    /// </exception>
    Task DropIndexAsync(string indexName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the index <see cref="CreateIndexAsync{T}(Expression{Func{T, object}}, string, CancellationToken)"/> derives for the same JSON path, if it
    /// exists.
    /// </summary>
    /// <remarks>
    /// The name is derived exactly as <see cref="CreateIndexAsync{T}(Expression{Func{T, object}}, string, CancellationToken)"/> derives it, so this drops
    /// the index that call created. An index created under an explicit name must be dropped
    /// through <see cref="DropIndexAsync(string, CancellationToken)"/>.
    /// </remarks>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="expression">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task DropIndexAsync<T>(
        Expression<Func<T, object>> expression,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the reserved raw-blob table (<c>__store_blobs</c>) if it does not exist.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task CreateBlobTableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a raw binary payload verbatim (no JSONB conversion).
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="data">The bytes to store</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    Task PutBlobAsync(string id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a raw binary payload read from a stream, without materializing it in memory.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="source">
    /// The bytes to store. Read from its current position and left open; the store never
    /// disposes it.
    /// </param>
    /// <param name="length">
    /// The exact number of bytes to read from <paramref name="source"/>. SQLite's incremental
    /// blob I/O cannot resize a blob, so the row is reserved at this size before the first byte
    /// is written and the source must hold exactly this many bytes.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <remarks>
    /// Nothing is handed back to the caller, so there is no lifetime to manage: the copy happens
    /// inside the call. Called on the store it runs in its own transaction, so a failure part-way
    /// leaves any previous blob under <paramref name="id"/> intact; called on an
    /// <see cref="IDocumentTransaction"/> it commits with the caller's other writes.
    /// </remarks>
    /// <exception cref="EndOfStreamException">
    /// <paramref name="source"/> ended before <paramref name="length"/> bytes were read.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> holds more than <paramref name="length"/> bytes, is not
    /// readable, or <paramref name="id"/> is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="length"/> is negative or above <see cref="BlobLimits.MaxBlobLength"/>.
    /// </exception>
    Task PutBlobAsync(string id, Stream source, long length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the byte length of a raw binary payload without reading the payload itself.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The stored length in bytes, or null when the blob does not exist</returns>
    Task<long?> BlobLengthAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a raw binary payload.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The stored bytes, or null when not found</returns>
    Task<byte[]?> GetBlobAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a raw binary payload.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when a row was deleted</returns>
    Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a raw binary payload exists.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True when the blob exists</returns>
    Task<bool> BlobExistsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs arbitrary SQL against a connection owned by the store — the escape hatch for
    /// joins, aggregates, virtual-column seeks and anything else the document API does not
    /// cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection is valid only for the duration of the callback; do not store it. When
    /// called on an <see cref="IDocumentTransaction"/>, the connection is the transaction's,
    /// so commands created from it enlist in that transaction.
    /// </para>
    /// <para>
    /// Build commands with <c>connection.CreateCommand()</c>, which copies the active
    /// transaction onto them. A directly constructed <see cref="SqliteCommand"/> has no
    /// transaction and will not execute while one is pending.
    /// </para>
    /// </remarks>
    /// <typeparam name="TResult">The result type</typeparam>
    /// <param name="operation">The work to run against the connection</param>
    /// <param name="cancellationToken">A token to cancel waiting for a free connection</param>
    /// <returns>The callback's result</returns>
    Task<TResult> ExecuteRawAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteRawAsync{TResult}" />
    Task ExecuteRawAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the table name this store uses for <typeparamref name="T"/>, for interpolating into
    /// raw SQL.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <returns>The table name produced by the configured <see cref="ITableNamingConvention"/></returns>
    string GetTableName<T>();

    /// <summary>
    /// Serializes a document to the same UTF-8 JSON bytes the store writes, for binding to a raw
    /// <c>jsonb(@Data)</c> parameter.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="value">The document to serialize</param>
    /// <returns>The UTF-8 JSON bytes</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null</exception>
    /// <exception cref="Exceptions.SerializationException">Thrown when serialization fails</exception>
    byte[] SerializeDocument<T>(T value);

    /// <summary>
    /// Deserializes the JSON text a raw <c>SELECT json(data)</c> column yields.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="json">The JSON text, as returned by <c>json(data)</c></param>
    /// <returns>The document, or default when <paramref name="json"/> is null or empty</returns>
    /// <exception cref="Exceptions.SerializationException">Thrown when deserialization fails</exception>
    T? DeserializeDocument<T>(string? json);
}
