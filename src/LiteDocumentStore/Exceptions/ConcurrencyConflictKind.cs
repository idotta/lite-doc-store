namespace LiteDocumentStore.Exceptions;

/// <summary>
/// A classification of an optimistic-concurrency conflict, so a caller can pick a retry strategy
/// without parsing the exception message. The value is derived from a stored-version read taken
/// after the guard rejected the operation, so outside a transaction it describes the row as
/// observed then, which need not be the state that caused the rejection.
/// </summary>
public enum ConcurrencyConflictKind
{
    /// <summary>
    /// The reason was not determined.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An insert was requested (a write with expected version 0) and the id was found taken.
    /// Never reported for a delete, where expected version 0 targets a row still at 0.
    /// </summary>
    AlreadyExists = 1,

    /// <summary>
    /// The document was found, but at a version other than the expected one.
    /// </summary>
    VersionMismatch = 2,

    /// <summary>
    /// No document with that id was found, so there was no version to match.
    /// </summary>
    DocumentNotFound = 3,
}
