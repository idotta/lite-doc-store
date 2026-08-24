using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Represents a SQL-based database schema migration with version control and up/down support.
/// Provides a simple way to define migrations using raw SQL statements.
/// </summary>
public class Migration : IMigration
{
    private readonly string _upSql;
    private readonly string _downSql;

    /// <summary>
    /// Initializes a new migration with the specified version, name, and SQL statements.
    /// </summary>
    /// <param name="version">The unique version identifier (e.g., 20260109001)</param>
    /// <param name="name">A descriptive name for this migration</param>
    /// <param name="upSql">SQL to execute when applying this migration</param>
    /// <param name="downSql">SQL to execute when reverting this migration</param>
    public Migration(long version, string name, string upSql, string downSql)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(upSql);
        ArgumentNullException.ThrowIfNull(downSql);

        if (version <= 0)
        {
            throw new ArgumentException("Version must be greater than zero", nameof(version));
        }

        Version = version;
        Name = name;
        _upSql = upSql;
        _downSql = downSql;
        Checksum = ComputeChecksum(upSql);
    }

    /// <inheritdoc />
    public long Version { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Gets the uppercase SHA-256 hex digest of this migration's UTF-8 up SQL. The down SQL is
    /// deliberately excluded: it is not part of what was applied, so editing it does not fail a
    /// later run.
    /// </summary>
    public string Checksum { get; }

    /// <inheritdoc />
    public virtual async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(_upSql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(_downSql, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeChecksum(string upSql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(upSql)));
}
