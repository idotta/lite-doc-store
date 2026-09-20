using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Represents a SQL-based database schema migration with version control and up/down support.
/// Provides a simple way to define migrations using raw SQL statements.
/// </summary>
public class SqlMigration : IMigration
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
    public SqlMigration(long version, string name, string upSql, string downSql)
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
    /// <remarks>
    /// <para>
    /// <strong>The base value covers exactly the <c>upSql</c> handed to the constructor, and
    /// nothing else.</strong> A subclass that overrides <see cref="UpAsync"/> and changes what
    /// actually runs — extra SQL, SQL built in code, work that does not reach the connection at
    /// all — <strong>must</strong> override this property as well and return a value that changes
    /// whenever its own behaviour changes. Nothing detects the omission: the recorded checksum
    /// simply keeps describing the constructor's SQL while a different migration runs, so an edit
    /// to the override goes unreported. An override that only delegates
    /// (<c>await base.UpAsync(connection, cancellationToken)</c> with no SQL of its own) changes
    /// nothing about what runs and is already covered — prefer that shape, and leave this property
    /// alone for it.
    /// </para>
    /// <para>
    /// <c>public new string Checksum =&gt; …</c> does <strong>not</strong> work: the runner reads
    /// the checksum through <see cref="IMigration"/>, and interface dispatch on a hidden member
    /// lands on this property, so the base digest is what gets stored and compared. Use
    /// <c>override</c>.
    /// </para>
    /// <para>
    /// Returning null opts this migration out of verification entirely — the comparison is skipped
    /// whenever either side is null. Because this property is declared non-nullable, an override
    /// that opts out must write <c>=&gt; null!</c>. Opting out later is not an error and not a
    /// repair: if the history row already holds a non-null checksum, a subsequent null does not
    /// fail the run, it simply stops verifying that migration from then on.
    /// </para>
    /// </remarks>
    public virtual string Checksum { get; }

    /// <inheritdoc />
    /// <remarks>
    /// See <see cref="IMigration.UpAsync"/> for the connection contract every override inherits;
    /// this remark adds to it rather than replacing it.
    /// Overriding this changes what the checksum has to describe, so cover <see cref="Checksum"/>
    /// too — override it and return a value that changes with this method's behaviour. An override
    /// that just delegates (<c>await base.UpAsync(connection, cancellationToken)</c> and adds no
    /// SQL of its own) is already covered by the base digest and needs no checksum work; prefer
    /// that shape.
    /// </remarks>
    public virtual async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(_upSql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// See <see cref="IMigration.DownAsync"/> for the connection contract every override inherits;
    /// this remark adds to it rather than replacing it.
    /// Overriding this needs no checksum work: only the up definition is covered, and rollback
    /// never verifies checksums at all.
    /// </remarks>
    public virtual async Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(_downSql, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeChecksum(string upSql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(upSql)));
}
