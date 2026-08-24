namespace LiteDocumentStore;

/// <summary>
/// Defines a database schema migration with version control and up/down support.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Gets the unique version identifier for this migration.
    /// Migrations are applied in ascending order by version.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// Gets a descriptive name for this migration.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a checksum of this migration's applied state, or null to opt out of drift
    /// detection. It is recorded with the history row and compared on every later run, so an
    /// edit to an already-applied migration is reported rather than silently ignored.
    /// </summary>
    /// <remarks>
    /// Only the <em>up</em> definition should be covered: the down definition is not part of
    /// what was applied, so editing it must not fail a startup migration. <see cref="Migration"/>
    /// returns an uppercase SHA-256 hex digest of its UTF-8 up SQL. An implementation that
    /// returns null is never verified, which is what keeps history written before checksums
    /// existed usable.
    /// </remarks>
    string? Checksum => null;

    /// <summary>
    /// Applies the migration (upgrade operation).
    /// </summary>
    /// <param name="connection">The SQLite connection to execute the migration on</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task UpAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts the migration (downgrade operation).
    /// </summary>
    /// <param name="connection">The SQLite connection to execute the rollback on</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task DownAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken = default);
}
