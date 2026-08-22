using Microsoft.Data.Sqlite;
using LiteDocumentStore.Exceptions;

namespace LiteDocumentStore;

/// <summary>
/// Verifies that the SQLite library behind a connection is new enough to provide
/// <c>jsonb()</c>, which every document write depends on.
/// </summary>
/// <remarks>
/// <para>
/// The check runs once per physical connection, in <see cref="SqliteConnectionPool"/> rather
/// than in <see cref="DefaultConnectionFactory"/>: <see cref="IConnectionFactory"/> is public,
/// so a consumer-supplied factory would otherwise open unguarded connections. The pool is the
/// single place every connection the store uses comes from.
/// </para>
/// <para>
/// The reported version is a property of the loaded native library, so it cannot differ
/// between connections in one process and the result could be cached. It is not: a pool opens
/// at most <see cref="DocumentStoreOptions.MaxPoolSize"/> connections for the store's whole
/// lifetime, so the cache would save a handful of sub-microsecond queries in exchange for
/// process-wide mutable state.
/// </para>
/// </remarks>
internal static class SqliteVersionGuard
{
    /// <summary>
    /// The first SQLite version that ships the <c>jsonb()</c> function.
    /// </summary>
    public static readonly Version MinimumVersion = new(3, 45, 0);

    /// <summary>
    /// Reads the version from an open connection and throws when it is too old.
    /// </summary>
    /// <returns>The validated version, for logging.</returns>
    /// <exception cref="UnsupportedSqliteVersionException">
    /// The library is older than <see cref="MinimumVersion"/>, or reported a version string that
    /// could not be parsed.
    /// </exception>
    public static async Task<Version> EnsureSupportedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        var introspector = new SchemaIntrospector(connection);
        var reported = await introspector.GetSqliteVersionAsync(cancellationToken).ConfigureAwait(false);
        return Validate(reported);
    }

    /// <inheritdoc cref="EnsureSupportedAsync" />
    public static Version EnsureSupported(SqliteConnection connection)
    {
        // SchemaIntrospector is async-only, and its sole caller-facing method here would need a
        // synchronous twin for this one call site. The query is the same one it runs.
        return Validate(connection.QueryFirstString("SELECT sqlite_version()"));
    }

    /// <summary>
    /// Validates a version string as reported by <c>SELECT sqlite_version()</c>.
    /// </summary>
    /// <remarks>
    /// Internal so the grammar can be tested without an old SQLite library to hand.
    /// </remarks>
    internal static Version Validate(string? reported)
    {
        if (!Version.TryParse(reported, out var version))
        {
            throw new UnsupportedSqliteVersionException(
                $"Could not determine the SQLite version (SELECT sqlite_version() returned " +
                $"'{reported ?? "<null>"}'). LiteDocumentStore requires SQLite {MinimumVersion} or " +
                "later, because documents are stored with the jsonb() function it introduced.",
                reported,
                MinimumVersion);
        }

        if (version < MinimumVersion)
        {
            throw new UnsupportedSqliteVersionException(
                $"LiteDocumentStore requires SQLite {MinimumVersion} or later, but the loaded " +
                $"library reports {version}. Documents are stored with the jsonb() function, which " +
                $"SQLite {MinimumVersion} introduced, so every write would fail with 'no such " +
                "function: jsonb'. Update the SQLitePCLRaw.lib.e_sqlite3 (or bundle_e_sqlite3) " +
                "package reference, or the system SQLite library if the application provides its own.",
                version.ToString(),
                MinimumVersion);
        }

        return version;
    }
}
