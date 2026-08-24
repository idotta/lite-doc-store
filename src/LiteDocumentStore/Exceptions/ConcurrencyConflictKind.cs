namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Why an optimistic-concurrency write or delete was rejected, so a caller can pick a retry
/// strategy without parsing the exception message.
/// </summary>
public enum ConcurrencyConflictKind
{
    /// <summary>
    /// The reason was not determined.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An insert was requested (expected version 0) but the document already exists.
    /// </summary>
    AlreadyExists = 1,

    /// <summary>
    /// The document exists but its stored version differs from the expected version.
    /// </summary>
    VersionMismatch = 2,

    /// <summary>
    /// The document does not exist, so there was no version to match.
    /// </summary>
    DocumentNotFound = 3,
}
