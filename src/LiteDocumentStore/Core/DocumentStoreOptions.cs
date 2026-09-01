using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDocumentStore;

/// <summary>
/// Configuration options for DocumentStore repository behavior and SQLite performance settings.
/// </summary>
public sealed class DocumentStoreOptions
{
    /// <summary>
    /// Gets or sets the database file path or connection string.
    /// </summary>
    /// <remarks>
    /// A <em>private</em> in-memory database (<c>":memory:"</c>, or <c>Mode=Memory</c> without
    /// <c>Cache=Shared</c>) is rejected: each pooled connection would get its own empty copy.
    /// Use <see cref="ForInMemory"/> or <see cref="ForSharedInMemory"/> instead.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to enable Write-Ahead Logging (WAL) mode.
    /// WAL mode significantly improves write performance and concurrency.
    /// Default is true.
    /// </summary>
    public bool EnableWalMode { get; set; } = true;

    /// <summary>
    /// Gets or sets the synchronous mode for SQLite.
    /// Options: FULL (safest, slowest), NORMAL (balanced), OFF (fastest, risky).
    /// Default is NORMAL for optimal performance with reasonable durability.
    /// </summary>
    public SynchronousMode SynchronousMode { get; set; } = SynchronousMode.Normal;

    /// <summary>
    /// Gets or sets the page size in bytes. Valid values are powers of 2 between 512 and 65536,
    /// or 0 to keep whatever page size the database already has.
    /// Default is 4096. Larger values may improve performance for large datasets.
    /// </summary>
    /// <remarks>
    /// SQLite ignores <c>PRAGMA page_size</c> on a database that already has pages, so opening an
    /// existing database whose page size differs throws
    /// <see cref="Exceptions.IncompatiblePageSizeException"/> instead of running as if the option
    /// had been applied. Set this to 0 when the store has to open databases created elsewhere.
    /// </remarks>
    public int PageSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the cache size in number of pages.
    /// Negative values interpret as kilobytes (e.g., -2000 = 2MB).
    /// Default is -2000 (2MB).
    /// </summary>
    public int CacheSize { get; set; } = -2000;

    /// <summary>
    /// Gets or sets the busy timeout in milliseconds.
    /// How long to wait when the database is locked before returning SQLITE_BUSY.
    /// Default is 5000ms (5 seconds).
    /// </summary>
    /// <remarks>
    /// Applied as <c>PRAGMA busy_timeout</c> and, because that only bounds SQLite's busy handler
    /// within a single attempt, also as the connection's command timeout — Microsoft.Data.Sqlite
    /// otherwise retries a contended statement for its own 30 s default, making this value a floor
    /// instead of the bound. A command timeout stated in the connection string
    /// (<c>Default Timeout</c> / <c>Command Timeout</c>) is left alone and then governs instead.
    /// A custom <see cref="IConnectionFactory"/> applies both itself.
    /// <para>
    /// That retry loop is second-granular, and 0 means "retry forever" to it, so the store floors
    /// the command timeout at one second: a value below 1000 — including 0 — still waits about a
    /// second before <c>SQLITE_BUSY</c> surfaces. "Fail immediately on a locked database" is not
    /// expressible through the provider.
    /// </para>
    /// </remarks>
    public int BusyTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets whether to enable foreign key constraints.
    /// Default is true.
    /// </summary>
    public bool EnableForeignKeys { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of SQLite connections the store opens for operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store rents a connection per operation from an internal pool, so this caps how
    /// many operations can touch the database concurrently; further callers wait for a free
    /// connection. SQLite serializes writers regardless of this value, so raising it helps
    /// read concurrency (in WAL mode) more than write throughput. Default is the processor
    /// count, clamped to [2, 16].
    /// </para>
    /// <para>
    /// Blob read streams from <see cref="IDocumentStore.OpenBlobReadAsync"/> are counted
    /// <em>separately</em>: each holds its own connection outside this pool until disposed, so
    /// that a caller who forgets to dispose one cannot starve ordinary operations. This value
    /// bounds that count too, as a second budget of the same size — so a store may hold up to
    /// twice this many connections when every blob stream slot is in use.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1</exception>
    public int MaxPoolSize
    {
        get;
        // Validated here too, not just in the builder: SemaphoreSlim's own exception names
        // "maxCount" and never mentions which option was wrong.
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>
    /// Gets or sets how long an operation waits for a free pooled connection before the wait is
    /// treated as exhaustion rather than contention, in milliseconds. Default is 30000ms
    /// (30 seconds); <see cref="Timeout.Infinite"/> (-1) waits forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A slot is held for one operation, or — for a transaction — until it is committed, rolled
    /// back or disposed. A caller who leaks an <see cref="IDocumentTransaction"/> without
    /// disposing it holds a slot until that transaction is finalized, and <see cref="MaxPoolSize"/>
    /// such leaks would otherwise hang every later operation forever. Past this bound the rent
    /// throws <see cref="TimeoutException"/> instead, naming the cap.
    /// </para>
    /// <para>
    /// This bounds the wait for a <em>connection</em>, not for a SQLite lock — that is
    /// <see cref="BusyTimeoutMs"/>. Raise it for a workload whose operations legitimately queue
    /// longer than this behind <see cref="MaxPoolSize"/> concurrent ones; set it to
    /// <see cref="Timeout.Infinite"/> to queue indefinitely, which cannot report a leak.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is 0, or negative other than <see cref="Timeout.Infinite"/>
    /// </exception>
    public int PoolWaitTimeoutMs
    {
        get;
        // Validated here too, for MaxPoolSize's reason: SemaphoreSlim's own exception names
        // "millisecondsTimeout" and never mentions which option was wrong.
        set
        {
            if (value is 0 or < Timeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Pool wait timeout must be positive, or -1 to wait forever.");
            }

            field = value;
        }
    } = 30_000;

    /// <summary>
    /// Gets or sets the default table naming convention.
    /// If null, <see cref="DefaultTableNamingConvention"/> is used: the type's namespace-qualified
    /// name with every separator folded to an underscore, so <c>MyApp.Sales.Order</c> becomes
    /// <c>MyApp_Sales_Order</c>.
    /// </summary>
    /// <remarks>
    /// Whichever convention is used, a store refuses to serve two different types that resolve to the
    /// same table name — sharing one table makes each type's writes overwrite the other's rows.
    /// </remarks>
    public ITableNamingConvention? TableNamingConvention { get; set; }

    /// <summary>
    /// Gets or sets additional PRAGMA statements to execute on connection open.
    /// Useful for custom SQLite configuration.
    /// </summary>
    public List<string> AdditionalPragmas { get; set; } = [];

    /// <summary>
    /// Gets or sets the <see cref="JsonSerializerOptions"/> used to (de)serialize documents.
    /// For Native AOT / trimming, set this to options backed by a source-generated
    /// <see cref="JsonSerializerContext"/>, e.g.
    /// <c>new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }</c>.
    /// When null (the default), the store falls back to reflection-based serialization,
    /// which works only in non-AOT scenarios.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Creates a new instance of DocumentStoreOptions with default settings.
    /// </summary>
    public DocumentStoreOptions()
    {
    }

    /// <summary>
    /// Creates a new instance of DocumentStoreOptions with the specified connection string.
    /// </summary>
    /// <param name="connectionString">The database file path or connection string</param>
    public DocumentStoreOptions(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Creates a builder for configuring DocumentStoreOptions with a fluent API.
    /// </summary>
    /// <param name="connectionString">The database file path or connection string</param>
    /// <returns>A new DocumentStoreOptionsBuilder instance</returns>
    public static DocumentStoreOptionsBuilder Builder(string connectionString)
    {
        return new DocumentStoreOptionsBuilder(connectionString);
    }

    /// <summary>
    /// Creates a builder for configuring DocumentStoreOptions with a fluent API.
    /// </summary>
    /// <returns>A new DocumentStoreOptionsBuilder instance</returns>
    public static DocumentStoreOptionsBuilder Builder()
    {
        return new DocumentStoreOptionsBuilder();
    }

    /// <summary>
    /// Creates options for a file-based SQLite database with optimized settings.
    /// </summary>
    /// <param name="filePath">Path to the database file</param>
    /// <returns>DocumentStoreOptions configured for file-based storage</returns>
    public static DocumentStoreOptions ForFile(string filePath)
    {
        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={filePath}",
            EnableWalMode = true,
            SynchronousMode = SynchronousMode.Normal
        };
    }

    /// <summary>
    /// Creates options for a private in-memory SQLite database.
    /// Data is lost when the store is disposed.
    /// </summary>
    /// <remarks>
    /// The store pools connections, and a plain <c>Data Source=:memory:</c> gives every
    /// connection its own private database — the second operation would not see the first
    /// one's writes. So this uses a uniquely named shared-cache in-memory database instead:
    /// private to this set of options, but visible to every connection in the store's pool.
    /// Use <see cref="ForSharedInMemory(string)"/> when several stores must share one
    /// in-memory database by name.
    /// </remarks>
    /// <returns>DocumentStoreOptions configured for in-memory storage</returns>
    public static DocumentStoreOptions ForInMemory()
    {
        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source=file:lds-{Guid.NewGuid():N}?mode=memory&cache=shared",
            EnableWalMode = false, // WAL not supported for in-memory
            SynchronousMode = SynchronousMode.Off // Maximum performance for in-memory
        };
    }

    /// <summary>
    /// Creates options for a shared in-memory SQLite database.
    /// Multiple connections can access the same in-memory database.
    /// </summary>
    /// <param name="cacheName">Optional name for the shared cache (default: "shared")</param>
    /// <returns>DocumentStoreOptions configured for shared in-memory storage</returns>
    /// <exception cref="ArgumentException">
    /// The name is blank, or carries a character that would end the URI filename or the connection
    /// string itself: <c>;</c>, <c>?</c>, <c>&amp;</c> or <c>#</c>.
    /// </exception>
    public static DocumentStoreOptions ForSharedInMemory(string cacheName = "shared")
    {
        // The name is interpolated into a URI filename inside a connection string, so a character
        // that terminates either one silently changes what is opened. Measured:
        // ForSharedInMemory("x;Data Source=evil.db") appended a second Data Source keyword — the
        // last one wins — and created an on-disk file with WAL honoured, and a "#" fragment
        // likewise opened a file database. A blank name leaves the filename empty, which SQLite
        // opens as a database private to each connection.
        if (string.IsNullOrWhiteSpace(cacheName) || cacheName.AsSpan().IndexOfAny(";?&#") >= 0)
        {
            throw new ArgumentException(
                "The shared in-memory cache name must be non-blank and must not contain ';', '?', " +
                $"'&' or '#', because it is used as a URI filename, but was \"{cacheName}\".",
                nameof(cacheName));
        }

        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source=file:{cacheName}?mode=memory&cache=shared",
            EnableWalMode = false, // WAL not supported for in-memory
            SynchronousMode = SynchronousMode.Off
        };
    }

    /// <summary>
    /// Throws when these options cannot be used to open a store.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="DocumentStoreFactory"/>, the path every store goes through including
    /// the DI registration. Options built by hand rather than through
    /// <see cref="DocumentStoreOptionsBuilder"/> are otherwise never checked, and a negative
    /// <see cref="BusyTimeoutMs"/> or a non-power-of-2 <see cref="PageSize"/> reaches SQLite as a
    /// PRAGMA that quietly does nothing.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An option is outside its valid range, or the connection string names a database the store
    /// cannot use as configured (a private in-memory database, or an in-memory database with
    /// <see cref="EnableWalMode"/> set). The exception names the offending option.
    /// </exception>
    public void Validate()
    {
        // Runs the connection-string checks the pool would make later too, so a misconfigured
        // store fails at creation rather than when the first connection opens.
        SqliteConnectionStringGuard.EnsureUsable(this, nameof(ConnectionString));

        if (PageSize != 0 && (PageSize < 512 || PageSize > 65536 || (PageSize & (PageSize - 1)) != 0))
        {
            throw new ArgumentException(
                "Page size must be a power of 2 between 512 and 65536, or 0 to keep the database's " +
                $"own page size, but was {PageSize}.",
                nameof(PageSize));
        }

        if (BusyTimeoutMs < 0)
        {
            throw new ArgumentException(
                $"Busy timeout must not be negative, but was {BusyTimeoutMs}.",
                nameof(BusyTimeoutMs));
        }

        if (MaxPoolSize < 1)
        {
            throw new ArgumentException(
                $"Max pool size must be at least 1, but was {MaxPoolSize}.",
                nameof(MaxPoolSize));
        }

        if (PoolWaitTimeoutMs is 0 or < Timeout.Infinite)
        {
            throw new ArgumentException(
                "Pool wait timeout must be positive, or -1 to wait forever, but was " +
                $"{PoolWaitTimeoutMs}.",
                nameof(PoolWaitTimeoutMs));
        }

        if (AdditionalPragmas is null)
        {
            throw new ArgumentException("Additional pragmas must not be null.", nameof(AdditionalPragmas));
        }

        for (var i = 0; i < AdditionalPragmas.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(AdditionalPragmas[i]))
            {
                throw new ArgumentException(
                    $"Additional pragma at index {i} is null or blank.",
                    nameof(AdditionalPragmas));
            }
        }
    }

    /// <summary>
    /// Creates a copy of the current options.
    /// </summary>
    /// <remarks>
    /// <see cref="SerializerOptions"/> is shared by reference intentionally: the instance carries
    /// the source-generated <c>TypeInfoResolver</c> and its metadata cache, which must be shared for
    /// AOT correctness and performance. System.Text.Json also makes a <see cref="JsonSerializerOptions"/>
    /// read-only after its first use, so the shared instance is effectively immutable in practice.
    /// </remarks>
    /// <returns>A new DocumentStoreOptions instance with copied values</returns>
    public DocumentStoreOptions Clone()
    {
        return new DocumentStoreOptions
        {
            ConnectionString = ConnectionString,
            EnableWalMode = EnableWalMode,
            SynchronousMode = SynchronousMode,
            PageSize = PageSize,
            CacheSize = CacheSize,
            BusyTimeoutMs = BusyTimeoutMs,
            EnableForeignKeys = EnableForeignKeys,
            MaxPoolSize = MaxPoolSize,
            PoolWaitTimeoutMs = PoolWaitTimeoutMs,
            TableNamingConvention = TableNamingConvention,
            AdditionalPragmas = [.. AdditionalPragmas],
            SerializerOptions = SerializerOptions
        };
    }
}

/// <summary>
/// SQLite synchronous mode settings.
/// </summary>
public enum SynchronousMode
{
    /// <summary>
    /// Fastest, but risky. Data may be corrupted on power loss or OS crash.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Balanced performance and durability. Good for most applications.
    /// In WAL mode, only syncs on checkpoint (safe for application crashes).
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Safest, but slowest. Guarantees data integrity even on power loss.
    /// </summary>
    Full = 2
}
