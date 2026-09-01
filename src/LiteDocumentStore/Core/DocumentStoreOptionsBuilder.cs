using System.Text.Json;

namespace LiteDocumentStore;

/// <summary>
/// Builder for creating DocumentStoreOptions with a fluent API.
/// </summary>
public sealed class DocumentStoreOptionsBuilder
{
    private readonly DocumentStoreOptions _options;

    /// <summary>
    /// Initializes a new instance of DocumentStoreOptionsBuilder.
    /// </summary>
    public DocumentStoreOptionsBuilder()
    {
        _options = new DocumentStoreOptions();
    }

    /// <summary>
    /// Initializes a new instance of DocumentStoreOptionsBuilder with a connection string.
    /// </summary>
    /// <param name="connectionString">The database file path or connection string</param>
    public DocumentStoreOptionsBuilder(string connectionString)
    {
        _options = new DocumentStoreOptions(connectionString);
    }

    /// <summary>
    /// Sets the connection string.
    /// </summary>
    /// <param name="connectionString">The database file path or connection string</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithConnectionString(string connectionString)
    {
        _options.ConnectionString = connectionString;
        return this;
    }

    /// <summary>
    /// Configures the database to use a file-based SQLite database.
    /// </summary>
    /// <param name="filePath">Path to the database file</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder UseFile(string filePath)
    {
        _options.ConnectionString = $"Data Source={filePath}";
        return this;
    }

    /// <summary>
    /// Configures the database to use a private in-memory SQLite database.
    /// Data is lost when the store is disposed.
    /// </summary>
    /// <remarks>
    /// Uses a uniquely named shared-cache database rather than <c>:memory:</c>, so that every
    /// connection in the store's pool sees the same data. See
    /// <see cref="DocumentStoreOptions.ForInMemory"/>.
    /// </remarks>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder UseInMemory()
    {
        var inMemory = DocumentStoreOptions.ForInMemory();
        _options.ConnectionString = inMemory.ConnectionString;
        _options.EnableWalMode = false; // WAL not supported for in-memory
        _options.SynchronousMode = SynchronousMode.Off;
        return this;
    }

    /// <summary>
    /// Configures the database to use a shared in-memory SQLite database.
    /// Multiple connections can access the same in-memory database.
    /// </summary>
    /// <param name="cacheName">Optional name for the shared cache (default: "shared")</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentException">
    /// The name is blank or carries a character that would end the URI filename or the connection
    /// string. See <see cref="DocumentStoreOptions.ForSharedInMemory(string)"/>.
    /// </exception>
    public DocumentStoreOptionsBuilder UseSharedInMemory(string cacheName = "shared")
    {
        var shared = DocumentStoreOptions.ForSharedInMemory(cacheName);
        _options.ConnectionString = shared.ConnectionString;
        _options.EnableWalMode = false; // WAL not supported for in-memory
        _options.SynchronousMode = SynchronousMode.Off;
        return this;
    }

    /// <summary>
    /// Enables or disables Write-Ahead Logging (WAL) mode.
    /// </summary>
    /// <param name="enabled">True to enable WAL mode, false to disable</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithWalMode(bool enabled = true)
    {
        _options.EnableWalMode = enabled;
        return this;
    }

    /// <summary>
    /// Sets the synchronous mode for SQLite.
    /// </summary>
    /// <param name="mode">The synchronous mode (Off, Normal, or Full)</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithSynchronousMode(SynchronousMode mode)
    {
        _options.SynchronousMode = mode;
        return this;
    }

    /// <summary>
    /// Sets the page size in bytes.
    /// Valid values are powers of 2 between 512 and 65536, or 0 to keep the page size the
    /// database already has.
    /// </summary>
    /// <param name="pageSize">The page size in bytes, or 0</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithPageSize(int pageSize)
    {
        if (pageSize != 0 && (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0))
        {
            throw new ArgumentException(
                "Page size must be a power of 2 between 512 and 65536, or 0 to keep the database's own page size.",
                nameof(pageSize));
        }
        _options.PageSize = pageSize;
        return this;
    }

    /// <summary>
    /// Sets the cache size in number of pages or kilobytes.
    /// </summary>
    /// <param name="cacheSize">
    /// Positive values specify number of pages.
    /// Negative values specify kilobytes (e.g., -2000 = 2MB).
    /// </param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithCacheSize(int cacheSize)
    {
        _options.CacheSize = cacheSize;
        return this;
    }

    /// <summary>
    /// Sets the cache size in megabytes.
    /// </summary>
    /// <param name="cacheSizeMb">Cache size in megabytes</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithCacheSizeMb(int cacheSizeMb)
    {
        _options.CacheSize = -cacheSizeMb * 1024;
        return this;
    }

    /// <summary>
    /// Sets the busy timeout in milliseconds. See
    /// <see cref="DocumentStoreOptions.BusyTimeoutMs"/> — it also caps Microsoft.Data.Sqlite's own
    /// retry loop, unless the connection string states a command timeout.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds (must not be negative)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeoutMs"/> is negative.
    /// </exception>
    public DocumentStoreOptionsBuilder WithBusyTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);

        _options.BusyTimeoutMs = timeoutMs;
        return this;
    }

    /// <summary>
    /// Enables or disables foreign key constraints.
    /// </summary>
    /// <param name="enabled">True to enable foreign keys, false to disable</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithForeignKeys(bool enabled = true)
    {
        _options.EnableForeignKeys = enabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of connections the store keeps open.
    /// </summary>
    /// <param name="maxPoolSize">Maximum pooled connections (must be at least 1)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxPoolSize"/> is less than 1.
    /// </exception>
    public DocumentStoreOptionsBuilder WithMaxPoolSize(int maxPoolSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoolSize, 1);

        _options.MaxPoolSize = maxPoolSize;
        return this;
    }

    /// <summary>
    /// Sets how long an operation waits for a free pooled connection.
    /// </summary>
    /// <param name="timeoutMs">
    /// Milliseconds to wait, or <see cref="Timeout.Infinite"/> (-1) to wait forever
    /// </param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeoutMs"/> is 0, or negative other than
    /// <see cref="Timeout.Infinite"/>.
    /// </exception>
    public DocumentStoreOptionsBuilder WithPoolWaitTimeout(int timeoutMs)
    {
        // The property setter validates too; doing it here names the builder's own parameter.
        if (timeoutMs is 0 or < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMs),
                timeoutMs,
                "Pool wait timeout must be positive, or -1 to wait forever.");
        }

        _options.PoolWaitTimeoutMs = timeoutMs;
        return this;
    }

    /// <summary>
    /// Sets the table naming convention.
    /// </summary>
    /// <param name="convention">The table naming convention to use</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithTableNamingConvention(ITableNamingConvention convention)
    {
        _options.TableNamingConvention = convention;
        return this;
    }

    /// <summary>
    /// Sets the <see cref="JsonSerializerOptions"/> used to (de)serialize documents.
    /// For Native AOT / trimming, back these options with a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>, e.g.
    /// <c>new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }</c>.
    /// </summary>
    /// <param name="serializerOptions">The serializer options to use</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder WithSerializerOptions(JsonSerializerOptions serializerOptions)
    {
        _options.SerializerOptions = serializerOptions;
        return this;
    }

    /// <summary>
    /// Adds a custom PRAGMA statement to execute on connection open.
    /// </summary>
    /// <param name="pragma">The PRAGMA statement (e.g., "PRAGMA temp_store = MEMORY")</param>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder AddPragma(string pragma)
    {
        if (!string.IsNullOrWhiteSpace(pragma))
        {
            _options.AdditionalPragmas.Add(pragma);
        }
        return this;
    }

    /// <summary>
    /// Configures options for high-performance scenarios.
    /// Sets: WAL mode, synchronous=NORMAL, larger cache, optimized page size.
    /// </summary>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder OptimizeForPerformance()
    {
        _options.EnableWalMode = true;
        _options.SynchronousMode = SynchronousMode.Normal;
        _options.PageSize = 8192; // Larger page size for better throughput
        _options.CacheSize = -4000; // 4MB cache
        return this;
    }

    /// <summary>
    /// Configures options for maximum durability and data safety.
    /// Sets: WAL mode, synchronous=FULL, foreign keys enabled.
    /// </summary>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder OptimizeForSafety()
    {
        _options.EnableWalMode = true;
        _options.SynchronousMode = SynchronousMode.Full;
        _options.EnableForeignKeys = true;
        return this;
    }

    /// <summary>
    /// Configures options for development/testing scenarios.
    /// Sets: In-memory database, no WAL, synchronous=OFF for maximum speed.
    /// </summary>
    /// <returns>This builder for method chaining</returns>
    public DocumentStoreOptionsBuilder OptimizeForTesting()
    {
        UseInMemory();
        return this;
    }

    /// <summary>
    /// Builds the DocumentStoreOptions instance.
    /// </summary>
    /// <returns>The configured DocumentStoreOptions</returns>
    public DocumentStoreOptions Build()
    {
        if (string.IsNullOrEmpty(_options.ConnectionString))
        {
            throw new InvalidOperationException("Connection string must be set before building options.");
        }
        if (_options.MaxPoolSize < 1)
        {
            throw new InvalidOperationException("Max pool size must be at least 1.");
        }

        // The same check the factory runs, so a builder cannot produce options a store then
        // refuses.
        _options.Validate();

        return _options.Clone();
    }
}
