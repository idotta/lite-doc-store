namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Exception thrown when the loaded SQLite library is older than the version LiteDocumentStore
/// requires, or reports a version string that cannot be parsed.
/// </summary>
/// <remarks>
/// Every document is stored through SQLite's <c>jsonb()</c> function, which first shipped in
/// SQLite 3.45.0. On an older library the store would open successfully and then fail on the
/// first write with <c>SqliteException: no such function: jsonb</c>, so the version is checked
/// when a connection is opened and this exception is thrown instead.
/// </remarks>
public class UnsupportedSqliteVersionException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the version string reported by <c>SELECT sqlite_version()</c>, verbatim. It is a
    /// string rather than a <see cref="Version"/> because this exception is also thrown when the
    /// value could not be parsed at all; it is null when the query returned no value.
    /// </summary>
    public string? ActualVersion { get; }

    /// <summary>
    /// Gets the minimum SQLite version the store requires.
    /// </summary>
    public Version? MinimumVersion { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedSqliteVersionException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public UnsupportedSqliteVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedSqliteVersionException"/> class with an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public UnsupportedSqliteVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedSqliteVersionException"/> class with
    /// the reported and required versions.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="actualVersion">The version string reported by the loaded SQLite library.</param>
    /// <param name="minimumVersion">The minimum SQLite version the store requires.</param>
    public UnsupportedSqliteVersionException(string message, string? actualVersion, Version? minimumVersion)
        : base(message)
    {
        ActualVersion = actualVersion;
        MinimumVersion = minimumVersion;
    }
}
