namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Thrown when a migration that has never been applied carries a version below the highest
/// version already in the history table, and <see cref="MigrationOptions.AllowOutOfOrder"/> is
/// not set.
/// </summary>
public class MigrationOutOfOrderException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the version of the migration that was rejected.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Gets the name of the migration that was rejected.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the highest version applied when the migration was rejected, read under the same
    /// write transaction as the membership check.
    /// </summary>
    public long CurrentVersion { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationOutOfOrderException"/> class.
    /// </summary>
    /// <param name="version">The version of the migration that was rejected.</param>
    /// <param name="name">The name of the migration that was rejected.</param>
    /// <param name="currentVersion">The highest version already applied.</param>
    public MigrationOutOfOrderException(long version, string? name, long currentVersion)
        : base($"Migration {version} ({name}) has not been applied but its version is below the current version {currentVersion}. Set MigrationOptions.AllowOutOfOrder to apply it anyway.")
    {
        Version = version;
        Name = name;
        CurrentVersion = currentVersion;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationOutOfOrderException"/> class with
    /// an explicit message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public MigrationOutOfOrderException(string message)
        : base(message)
    {
    }
}
