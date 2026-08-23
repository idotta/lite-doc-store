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
    /// Schema: <c>id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0</c>.
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
    /// already exist.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The new version of the document</returns>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the stored version does not match <paramref name="expectedVersion"/>, or
    /// when <paramref name="expectedVersion"/> is 0 and the document already exists.
    /// </exception>
    Task<long> UpsertWithVersionAsync<T>(
        string id,
        T data,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a document together with its current version, for read-modify-write cycles.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The document and its version, or null when not found</returns>
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
    Task<T?> GetAsync<T>(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every document of the given type.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>All stored documents</returns>
    Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default);

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
