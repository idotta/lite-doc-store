namespace LiteDocumentStore;

/// <summary>
/// Represents a record of an applied migration in the migration history table. An instance is a
/// snapshot of one history row, rebuilt from the reader on every
/// <see cref="IDocumentStore.GetAppliedMigrationsAsync(System.Threading.CancellationToken)"/> call,
/// so the properties are <c>init</c>-only: a write would never have persisted.
/// </summary>
public sealed class MigrationHistoryRecord
{
    /// <summary>
    /// Gets the migration version identifier.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Gets the descriptive name of the migration.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the timestamp when the migration was applied.
    /// </summary>
    public DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Gets the checksum recorded when the migration was applied, or null when the
    /// migration carried none or the row predates checksum tracking.
    /// </summary>
    public string? Checksum { get; init; }
}
