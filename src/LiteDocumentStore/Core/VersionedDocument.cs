namespace LiteDocumentStore;

/// <summary>
/// A document paired with its optimistic-concurrency version.
/// Returned by <see cref="IDocumentStore.GetWithVersionAsync{T}"/>; pass
/// <see cref="Version"/> back to <see cref="IDocumentStore.UpsertWithVersionAsync{T}"/>
/// to perform a compare-and-swap write.
/// </summary>
/// <typeparam name="T">Type of the stored document</typeparam>
/// <param name="Data">The deserialized document</param>
/// <param name="Version">The version of the stored row (starts at 1, incremented on every write)</param>
public sealed record VersionedDocument<T>(T Data, long Version);
