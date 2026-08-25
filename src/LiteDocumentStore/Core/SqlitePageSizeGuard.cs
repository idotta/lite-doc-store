using System.Globalization;
using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Verifies that <see cref="DocumentStoreOptions.PageSize"/> is the page size the database
/// actually has, once the PRAGMAs have been applied to a freshly opened connection.
/// </summary>
/// <remarks>
/// <para>
/// <c>PRAGMA page_size = N</c> is not an error on a database that has already been written — it
/// is simply ignored, so the option looks applied and is not. Converting an existing database
/// takes a <c>VACUUM</c>, and that only works while the database is not in WAL mode (measured:
/// in WAL mode the VACUUM runs and leaves the page size unchanged). The store therefore reads
/// the value back and refuses the mismatch rather than running on a database configured
/// differently from what the caller asked for.
/// </para>
/// <para>
/// Like <see cref="SqliteVersionGuard"/>, the check runs in <see cref="SqliteConnectionPool"/>
/// rather than in <see cref="DefaultConnectionFactory"/>, because <see cref="IConnectionFactory"/>
/// is public and a consumer-supplied factory would otherwise open unchecked connections.
/// </para>
/// </remarks>
internal static class SqlitePageSizeGuard
{
    /// <summary>
    /// Reads <c>PRAGMA page_size</c> back from an open connection and throws when it differs
    /// from the requested size. A requested size of 0 means "accept the database's own page
    /// size" and skips the query entirely.
    /// </summary>
    /// <exception cref="IncompatiblePageSizeException">
    /// The database reports a different page size than the options asked for.
    /// </exception>
    public static async Task EnsureAppliedAsync(
        SqliteConnection connection,
        int requestedPageSize,
        CancellationToken cancellationToken = default)
    {
        if (requestedPageSize == 0)
        {
            return;
        }

        var actual = await connection
            .QueryFirstStringAsync("PRAGMA page_size;", cancellationToken)
            .ConfigureAwait(false);

        Validate(requestedPageSize, actual);
    }

    /// <inheritdoc cref="EnsureAppliedAsync" />
    public static void EnsureApplied(SqliteConnection connection, int requestedPageSize)
    {
        if (requestedPageSize == 0)
        {
            return;
        }

        Validate(requestedPageSize, connection.QueryFirstString("PRAGMA page_size;"));
    }

    /// <summary>
    /// Compares a requested page size against the value <c>PRAGMA page_size</c> reported.
    /// </summary>
    /// <remarks>
    /// Internal so the message and the parse path can be tested without a database whose page
    /// size happens to differ.
    /// </remarks>
    internal static void Validate(int requestedPageSize, string? reported)
    {
        if (!int.TryParse(reported, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual))
        {
            throw new IncompatiblePageSizeException(
                $"Could not determine the database page size (PRAGMA page_size returned " +
                $"'{reported ?? "<null>"}'), so {nameof(DocumentStoreOptions)}." +
                $"{nameof(DocumentStoreOptions.PageSize)} = {requestedPageSize} could not be verified.",
                requestedPageSize,
                0);
        }

        if (actual == requestedPageSize)
        {
            return;
        }

        throw new IncompatiblePageSizeException(
            $"{nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.PageSize)} = " +
            $"{requestedPageSize} was not applied: the database reports a page size of {actual}. " +
            "SQLite ignores PRAGMA page_size on a database that already has pages, and only a " +
            "VACUUM outside WAL mode can convert one. Either set PageSize to " +
            $"{actual} to match this database, set it to 0 to accept whatever page size the " +
            "database has, or VACUUM the database after changing it.",
            requestedPageSize,
            actual);
    }
}
