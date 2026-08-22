using System.Linq.Expressions;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// The document operations available both directly on an <see cref="IDocumentStore"/> and
/// inside an <see cref="IDocumentTransaction"/>.
/// </summary>
/// <remarks>
/// Called on the store, each operation runs on its own connection and commits immediately.
/// Called on a transaction, every operation runs on that transaction's connection and is
/// committed or rolled back with it.
/// </remarks>
public interface IDocumentOperations
{
    /// <summary>
    /// Creates the document table for <typeparamref name="T"/> if it does not exist.
    /// Schema: <c>id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0</c>.
    /// </summary>
    /// <typeparam name="T">The document type whose table should be created</typeparam>
    Task CreateTableAsync<T>();

    /// <summary>
    /// Inserts or replaces a document, incrementing its version.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <param name="data">The document to store</param>
    /// <returns>The number of affected rows</returns>
    Task<int> UpsertAsync<T>(string id, T data);

    /// <summary>
    /// Inserts or replaces many documents in a single statement.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="items">The id/document pairs to store</param>
    /// <returns>The number of affected rows</returns>
    Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items);

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
    /// <returns>The new version of the document</returns>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the stored version does not match <paramref name="expectedVersion"/>, or
    /// when <paramref name="expectedVersion"/> is 0 and the document already exists.
    /// </exception>
    Task<long> UpsertWithVersionAsync<T>(string id, T data, long expectedVersion);

    /// <summary>
    /// Retrieves a document together with its current version, for read-modify-write cycles.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <returns>The document and its version, or null when not found</returns>
    Task<VersionedDocument<T>?> GetWithVersionAsync<T>(string id);

    /// <summary>
    /// Retrieves a document by id.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <returns>The document, or default when not found</returns>
    Task<T?> GetAsync<T>(string id);

    /// <summary>
    /// Retrieves every document of the given type.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <returns>All stored documents</returns>
    Task<IEnumerable<T>> GetAllAsync<T>();

    /// <summary>
    /// Deletes a document by id.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <returns>True when a row was deleted</returns>
    Task<bool> DeleteAsync<T>(string id);

    /// <summary>
    /// Deletes many documents in a single statement.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="ids">The document identifiers to delete</param>
    /// <returns>The number of deleted rows</returns>
    Task<int> DeleteManyAsync<T>(IEnumerable<string> ids);

    /// <summary>
    /// Determines whether a document exists.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="id">The document identifier</param>
    /// <returns>True when the document exists</returns>
    Task<bool> ExistsAsync<T>(string id);

    /// <summary>
    /// Counts the documents of the given type.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <returns>The number of stored documents</returns>
    Task<long> CountAsync<T>();

    /// <summary>
    /// Queries documents by a JSON path and value, using
    /// <c>WHERE json_extract(data, '$.Path') = @Value</c>.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <typeparam name="TValue">The compared value's type</typeparam>
    /// <param name="jsonPath">The JSON path, e.g. <c>$.Email</c></param>
    /// <param name="value">The value to match</param>
    /// <returns>The matching documents</returns>
    Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value);

    /// <summary>
    /// Creates an index over a JSON path, if it does not already exist.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPath">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    Task CreateIndexAsync<T>(Expression<Func<T, object>> jsonPath, string? indexName = null);

    /// <summary>
    /// Creates a composite index over several JSON paths, if it does not already exist.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPaths">Property-access expressions, in index column order</param>
    /// <param name="indexName">An explicit index name, or null to derive one</param>
    Task CreateCompositeIndexAsync<T>(Expression<Func<T, object>>[] jsonPaths, string? indexName = null);

    /// <summary>
    /// Adds a generated (virtual) column projecting a JSON path, so that raw SQL can index
    /// and seek on it.
    /// </summary>
    /// <typeparam name="T">The document type</typeparam>
    /// <param name="jsonPath">A property-access expression, e.g. <c>x => x.Email</c></param>
    /// <param name="columnName">The generated column's name</param>
    /// <param name="createIndex">Whether to also index the column</param>
    /// <param name="columnType">The column's SQLite type (default TEXT)</param>
    Task AddVirtualColumnAsync<T>(
        Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT");

    /// <summary>
    /// Creates the reserved raw-blob table (<c>__store_blobs</c>) if it does not exist.
    /// </summary>
    Task CreateBlobTableAsync();

    /// <summary>
    /// Stores a raw binary payload verbatim (no JSONB conversion).
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <param name="data">The bytes to store</param>
    Task PutBlobAsync(string id, ReadOnlyMemory<byte> data);

    /// <summary>
    /// Reads a raw binary payload.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <returns>The stored bytes, or null when not found</returns>
    Task<byte[]?> GetBlobAsync(string id);

    /// <summary>
    /// Deletes a raw binary payload.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <returns>True when a row was deleted</returns>
    Task<bool> DeleteBlobAsync(string id);

    /// <summary>
    /// Determines whether a raw binary payload exists.
    /// </summary>
    /// <param name="id">The blob identifier</param>
    /// <returns>True when the blob exists</returns>
    Task<bool> BlobExistsAsync(string id);

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
}
