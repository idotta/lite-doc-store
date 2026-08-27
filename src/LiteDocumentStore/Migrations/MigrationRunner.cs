using System.Data;
using System.Globalization;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore;

/// <summary>
/// Applies and rolls back schema migrations against one connection, tracking what has been
/// applied in a history table.
/// </summary>
/// <remarks>
/// <para>
/// Internal: migrations are reached through <see cref="IDocumentStore.MigrateAsync(IEnumerable{IMigration}, CancellationToken)"/>
/// and its siblings, each of which rents one pooled connection and builds a runner over it.
/// Handing a pooled connection to a consumer is what the connection model exists to prevent.
/// </para>
/// <para>
/// Every apply and every rollback runs in its own <c>BEGIN IMMEDIATE</c> transaction, and the
/// membership check happens <b>inside</b> it. That is what makes two processes starting
/// together safe: the loser blocks on the write lock (bounded by
/// <see cref="DocumentStoreOptions.BusyTimeoutMs"/>), then re-reads the history table, sees the
/// row the winner committed and reports "already applied" instead of failing on the primary key.
/// </para>
/// <para>
/// Transactions are per migration, not per run: if versions 1 and 2 commit and 3 throws, 1 and 2
/// stay applied.
/// </para>
/// </remarks>
internal sealed class MigrationRunner
{
    private const string MigrationTableName = "__store_migrations";

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private bool _schemaEnsured;

    /// <summary>
    /// Initializes a new migration runner over an open connection.
    /// </summary>
    /// <param name="connection">The open SQLite connection</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    internal MigrationRunner(SqliteConnection connection, ILogger? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets every applied migration, ordered by version.
    /// </summary>
    internal async Task<IReadOnlyList<MigrationHistoryRecord>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        var sql = $@"
            SELECT version, name, applied_at, checksum
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
                    DateTimeStyles.RoundtripKind),
                Checksum = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }

        return records;
    }

    /// <summary>
    /// Gets the highest applied migration version, or 0 when none have been applied.
    /// </summary>
    internal async Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        var version = await ReadCurrentVersionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Current migration version: {Version}", version);
        return version;
    }

    /// <summary>
    /// Applies every migration that is not already in the history table, in ascending version
    /// order, and returns how many were applied.
    /// </summary>
    internal async Task<int> ApplyMigrationsAsync(
        IEnumerable<IMigration> migrations,
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        var ordered = Validate(migrations, nameof(migrations));
        ArgumentNullException.ThrowIfNull(options);

        var appliedCount = 0;
        foreach (var migration in ordered)
        {
            if (await ApplyMigrationAsync(migration, options, cancellationToken).ConfigureAwait(false))
            {
                appliedCount++;
            }
        }

        _logger.LogInformation("Applied {Count} migrations", appliedCount);
        return appliedCount;
    }

    /// <summary>
    /// Applies one migration unless the history table already holds its version.
    /// </summary>
    /// <returns>True when the migration was applied, false when it was already applied</returns>
    internal async Task<bool> ApplyMigrationAsync(
        IMigration migration,
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(options);

        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        // Immediate, so the write lock is taken before the membership check rather than after
        // it: a concurrent process waits here and then observes the committed history row.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            var applied = await TryReadHistoryAsync(migration.Version, cancellationToken).ConfigureAwait(false);
            if (applied.Found)
            {
                if (options.VerifyChecksums)
                {
                    VerifyChecksum(migration, applied.Checksum);
                }

                _logger.LogDebug("Migration {Version} ({Name}) already applied, skipping",
                    migration.Version, migration.Name);
                transaction.Commit();
                return false;
            }

            var currentVersion = await ReadCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
            if (migration.Version < currentVersion)
            {
                if (!options.AllowOutOfOrder)
                {
                    throw new MigrationOutOfOrderException(migration.Version, migration.Name, currentVersion);
                }

                _logger.LogWarning(
                    "Applying out-of-order migration {Version} ({Name}) below the current version {CurrentVersion}",
                    migration.Version, migration.Name, currentVersion);
            }

            _logger.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.Name);

            await migration.UpAsync(_connection, cancellationToken).ConfigureAwait(false);

            // Commands created on the connection automatically enlist in the active transaction.
            // Persist DateTimeOffset as an ISO-8601 round-trip string.
            var sql = $@"
                INSERT INTO [{MigrationTableName}] (version, name, applied_at, checksum)
                VALUES (@Version, @Name, @AppliedAt, @Checksum)";

            await _connection.ExecuteAsync(
                sql,
                cancellationToken,
                ("Version", migration.Version),
                ("Name", migration.Name),
                ("AppliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ("Checksum", migration.Checksum))
                .ConfigureAwait(false);

            transaction.Commit();
            _logger.LogInformation("Migration {Version} applied successfully", migration.Version);
            return true;
        }
        catch (Exception ex)
        {
            // Disposal rolls the transaction back; no explicit Rollback needed here.
            _logger.LogError(ex, "Failed to apply migration {Version}: {Name}",
                migration.Version, migration.Name);
            throw;
        }
    }

    /// <summary>
    /// Rolls back one migration when the history table holds its version.
    /// </summary>
    /// <returns>True when the migration was rolled back, false when it was not applied</returns>
    internal async Task<bool> RollbackMigrationAsync(
        IMigration migration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);

        await EnsureMigrationTableExistsAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            var applied = await TryReadHistoryAsync(migration.Version, cancellationToken).ConfigureAwait(false);
            if (!applied.Found)
            {
                _logger.LogDebug("Migration {Version} ({Name}) not applied, nothing to rollback",
                    migration.Version, migration.Name);
                transaction.Commit();
                return false;
            }

            _logger.LogInformation("Rolling back migration {Version}: {Name}", migration.Version, migration.Name);

            await migration.DownAsync(_connection, cancellationToken).ConfigureAwait(false);

            var deleteSql = $@"DELETE FROM [{MigrationTableName}] WHERE version = @Version";
            await _connection.ExecuteAsync(deleteSql, cancellationToken, ("Version", migration.Version))
                .ConfigureAwait(false);

            transaction.Commit();
            _logger.LogInformation("Migration {Version} rolled back successfully", migration.Version);
            return true;
        }
        catch (Exception ex)
        {
            // Disposal rolls the transaction back; no explicit Rollback needed here.
            _logger.LogError(ex, "Failed to rollback migration {Version}: {Name}",
                migration.Version, migration.Name);
            throw;
        }
    }

    /// <summary>
    /// Rolls back every applied migration above <paramref name="targetVersion"/>, newest first.
    /// </summary>
    internal async Task<int> RollbackToVersionAsync(
        long targetVersion,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetVersion);
        var ordered = Validate(migrations, nameof(migrations));

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

        var migrationDict = ordered.ToDictionary(m => m.Version);

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
            if (await RollbackMigrationAsync(migration, cancellationToken).ConfigureAwait(false))
            {
                rolledBackCount++;
            }
        }

        _logger.LogInformation("Rolled back {Count} migrations to version {Version}",
            rolledBackCount, targetVersion);
        return rolledBackCount;
    }

    /// <summary>
    /// Materializes the supplied migrations in ascending version order, rejecting a null element
    /// or a duplicate version before anything is applied.
    /// </summary>
    private static List<IMigration> Validate(IEnumerable<IMigration> migrations, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(migrations, parameterName);

        var list = migrations.ToList();
        var seen = new Dictionary<long, int>();

        for (var i = 0; i < list.Count; i++)
        {
            var migration = list[i];
            if (migration is null)
            {
                throw new ArgumentException($"Migration at index {i} is null.", parameterName);
            }

            if (seen.TryGetValue(migration.Version, out var firstIndex))
            {
                throw new ArgumentException(
                    $"Duplicate migration version {migration.Version} at indices {firstIndex} and {i}.",
                    parameterName);
            }

            seen.Add(migration.Version, i);
        }

        return [.. list.OrderBy(m => m.Version)];
    }

    private static void VerifyChecksum(IMigration migration, string? storedChecksum)
    {
        var current = migration.Checksum;

        // Either side null means the comparison carries no information: a migration that opts
        // out, or a history row written before checksums were tracked.
        if (storedChecksum is null
            || current is null
            || string.Equals(storedChecksum, current, StringComparison.Ordinal))
        {
            return;
        }

        throw new MigrationChecksumMismatchException(migration.Version, migration.Name, storedChecksum, current);
    }

    private async Task<(bool Found, string? Checksum)> TryReadHistoryAsync(
        long version,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $@"SELECT checksum FROM [{MigrationTableName}] WHERE version = @Version";
        command.Parameters.AddWithValue("@Version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, null);
        }

        return (true, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    private Task<long> ReadCurrentVersionAsync(CancellationToken cancellationToken) =>
        _connection.ExecuteScalarAsync<long>(
            $@"SELECT COALESCE(MAX(version), 0) FROM [{MigrationTableName}]",
            cancellationToken);

    /// <summary>
    /// Creates the history table when it is absent, and adds the checksum column to a table
    /// created before checksums were tracked.
    /// </summary>
    private async Task EnsureMigrationTableExistsAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        var sql = $@"
            CREATE TABLE IF NOT EXISTS [{MigrationTableName}] (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                checksum TEXT NULL
            )";

        _logger.LogDebug("Ensuring migration history table exists");
        await _connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);

        if (!await HasChecksumColumnAsync(cancellationToken).ConfigureAwait(false))
        {
            await AddChecksumColumnAsync(cancellationToken).ConfigureAwait(false);
        }

        _schemaEnsured = true;
    }

    /// <summary>
    /// Adds the checksum column to a legacy history table, re-checking under the write lock so
    /// two processes starting together cannot both issue the ALTER.
    /// </summary>
    private async Task AddChecksumColumnAsync(CancellationToken cancellationToken)
    {
        // No catch: disposing an uncommitted transaction rolls it back.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        if (await HasChecksumColumnAsync(cancellationToken).ConfigureAwait(false))
        {
            transaction.Commit();
            return;
        }

        _logger.LogInformation("Upgrading migration history table: adding the checksum column");
        await _connection.ExecuteAsync(
            $"ALTER TABLE [{MigrationTableName}] ADD COLUMN checksum TEXT NULL",
            cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    private async Task<bool> HasChecksumColumnAsync(CancellationToken cancellationToken)
    {
        var count = await _connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM pragma_table_info('{MigrationTableName}') WHERE name = 'checksum'",
            cancellationToken).ConfigureAwait(false);

        return count > 0;
    }
}
