using System.Globalization;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore;

/// <summary>
/// Manages database schema migrations with a history tracking table.
/// Ensures migrations are applied in order and can be rolled back safely.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationTableName = "__store_migrations";
    private readonly SqliteConnection _connection;
    private readonly ILogger<MigrationRunner> _logger;

    /// <summary>
    /// Initializes a new migration runner with the specified connection.
    /// </summary>
    /// <param name="connection">The open SQLite connection</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public MigrationRunner(SqliteConnection connection, ILogger<MigrationRunner>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<MigrationRunner>.Instance;
    }

    /// <summary>
    /// Ensures the migration history table exists.
    /// </summary>
    private async Task EnsureMigrationTableExistsAsync(CancellationToken cancellationToken)
    {
        var sql = $@"
            CREATE TABLE IF NOT EXISTS [{MigrationTableName}] (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            )";

        _logger.LogDebug("Ensuring migration history table exists");
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all applied migrations from the history table, ordered by version.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>An enumerable of migration history records</returns>
    public async Task<IEnumerable<MigrationHistoryRecord>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        var sql = $@"
            SELECT version, name, applied_at
            FROM [{MigrationTableName}]
            ORDER BY version";

        _logger.LogDebug("Retrieving applied migrations");

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<MigrationHistoryRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new MigrationHistoryRecord
            {
                Version = reader.GetInt64(0),
                Name = reader.GetString(1),
                AppliedAt = DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }

        return records;
    }

    /// <summary>
    /// Gets the highest applied migration version, or 0 if no migrations have been applied.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The current migration version</returns>
    public async Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        var sql = $@"SELECT COALESCE(MAX(version), 0) FROM [{MigrationTableName}]";
        var version = await _connection.ExecuteScalarAsync<long>(sql, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Current migration version: {Version}", version);
        return version;
    }

    /// <summary>
    /// Applies a migration if it hasn't been applied yet.
    /// </summary>
    /// <param name="migration">The migration to apply</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True if the migration was applied, false if it was already applied</returns>
    public async Task<bool> ApplyMigrationAsync(IMigration migration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);

        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        var currentVersion = await GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);

        if (migration.Version <= currentVersion)
        {
            _logger.LogDebug("Migration {Version} ({Name}) already applied, skipping",
                migration.Version, migration.Name);
            return false;
        }

        _logger.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.Name);

        using var transaction = _connection.BeginTransaction();
        try
        {
            // Execute the migration
            await migration.UpAsync(_connection, cancellationToken).ConfigureAwait(false);

            // Record the migration. Commands created on the connection automatically enlist
            // in the active transaction. Persist DateTimeOffset as an ISO-8601 round-trip string.
            var sql = $@"
                INSERT INTO [{MigrationTableName}] (version, name, applied_at)
                VALUES (@Version, @Name, @AppliedAt)";

            await _connection.ExecuteAsync(
                sql,
                cancellationToken,
                ("Version", migration.Version),
                ("Name", migration.Name),
                ("AppliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);

            transaction.Commit();
            _logger.LogInformation("Migration {Version} applied successfully", migration.Version);
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to apply migration {Version}: {Name}",
                migration.Version, migration.Name);
            throw;
        }
    }

    /// <summary>
    /// Applies multiple migrations in order, stopping at the first failure.
    /// </summary>
    /// <param name="migrations">The migrations to apply, in order</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of migrations that were applied</returns>
    public async Task<int> ApplyMigrationsAsync(
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var orderedMigrations = migrations.OrderBy(m => m.Version).ToList();
        var appliedCount = 0;

        foreach (var migration in orderedMigrations)
        {
            var applied = await ApplyMigrationAsync(migration, cancellationToken).ConfigureAwait(false);
            if (applied)
            {
                appliedCount++;
            }
        }

        _logger.LogInformation("Applied {Count} migrations", appliedCount);
        return appliedCount;
    }

    /// <summary>
    /// Rolls back the most recent migration.
    /// </summary>
    /// <param name="migration">The migration to roll back</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>True if the migration was rolled back, false if it wasn't applied</returns>
    public async Task<bool> RollbackMigrationAsync(IMigration migration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);

        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        // Check if this migration is actually applied
        var checkSql = $@"SELECT COUNT(*) FROM [{MigrationTableName}] WHERE version = @Version";
        var isApplied = await _connection.ExecuteScalarAsync<int>(checkSql, cancellationToken, ("Version", migration.Version))
            .ConfigureAwait(false) > 0;

        if (!isApplied)
        {
            _logger.LogDebug("Migration {Version} ({Name}) not applied, nothing to rollback",
                migration.Version, migration.Name);
            return false;
        }

        _logger.LogInformation("Rolling back migration {Version}: {Name}", migration.Version, migration.Name);

        using var transaction = _connection.BeginTransaction();
        try
        {
            // Execute the rollback
            await migration.DownAsync(_connection, cancellationToken).ConfigureAwait(false);

            // Remove the migration record (enlists in the active transaction automatically)
            var deleteSql = $@"DELETE FROM [{MigrationTableName}] WHERE version = @Version";
            await _connection.ExecuteAsync(deleteSql, cancellationToken, ("Version", migration.Version))
                .ConfigureAwait(false);

            transaction.Commit();
            _logger.LogInformation("Migration {Version} rolled back successfully", migration.Version);
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to rollback migration {Version}: {Name}",
                migration.Version, migration.Name);
            throw;
        }
    }

    /// <summary>
    /// Rolls back to a specific version by reverting migrations in reverse order.
    /// </summary>
    /// <param name="targetVersion">The version to rollback to (0 to rollback all)</param>
    /// <param name="migrations">All available migrations</param>
    /// <param name="cancellationToken">A token to cancel the operation</param>
    /// <returns>The number of migrations that were rolled back</returns>
    public async Task<int> RollbackToVersionAsync(
        long targetVersion,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var appliedMigrations = await GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var migrationsToRollback = appliedMigrations
            .Where(m => m.Version > targetVersion)
            .OrderByDescending(m => m.Version)
            .ToList();

        if (migrationsToRollback.Count == 0)
        {
            _logger.LogDebug("No migrations to rollback to version {Version}", targetVersion);
            return 0;
        }

        var migrationDict = migrations.ToDictionary(m => m.Version);

        // Fail before mutating anything: every migration in the rollback range must have a
        // definition. Rolling back only part of the range would leave the schema and the history
        // table in an inconsistent state, so refuse the whole operation instead of skipping.
        var missingVersions = migrationsToRollback
            .Where(record => !migrationDict.ContainsKey(record.Version))
            .Select(record => record.Version)
            .ToList();

        if (missingVersions.Count > 0)
        {
            var versions = string.Join(", ", missingVersions);
            _logger.LogError(
                "Cannot roll back to version {Target}: missing migration definition(s) for version(s) {Versions}",
                targetVersion, versions);
            throw new LiteDocumentStoreException(
                $"Cannot roll back to version {targetVersion}: missing migration definition(s) for version(s) {versions}. No migrations were rolled back.");
        }

        var rolledBackCount = 0;
        foreach (var record in migrationsToRollback)
        {
            var migration = migrationDict[record.Version];
            var rolledBack = await RollbackMigrationAsync(migration, cancellationToken).ConfigureAwait(false);
            if (rolledBack)
            {
                rolledBackCount++;
            }
        }

        _logger.LogInformation("Rolled back {Count} migrations to version {Version}",
            rolledBackCount, targetVersion);
        return rolledBackCount;
    }
}
