namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Thrown when an applied migration's recorded checksum differs from the checksum of the
/// migration definition supplied to the run — the definition was edited after it was applied.
/// </summary>
/// <remarks>
/// Only the up SQL is covered (see <see cref="IMigration.Checksum"/>), so an edit confined to
/// the down SQL is deliberately undetected. Verification is skipped when either checksum is
/// null, which is what keeps history written before checksums existed usable.
/// </remarks>
public class MigrationChecksumMismatchException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the version of the migration whose checksum drifted.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Gets the name of the migration whose checksum drifted.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the checksum recorded in the history table when the migration was applied.
    /// </summary>
    public string? ExpectedChecksum { get; }

    /// <summary>
    /// Gets the checksum of the migration definition supplied to the current run.
    /// </summary>
    public string? ActualChecksum { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationChecksumMismatchException"/> class.
    /// </summary>
    /// <param name="version">The version of the migration whose checksum drifted.</param>
    /// <param name="name">The name of the migration whose checksum drifted.</param>
    /// <param name="expectedChecksum">The checksum recorded when the migration was applied.</param>
    /// <param name="actualChecksum">The checksum of the definition supplied to this run.</param>
    public MigrationChecksumMismatchException(
        long version,
        string? name,
        string? expectedChecksum,
        string? actualChecksum)
        : base($"Migration {version} ({name}) was applied with checksum {expectedChecksum} but the supplied definition has checksum {actualChecksum}. The migration was edited after it was applied.")
    {
        Version = version;
        Name = name;
        ExpectedChecksum = expectedChecksum;
        ActualChecksum = actualChecksum;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationChecksumMismatchException"/> class
    /// with an explicit message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public MigrationChecksumMismatchException(string message)
        : base(message)
    {
    }
}
