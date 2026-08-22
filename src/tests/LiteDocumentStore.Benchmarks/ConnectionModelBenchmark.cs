using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore.Benchmarks;

/// <summary>
/// Measures the per-operation cost of the candidate connection models for a document
/// store, isolated from serialization and JSONB work.
///
/// <list type="bullet">
///   <item>Held — one long-lived connection (today's singleton model; not thread-safe).</item>
///   <item>PooledOpen — open/close a connection per operation, relying on
///   Microsoft.Data.Sqlite's built-in pool.</item>
///   <item>PooledOpenWithPragmas — same, plus the per-connection PRAGMA batch that a
///   pooled handle needs because pooling does not preserve session PRAGMAs.</item>
///   <item>CustomPool — rent from an internal pool of connections that were
///   PRAGMA-configured once at creation.</item>
/// </list>
///
/// The parallel benchmarks show what the models cost under concurrency: a gated single
/// connection (the minimal-diff fix) against a real pool.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, iterationCount: 10, warmupCount: 5)]
public class ConnectionModelBenchmark
{
    private const int RowCount = 1_000;
    private const int PoolSize = 8;
    private const int ParallelOperations = 64;

    private const string PragmaBatch =
        "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-2000; PRAGMA busy_timeout=5000;";

    private string _dbPath = null!;
    private string _pooledConnectionString = null!;
    private string _unpooledConnectionString = null!;

    private SqliteConnection _heldConnection = null!;
    private ConcurrentBag<SqliteConnection> _customPool = null!;
    private SemaphoreSlim _poolSlots = null!;
    private SemaphoreSlim _gate = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lds-connmodel-{Guid.NewGuid():N}.db");
        _pooledConnectionString = $"Data Source={_dbPath};Pooling=True;Foreign Keys=True";
        _unpooledConnectionString = $"Data Source={_dbPath};Pooling=False;Foreign Keys=True";

        _heldConnection = new SqliteConnection(_pooledConnectionString);
        await _heldConnection.OpenAsync();
        await ExecuteAsync(_heldConnection, "PRAGMA journal_mode=WAL;");
        await ExecuteAsync(_heldConnection, PragmaBatch);
        await ExecuteAsync(
            _heldConnection,
            "CREATE TABLE IF NOT EXISTS [TestDocument] (id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0);");

        for (var i = 0; i < RowCount; i++)
        {
            using var insert = _heldConnection.CreateCommand();
            insert.CommandText =
                "INSERT OR REPLACE INTO [TestDocument] (id, data, version) VALUES (@id, jsonb(@data), 1);";
            insert.Parameters.AddWithValue("@id", $"doc-{i:D6}");
            insert.Parameters.AddWithValue("@data", $"{{\"Name\":\"Document {i}\",\"Age\":{20 + i % 50}}}");
            await insert.ExecuteNonQueryAsync();
        }

        _gate = new SemaphoreSlim(1, 1);
        _poolSlots = new SemaphoreSlim(PoolSize, PoolSize);
        _customPool = [];
        for (var i = 0; i < PoolSize; i++)
        {
            var connection = new SqliteConnection(_unpooledConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, PragmaBatch);
            _customPool.Add(connection);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        while (_customPool.TryTake(out var pooled))
            pooled.Dispose();

        _heldConnection.Dispose();
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
        _poolSlots.Dispose();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ==================== Single-threaded read ====================

    [Benchmark(Baseline = true, Description = "Held connection (today's singleton)")]
    public Task<string?> Held_Read() => ReadAsync(_heldConnection, "doc-000500");

    [Benchmark(Description = "Pooled open per operation")]
    public async Task<string?> PooledOpen_Read()
    {
        await using var connection = new SqliteConnection(_pooledConnectionString);
        await connection.OpenAsync();
        return await ReadAsync(connection, "doc-000500");
    }

    [Benchmark(Description = "Pooled open per operation + PRAGMA batch")]
    public async Task<string?> PooledOpenWithPragmas_Read()
    {
        await using var connection = new SqliteConnection(_pooledConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, PragmaBatch);
        return await ReadAsync(connection, "doc-000500");
    }

    [Benchmark(Description = "Custom pool rent (PRAGMAs applied once)")]
    public async Task<string?> CustomPool_Read()
    {
        var connection = await RentAsync();
        try
        {
            return await ReadAsync(connection, "doc-000500");
        }
        finally
        {
            Return(connection);
        }
    }

    // ==================== Single-threaded write ====================

    [Benchmark(Description = "Held connection write")]
    public Task<int> Held_Write() => WriteAsync(_heldConnection, "doc-000001");

    [Benchmark(Description = "Pooled open write + PRAGMA batch")]
    public async Task<int> PooledOpenWithPragmas_Write()
    {
        await using var connection = new SqliteConnection(_pooledConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, PragmaBatch);
        return await WriteAsync(connection, "doc-000001");
    }

    [Benchmark(Description = "Custom pool write")]
    public async Task<int> CustomPool_Write()
    {
        var connection = await RentAsync();
        try
        {
            return await WriteAsync(connection, "doc-000001");
        }
        finally
        {
            Return(connection);
        }
    }

    // ==================== Concurrent reads ====================

    [Benchmark(Description = "Gated single connection, 64 concurrent reads")]
    public async Task GatedHeld_ParallelReads()
    {
        var tasks = new Task[ParallelOperations];
        for (var i = 0; i < ParallelOperations; i++)
        {
            var id = $"doc-{i:D6}";
            tasks[i] = Task.Run(async () =>
            {
                await _gate.WaitAsync();
                try
                {
                    await ReadAsync(_heldConnection, id);
                }
                finally
                {
                    _gate.Release();
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark(Description = "Custom pool, 64 concurrent reads")]
    public async Task CustomPool_ParallelReads()
    {
        var tasks = new Task[ParallelOperations];
        for (var i = 0; i < ParallelOperations; i++)
        {
            var id = $"doc-{i:D6}";
            tasks[i] = Task.Run(async () =>
            {
                var connection = await RentAsync();
                try
                {
                    await ReadAsync(connection, id);
                }
                finally
                {
                    Return(connection);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark(Description = "Pooled open + PRAGMA batch, 64 concurrent reads")]
    public async Task PooledOpenWithPragmas_ParallelReads()
    {
        var tasks = new Task[ParallelOperations];
        for (var i = 0; i < ParallelOperations; i++)
        {
            var id = $"doc-{i:D6}";
            tasks[i] = Task.Run(async () =>
            {
                await using var connection = new SqliteConnection(_pooledConnectionString);
                await connection.OpenAsync();
                await ExecuteAsync(connection, PragmaBatch);
                await ReadAsync(connection, id);
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== Helpers ====================

    private async ValueTask<SqliteConnection> RentAsync()
    {
        await _poolSlots.WaitAsync();
        if (_customPool.TryTake(out var connection))
            return connection;

        // Slot was reserved, so the bag is only transiently empty; fall back to a fresh
        // configured connection rather than spinning.
        var created = new SqliteConnection(_unpooledConnectionString);
        await created.OpenAsync();
        await ExecuteAsync(created, PragmaBatch);
        return created;
    }

    private void Return(SqliteConnection connection)
    {
        _customPool.Add(connection);
        _poolSlots.Release();
    }

    private static async Task<string?> ReadAsync(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json(data) FROM [TestDocument] WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetString(0) : null;
    }

    private static async Task<int> WriteAsync(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [TestDocument] (id, data, version) VALUES (@id, jsonb(@data), 1)
            ON CONFLICT(id) DO UPDATE SET data = jsonb(@data), version = version + 1;
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@data", "{\"Name\":\"Updated\",\"Age\":42}");
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Isolates the per-operation cost the pooled connection model adds to the store's own read
/// path, against the model it replaced.
/// </summary>
/// <remarks>
/// <para>
/// "Held" runs <see cref="DocumentOperations"/> directly over one long-lived connection — the
/// shared-connection model, minus its thread-safety bug. "Pooled" runs the same operation
/// through <see cref="IDocumentStore"/>, so the delta is exactly what the model costs: a
/// semaphore wait, a bag pop, the lease, and the closure in <c>DocumentStore.RunAsync</c>.
/// </para>
/// <para>
/// Both are measured on a file database and on a shared-cache in-memory database, because
/// <see cref="DocumentStoreOptions.ForInMemory"/> had to move from a private <c>:memory:</c>
/// database to a shared-cache one — and shared-cache locking is a cost of its own, separate
/// from anything the pool does.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, iterationCount: 10, warmupCount: 5)]
public class StorePathBenchmark
{
    private const int RowCount = 1_000;
    private const string DocumentId = "doc-000500";

    private string _dbPath = null!;
    private IDocumentStore _fileStore = null!;
    private IDocumentStore _memoryStore = null!;
    private SqliteConnection _fileHeldConnection = null!;
    private SqliteConnection _memoryHeldConnection = null!;
    private DocumentOperations _fileHeldOperations;
    private DocumentOperations _memoryHeldOperations;
    private SqliteConnection _privateMemoryConnection = null!;
    private DocumentOperations _privateMemoryOperations;

    [GlobalSetup]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lds-storepath-{Guid.NewGuid():N}.db");
        var factory = new DocumentStoreFactory();
        var connectionFactory = new DefaultConnectionFactory();
        var convention = new DefaultTableNamingConvention();
        var serializerOptions = JsonHelper.CreateDefaultReflectionOptions();

        var fileOptions = DocumentStoreOptions.ForFile(_dbPath);
        _fileStore = await factory.CreateAsync(fileOptions);
        await SeedAsync(_fileStore);
        _fileHeldConnection = await connectionFactory.CreateConnectionAsync(fileOptions);
        _fileHeldOperations = new DocumentOperations(
            _fileHeldConnection, convention, serializerOptions, NullLogger.Instance);

        // One shared-cache in-memory database, reached by both the store and the held connection.
        var memoryOptions = DocumentStoreOptions.ForInMemory();
        _memoryStore = await factory.CreateAsync(memoryOptions);
        await SeedAsync(_memoryStore);
        _memoryHeldConnection = await connectionFactory.CreateConnectionAsync(memoryOptions);
        _memoryHeldOperations = new DocumentOperations(
            _memoryHeldConnection, convention, serializerOptions, NullLogger.Instance);

        // A private :memory: database — what ForInMemory() produced before pooling forced the
        // move to shared cache. Measures the shared-cache tax, with no store involved.
        _privateMemoryConnection = await connectionFactory.CreateConnectionAsync(
            new DocumentStoreOptions("Data Source=:memory:")
            {
                EnableWalMode = false,
                SynchronousMode = SynchronousMode.Off
            });
        _privateMemoryOperations = new DocumentOperations(
            _privateMemoryConnection, convention, serializerOptions, NullLogger.Instance);
        await _privateMemoryOperations.CreateTableAsync<StoreDoc>();
        await _privateMemoryOperations.UpsertManyAsync(Enumerable.Range(0, RowCount)
            .Select(i => ($"doc-{i:D6}", new StoreDoc { Name = $"Document {i}", Age = 20 + (i % 50) })));
    }

    private static async Task SeedAsync(IDocumentStore store)
    {
        await store.CreateTableAsync<StoreDoc>();
        var items = Enumerable.Range(0, RowCount)
            .Select(i => ($"doc-{i:D6}", new StoreDoc { Name = $"Document {i}", Age = 20 + (i % 50) }));
        await store.UpsertManyAsync(items);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _fileHeldConnection.Dispose();
        _memoryHeldConnection.Dispose();
        _privateMemoryConnection.Dispose();
        await _fileStore.DisposeAsync();
        await _memoryStore.DisposeAsync();

        // The held connections were opened straight from the connection factory, so they keep
        // Microsoft.Data.Sqlite's default pooling and their handles outlive Dispose. (The store's
        // own connections do not: its pool forces Pooling=False.)
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Never fail a benchmark run over a leftover temp file.
            }
        }
    }

    [Benchmark(Baseline = true, Description = "File DB: held connection (old model)")]
    public Task<StoreDoc?> File_Held_Get() => _fileHeldOperations.GetAsync<StoreDoc>(DocumentId);

    [Benchmark(Description = "File DB: pooled store (new model)")]
    public Task<StoreDoc?> File_Pooled_Get() => _fileStore.GetAsync<StoreDoc>(DocumentId);

    [Benchmark(Description = "Shared-cache memory DB: held connection (old model)")]
    public Task<StoreDoc?> Memory_Held_Get() => _memoryHeldOperations.GetAsync<StoreDoc>(DocumentId);

    [Benchmark(Description = "Shared-cache memory DB: pooled store (new model)")]
    public Task<StoreDoc?> Memory_Pooled_Get() => _memoryStore.GetAsync<StoreDoc>(DocumentId);

    [Benchmark(Description = "Private :memory: held connection (pre-change in-memory default)")]
    public Task<StoreDoc?> MemoryPrivate_Held_Get() => _privateMemoryOperations.GetAsync<StoreDoc>(DocumentId);

    [Benchmark(Description = "File DB: pooled store upsert")]
    public Task<int> File_Pooled_Upsert() =>
        _fileStore.UpsertAsync("doc-000001", new StoreDoc { Name = "Updated", Age = 42 });

    [Benchmark(Description = "File DB: held connection upsert")]
    public Task<int> File_Held_Upsert() =>
        _fileHeldOperations.UpsertAsync("doc-000001", new StoreDoc { Name = "Updated", Age = 42 });
}

/// <summary>
/// Document type for <see cref="StorePathBenchmark"/>.
/// </summary>
public class StoreDoc
{
    /// <summary>Gets or sets the document name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the document age.</summary>
    public int Age { get; set; }
}
