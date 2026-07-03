using System.Data;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Defines the contract for a document store that provides JSON document storage
/// with full relational database capabilities. Supports multiple entity types
/// through generic methods. Implements disposal interfaces for proper resource cleanup.
/// </summary>
public interface IDocumentStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Creates a table for storing JSON objects with a generic schema using JSONB format.
    /// The table name will be derived from the type T.
    /// </summary>
    /// <typeparam name="T">Type whose name will be used as the table name</typeparam>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateTableAsync<T>();

    /// <summary>
    /// Inserts or updates a JSON object in the document store using JSONB format.
    /// </summary>
    /// <typeparam name="T">Type of the object to store (also used as table name)</typeparam>
    /// <param name="id">Unique identifier for the object</param>
    /// <param name="data">The object to store</param>
    /// <returns>The number of rows affected by the operation</returns>
    Task<int> UpsertAsync<T>(string id, T data);

    /// <summary>
    /// Inserts or updates multiple JSON objects in the document store using a single SQL statement.
    /// This is more efficient than calling UpsertAsync multiple times.
    /// </summary>
    /// <typeparam name="T">Type of the objects to store (also used as table name)</typeparam>
    /// <param name="items">Collection of (id, data) tuples to upsert</param>
    /// <returns>The total number of rows affected by the operation</returns>
    Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items);

    /// <summary>
    /// Retrieves a JSON object by its ID from the document store.
    /// </summary>
    /// <typeparam name="T">Type of the object to retrieve (also used as table name)</typeparam>
    /// <param name="id">Unique identifier of the object</param>
    /// <returns>The deserialized object, or default if not found</returns>
    Task<T?> GetAsync<T>(string id);

    /// <summary>
    /// Retrieves all JSON objects from the document store.
    /// </summary>
    /// <typeparam name="T">Type of the objects to retrieve (also used as table name)</typeparam>
    /// <returns>An enumerable of deserialized objects</returns>
    Task<IEnumerable<T>> GetAllAsync<T>();

    /// <summary>
    /// Deletes a JSON object by its ID from the document store.
    /// </summary>
    /// <typeparam name="T">Type whose name will be used as the table name</typeparam>
    /// <param name="id">Unique identifier of the object to delete</param>
    /// <returns>True if the object was deleted, false if it didn't exist</returns>
    Task<bool> DeleteAsync<T>(string id);

    /// <summary>
    /// Deletes multiple JSON objects by their IDs from the document store using a single SQL statement.
    /// This is more efficient than calling DeleteAsync multiple times.
    /// </summary>
    /// <typeparam name="T">Type whose name will be used as the table name</typeparam>
    /// <param name="ids">Collection of unique identifiers of the objects to delete</param>
    /// <returns>The number of rows affected (documents deleted)</returns>
    Task<int> DeleteManyAsync<T>(IEnumerable<string> ids);

    /// <summary>
    /// Checks if a document exists in the store without deserializing it.
    /// More efficient than GetAsync when you only need to check existence.
    /// </summary>
    /// <typeparam name="T">Type whose name will be used as the table name</typeparam>
    /// <param name="id">Unique identifier of the document to check</param>
    /// <returns>True if the document exists, false otherwise</returns>
    Task<bool> ExistsAsync<T>(string id);

    /// <summary>
    /// Gets the total count of documents in a table.
    /// </summary>
    /// <typeparam name="T">Type whose name will be used as the table name</typeparam>
    /// <returns>The number of documents in the table</returns>
    Task<long> CountAsync<T>();

    /// <summary>
    /// Executes a batch of operations within a transaction for optimal performance.
    /// </summary>
    /// <param name="action">Async action to execute within the transaction</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> action);

    /// <summary>
    /// Executes a batch of operations within a transaction for optimal performance.
    /// </summary>
    /// <param name="action">Async action to execute within the transaction</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task ExecuteInTransactionAsync(Func<Task> action);

    /// <summary>
    /// Checks if the document store is healthy and ready for operations.
    /// Verifies the connection is open and validates SQLite version supports JSONB (3.45+).
    /// Useful for liveness probes in containerized environments and health monitoring.
    /// </summary>
    /// <returns>True if the store is healthy, false otherwise</returns>
    Task<bool> IsHealthyAsync();

    /// <summary>
    /// Creates an index on a JSON path expression for optimized query performance.
    /// Automatically checks if the index exists before creation to avoid errors.
    /// </summary>
    /// <typeparam name="T">Type whose table will have the index created</typeparam>
    /// <param name="jsonPath">Expression selecting the JSON property to index</param>
    /// <param name="indexName">Optional custom index name. If null, a name will be auto-generated</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateIndexAsync<T>(System.Linq.Expressions.Expression<Func<T, object>> jsonPath, string? indexName = null);

    /// <summary>
    /// Creates a composite index on multiple JSON path expressions for optimized multi-column queries.
    /// Automatically checks if the index exists before creation to avoid errors.
    /// </summary>
    /// <typeparam name="T">Type whose table will have the index created</typeparam>
    /// <param name="jsonPaths">Array of expressions selecting the JSON properties to index</param>
    /// <param name="indexName">Optional custom index name. If null, a name will be auto-generated</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateCompositeIndexAsync<T>(System.Linq.Expressions.Expression<Func<T, object>>[] jsonPaths, string? indexName = null);

    /// <summary>
    /// Adds a virtual (generated) column to a table based on a JSON path expression.
    /// Virtual columns are computed from json_extract(data, '$.path') and can be indexed for better query performance.
    /// Automatically checks if the column exists before creation to avoid errors.
    /// </summary>
    /// <typeparam name="T">Type whose table will have the column added</typeparam>
    /// <param name="jsonPath">Expression selecting the JSON property to extract (e.g., x => x.Email)</param>
    /// <param name="columnName">Name for the new virtual column</param>
    /// <param name="createIndex">If true, automatically creates an index on the virtual column</param>
    /// <param name="columnType">The SQLite column type (TEXT, INTEGER, REAL, etc.). Defaults to TEXT.</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <example>
    /// <code>
    /// // Add a virtual column for email with automatic indexing
    /// await store.AddVirtualColumnAsync&lt;Customer&gt;(x => x.Email, "email", createIndex: true);
    /// 
    /// // Now queries can use the virtual column directly
    /// // SELECT * FROM Customer WHERE email = 'john@example.com'
    /// </code>
    /// </example>
    Task AddVirtualColumnAsync<T>(
        System.Linq.Expressions.Expression<Func<T, object>> jsonPath,
        string columnName,
        bool createIndex = false,
        string columnType = "TEXT");

    /// <summary>
    /// Queries documents by a JSON path and value using json_extract().
    /// Supports patterns like: $.property, $.nested.property, $.array[0]
    /// </summary>
    /// <typeparam name="T">Type of the objects to retrieve (also used as table name)</typeparam>
    /// <typeparam name="TValue">Type of the value to match</typeparam>
    /// <param name="jsonPath">The JSON path to query (e.g., '$.email', '$.address.city')</param>
    /// <param name="value">The value to match</param>
    /// <returns>An enumerable of deserialized objects matching the query</returns>
    Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value);

    /// <summary>
    /// Gets the underlying SQLite connection for advanced operations and raw SQL access.
    /// This enables the hybrid experience where users can use both document storage
    /// and traditional relational database features.
    /// </summary>
    SqliteConnection Connection { get; }
}
