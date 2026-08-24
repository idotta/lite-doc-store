namespace LiteDocumentStore;

/// <summary>
/// The policy a migration run applies to out-of-order versions and to checksum drift.
/// </summary>
/// <remarks>
/// <para>
/// Passed to the options-bearing <see cref="IDocumentStore.MigrateAsync(IEnumerable{IMigration}, MigrationOptions, CancellationToken)"/>
/// overload. The parameterless overload uses <see cref="Default"/>.
/// </para>
/// <para>
/// Neither setting affects rollback: <see cref="IDocumentStore.RollbackToVersionAsync"/> takes
/// no options and never verifies checksums.
/// </para>
/// </remarks>
public sealed class MigrationOptions
{
    /// <summary>
    /// The default policy: out-of-order versions are rejected and checksums are verified.
    /// </summary>
    public static MigrationOptions Default { get; } = new();

    /// <summary>
    /// Applies a migration whose version is below the highest already-applied version instead
    /// of throwing <see cref="Exceptions.MigrationOutOfOrderException"/>. Such a back-fill is
    /// usually a branch-merge accident — the migration runs against a schema its author never
    /// saw — so it is rejected unless this is set.
    /// </summary>
    public bool AllowOutOfOrder { get; init; }

    /// <summary>
    /// Compares <see cref="IMigration.Checksum"/> against the checksum recorded when the
    /// migration was applied, throwing
    /// <see cref="Exceptions.MigrationChecksumMismatchException"/> when they differ. Verification
    /// is skipped for any migration where either side is null. Defaults to true.
    /// </summary>
    public bool VerifyChecksums { get; init; } = true;
}
