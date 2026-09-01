using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Rejects connection strings the store cannot honour: a private in-memory database, which a
/// pool of connections would multiply into one empty database per connection, and WAL mode on
/// an in-memory database, where <c>PRAGMA journal_mode = WAL</c> silently reports back
/// <c>memory</c>.
/// </summary>
/// <remarks>
/// Classification is structural rather than spelling-based: a URI data source is split at its
/// first <c>?</c> and its query parsed the way SQLite parses it — percent-decoded keys and values,
/// the last occurrence of a repeated parameter winning, and the <c>Mode=</c>/<c>Cache=</c>
/// keywords filling in only what the query omits. It is case-sensitive because SQLite is:
/// measured, <c>FILE::memory:</c> and <c>mode=MEMORY</c> do not name an in-memory database at all,
/// they fail to open with SQLite Error 14, so rejecting them here would blame the store for a
/// string SQLite never accepts.
/// </remarks>
internal static class SqliteConnectionStringGuard
{
    /// <summary>
    /// Parses the options' connection string and throws when it cannot be used as configured.
    /// </summary>
    /// <returns>The parsed builder, so callers do not parse twice.</returns>
    /// <exception cref="ArgumentException">
    /// The connection string is empty, names a private in-memory database, or names an in-memory
    /// database while <see cref="DocumentStoreOptions.EnableWalMode"/> is set.
    /// </exception>
    public static SqliteConnectionStringBuilder EnsureUsable(DocumentStoreOptions options, string paramName)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "Connection string must be set before creating a document store.",
                paramName);
        }

        var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
        var shape = Classify(builder);

        // A private in-memory database belongs to a single connection, so a pool of them would
        // give every operation its own empty database. Refuse it rather than silently losing
        // writes; a uniquely named shared-cache database has the same "private" semantics and
        // works across connections. An empty filename is the same failure spelled differently:
        // SQLite gives every connection its own database however shared the cache says it is.
        if (shape.InMemory && (!shape.Shared || shape.EmptyName))
        {
            throw new ArgumentException(
                "A private in-memory database (\":memory:\" or \"file::memory:\", with or without " +
                "Cache=Shared; Mode=Memory without Cache=Shared; or an in-memory URI whose filename " +
                "is empty, as in \"file:?mode=memory&cache=shared\") cannot be used by a document " +
                "store, because the store pools connections and each connection would get its own " +
                "empty database. Use " +
                $"{nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.ForInMemory)}() for a " +
                $"private in-memory store, or {nameof(DocumentStoreOptions.ForSharedInMemory)}(name) " +
                "to share one by name.",
                paramName);
        }

        // SQLite answers PRAGMA journal_mode = WAL with "memory" on an in-memory database: the
        // request is not an error and not honoured either. Left alone it would also make the
        // store run its dispose-time WAL checkpoint against a database that has no WAL.
        if (options.EnableWalMode && shape.InMemory)
        {
            throw new ArgumentException(
                "An in-memory database cannot use WAL mode: SQLite keeps its journal in memory and " +
                $"reports \"memory\" back from PRAGMA journal_mode = WAL. Set " +
                $"{nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.EnableWalMode)} to false, " +
                $"or use {nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.ForInMemory)}()/" +
                $"{nameof(DocumentStoreOptions.ForSharedInMemory)}(name), which already do.",
                paramName);
        }

        return builder;
    }

    /// <summary>
    /// Whether the connection string states a command timeout, under any of the spellings
    /// Microsoft.Data.Sqlite accepts for it.
    /// </summary>
    /// <remarks>
    /// <see cref="SqliteConnectionStringBuilder"/> cannot answer this — it reports every known
    /// keyword as present and returns the provider default (30 s) for the ones the string omits,
    /// so an explicit <c>Default Timeout=30</c> is indistinguishable from silence. The base
    /// <see cref="System.Data.Common.DbConnectionStringBuilder"/> keeps only the keys the string
    /// actually carried.
    /// </remarks>
    public static bool SpecifiesCommandTimeout(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        System.Data.Common.DbConnectionStringBuilder keys;
        try
        {
            keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            // Unparseable: opening the connection will fail on its own terms, and defaulting the
            // timeout here must not turn that into a different exception.
            return true;
        }

        return keys.ContainsKey("Default Timeout")
            || keys.ContainsKey("DefaultTimeout")
            || keys.ContainsKey("Command Timeout")
            || keys.ContainsKey("CommandTimeout");
    }

    /// <summary>What the data source names, once parsed the way SQLite parses it.</summary>
    private readonly record struct ConnectionShape(bool InMemory, bool Shared, bool EmptyName);

    private static ConnectionShape Classify(SqliteConnectionStringBuilder builder)
    {
        const string uriPrefix = "file:";
        var dataSource = builder.DataSource;

        if (!dataSource.StartsWith(uriPrefix, StringComparison.Ordinal))
        {
            // An unadorned ":memory:" is private to its connection whatever the cache setting:
            // SQLite shares an in-memory database only through a URI filename. Measured against
            // Microsoft.Data.Sqlite — "Data Source=:memory:;Cache=Shared" gives a second
            // connection an empty database, while "Data Source=x;Mode=Memory;Cache=Shared" shares
            // one and a missing Data Source leaves the filename empty, which does not.
            var bareMemory = string.Equals(dataSource, ":memory:", StringComparison.Ordinal);
            return new ConnectionShape(
                InMemory: bareMemory || builder.Mode == SqliteOpenMode.Memory,
                Shared: !bareMemory && builder.Cache == SqliteCacheMode.Shared,
                EmptyName: dataSource.Length == 0);
        }

        var uri = dataSource[uriPrefix.Length..];

        // A "#" starts a fragment, and SQLite discards it along with everything after — measured,
        // "file:x?mode=memory#ignored&cache=shared" opens private in-memory, so reading the query
        // past the "#" would see a cache=shared that SQLite never does.
        var fragmentStart = uri.IndexOf('#');
        if (fragmentStart >= 0)
        {
            uri = uri[..fragmentStart];
        }

        var queryStart = uri.IndexOf('?');
        var path = queryStart < 0 ? uri : uri[..queryStart];
        var query = queryStart < 0 ? string.Empty : uri[(queryStart + 1)..];

        string? mode = null;
        string? cache = null;
        foreach (var parameter in query.Split('&'))
        {
            var separator = parameter.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            // SQLite percent-decodes keys and values alike, and takes "+" and a malformed escape
            // literally: measured, "cach%65=shared" and "cache=shar%65d" both share, while
            // "cache=shared+" fails with "no such cache mode: shared+". Uri.UnescapeDataString
            // matches on every one of those, and never throws.
            var key = Uri.UnescapeDataString(parameter[..separator]);
            var value = Uri.UnescapeDataString(parameter[(separator + 1)..]);

            // A repeated parameter takes its last value: measured,
            // "file:x?mode=memory&cache=shared&cache=private" opens private.
            if (string.Equals(key, "mode", StringComparison.Ordinal))
            {
                mode = value;
            }
            else if (string.Equals(key, "cache", StringComparison.Ordinal))
            {
                cache = value;
            }
        }

        // Where the query states a parameter it beats the keyword, and where it omits one the
        // keyword fills in: measured, "file:x?mode=memory&cache=private;Cache=Shared" opens
        // private while "file:x?mode=memory;Cache=Shared" opens shared.
        var inMemory = string.Equals(path, ":memory:", StringComparison.Ordinal)
            || (mode is null
                ? builder.Mode == SqliteOpenMode.Memory
                : string.Equals(mode, "memory", StringComparison.Ordinal));

        var shared = cache is null
            ? builder.Cache == SqliteCacheMode.Shared
            : string.Equals(cache, "shared", StringComparison.Ordinal);

        // A non-empty raw path can still decode to an empty filename, and SQLite then opens a
        // database private to the connection: measured, both "file:%00?mode=memory&cache=shared"
        // and "file:%00x?mode=memory&cache=shared" leave a second connection with no such table.
        var name = Uri.UnescapeDataString(path);
        var nul = name.IndexOf('\0');
        var truncated = nul < 0 ? name : name[..nul];

        return new ConnectionShape(inMemory, shared, truncated.Length == 0);
    }
}
