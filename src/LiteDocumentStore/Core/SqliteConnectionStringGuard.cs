using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Rejects connection strings the store cannot honour: a private in-memory database, which a
/// pool of connections would multiply into one empty database per connection, and WAL mode on
/// an in-memory database, where <c>PRAGMA journal_mode = WAL</c> silently reports back
/// <c>memory</c>.
/// </summary>
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

        // A private in-memory database belongs to a single connection, so a pool of them would
        // give every operation its own empty database. Refuse it rather than silently losing
        // writes; a uniquely named shared-cache database has the same "private" semantics and
        // works across connections.
        if (IsInMemory(builder) && !IsSharedCache(builder))
        {
            throw new ArgumentException(
                "A private in-memory database (\"Data Source=:memory:\" or Mode=Memory without " +
                "Cache=Shared) cannot be used by a document store, because the store pools " +
                "connections and each connection would get its own empty database. Use " +
                $"{nameof(DocumentStoreOptions)}.{nameof(DocumentStoreOptions.ForInMemory)}() for a " +
                $"private in-memory store, or {nameof(DocumentStoreOptions.ForSharedInMemory)}(name) " +
                "to share one by name.",
                paramName);
        }

        // SQLite answers PRAGMA journal_mode = WAL with "memory" on an in-memory database: the
        // request is not an error and not honoured either. Left alone it would also make the
        // store run its dispose-time WAL checkpoint against a database that has no WAL.
        if (options.EnableWalMode && IsInMemory(builder))
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

    private static bool IsInMemory(SqliteConnectionStringBuilder builder)
    {
        if (builder.Mode == SqliteOpenMode.Memory)
        {
            return true;
        }

        var dataSource = builder.DataSource;
        return dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSharedCache(SqliteConnectionStringBuilder builder)
    {
        return builder.Cache == SqliteCacheMode.Shared
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains("cache=shared", StringComparison.OrdinalIgnoreCase));
    }
}
