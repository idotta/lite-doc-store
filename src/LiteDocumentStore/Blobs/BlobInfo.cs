namespace LiteDocumentStore;

/// <summary>
/// The metadata the store keeps about a raw binary payload, read without touching the payload
/// itself.
/// </summary>
/// <param name="Id">The blob identifier</param>
/// <param name="Length">
/// The stored payload size in bytes. Read as <c>length(data)</c> rather than from a stored
/// column, so it cannot drift from the payload a consumer writes through raw SQL.
/// </param>
/// <param name="ContentType">
/// The content type recorded on the last write, or null when none was supplied.
/// </param>
/// <param name="CreatedAt">
/// When the blob was first written, or null for a row written before the store tracked
/// timestamps (see <see cref="IDocumentOperations.CreateBlobTableAsync"/>) or inserted through
/// raw SQL.
/// </param>
/// <param name="UpdatedAt">
/// When the blob was last written, under the same null caveat as <paramref name="CreatedAt"/>.
/// </param>
/// <param name="Version">
/// The optimistic-concurrency version, incremented on every store write. A row inserted through
/// raw SQL starts at the column default of 1.
/// </param>
public sealed record BlobInfo(
    string Id,
    long Length,
    string? ContentType,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    long Version);
