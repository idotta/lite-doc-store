namespace LiteDocumentStore;

/// <summary>
/// Defines the contract for creating <see cref="IDocumentStore"/> instances.
/// The factory composes all dependencies (connection pool, serializer, naming convention, logger)
/// and returns a ready-to-use document store that owns its connections.
/// </summary>
public interface IDocumentStoreFactory
{
    /// <summary>
    /// Creates a new document store with the specified options.
    /// The returned store owns its connection pool and should be disposed when no longer needed.
    /// </summary>
    /// <param name="options">Configuration options for the store</param>
    /// <returns>A new document store instance</returns>
    IDocumentStore Create(DocumentStoreOptions options);

    /// <summary>
    /// Creates a new document store with the specified options asynchronously.
    /// The returned store owns its connection pool and should be disposed when no longer needed.
    /// </summary>
    /// <param name="options">Configuration options for the store</param>
    /// <returns>A task containing the new document store instance</returns>
    Task<IDocumentStore> CreateAsync(DocumentStoreOptions options);
}
