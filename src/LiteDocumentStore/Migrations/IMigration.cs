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
