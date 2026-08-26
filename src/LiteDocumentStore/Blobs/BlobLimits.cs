namespace LiteDocumentStore;

/// <summary>
/// The limits the store applies to raw binary payloads stored in the reserved blob table.
/// </summary>
public static class BlobLimits
{
    /// <summary>
    /// The largest payload the store will accept, in bytes (1,000,000,000).
    /// </summary>
    /// <remarks>
    /// This is a library policy, not a runtime probe: it is derived from SQLite's default
    /// <c>SQLITE_MAX_LENGTH</c> of one billion bytes, which a custom build can raise or lower.
    /// Rejecting here turns a payload SQLite would refuse into an <see cref="ArgumentOutOfRangeException"/>
    /// naming the length, instead of an opaque failure part-way through a large write.
    /// </remarks>
    public const long MaxBlobLength = 1_000_000_000L;
}
