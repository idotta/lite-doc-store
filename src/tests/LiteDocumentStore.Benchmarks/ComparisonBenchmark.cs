using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Dapper;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LiteDocumentStore.Benchmarks;

/// <summary>
/// Comprehensive comparison benchmark: LiteDocumentStore vs Raw Dapper vs LiteDB.
/// Measures: single insert, bulk insert, query by ID, full-table scan.
/// All using in-memory databases for fair comparison.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, iterationCount: 15)]
public class ComparisonBenchmark
{
    // Test data sizes
    private const int SingleOperationCount = 100;
    private const int BulkOperationCount = 1000;
    private const int ScanCount = 1000;

    // LiteDocumentStore
    private IDocumentStore _documentStore = null!;
    private ServiceProvider _serviceProvider = null!;

    // Raw Dapper with SQLite
    private SqliteConnection _dapperConnection = null!;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // LiteDB
    private LiteDatabase _liteDb = null!;
    private ILiteCollection<TestDocument> _liteDbCollection = null!;

    // Test data
    private List<TestDocument> _testDocuments = null!;
    private List<string> _testIds = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        // Generate test data
        _testDocuments = GenerateTestDocuments(BulkOperationCount);
        _testIds = _testDocuments.Select(d => d.Id).ToList();

        // Setup LiteDocumentStore
        await SetupLiteDocumentStore();

        // Setup Raw Dapper
        await SetupRawDapper();

        // Setup LiteDB
        SetupLiteDB();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_documentStore != null)
            await _documentStore.DisposeAsync();

        _serviceProvider?.Dispose();

        _dapperConnection?.Dispose();
        _liteDb?.Dispose();
    }

    private async Task SetupLiteDocumentStore()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = "Data Source=:memory:";
            options.EnableWalMode = false; // WAL not supported in :memory:
        });

        _serviceProvider = services.BuildServiceProvider();
        _documentStore = _serviceProvider.GetRequiredService<IDocumentStore>();
        await _documentStore.CreateTableAsync<TestDocument>();
    }

    private async Task SetupRawDapper()
    {
        _dapperConnection = new SqliteConnection("Data Source=:memory:");
        await _dapperConnection.OpenAsync();

        // Create table manually
        var createTable = @"
            CREATE TABLE [TestDocument] (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL
            )";
        await _dapperConnection.ExecuteAsync(createTable);
    }

    private void SetupLiteDB()
    {
        _liteDb = new LiteDatabase(":memory:");
        _liteDbCollection = _liteDb.GetCollection<TestDocument>("testdocuments");
    }

    private static List<TestDocument> GenerateTestDocuments(int count)
    {
        var documents = new List<TestDocument>();
        for (int i = 0; i < count; i++)
        {
            documents.Add(new TestDocument
            {
                Id = $"doc-{i:D6}",
                Name = $"Document {i}",
                Email = $"user{i}@example.com",
                Age = 20 + (i % 50),
                IsActive = i % 2 == 0,
                Category = $"Category {i % 10}",
                Tags = [$"tag{i % 5}", $"tag{i % 7}"],
                CreatedAt = DateTime.UtcNow.AddDays(-i),
                Score = 100.0 + (i % 100) * 1.5,
                Metadata = new Dictionary<string, string>
                {
                    ["Key1"] = $"Value{i}",
                    ["Key2"] = $"Data{i % 20}",
                    ["Status"] = i % 3 == 0 ? "Active" : "Inactive"
                }
            });
        }
        return documents;
    }

    // ==================== Single Insert Benchmarks ====================

    [Benchmark(Baseline = true)]
    [Arguments(0)]
    [Arguments(50)]
    public async Task LiteDocumentStore_SingleInsert(int index)
    {
        var doc = _testDocuments[index];
        await _documentStore.UpsertAsync(doc.Id, doc);
    }

    [Benchmark]
    [Arguments(0)]
    [Arguments(50)]
    public async Task RawDapper_SingleInsert(int index)
    {
        var doc = _testDocuments[index];
        var json = System.Text.Json.JsonSerializer.Serialize(doc, _jsonOptions);

        var sql = "INSERT OR REPLACE INTO [TestDocument] (id, data) VALUES (@Id, jsonb(@Data))";
        await _dapperConnection.ExecuteAsync(sql, new { Id = doc.Id, Data = json });
    }

    [Benchmark]
    [Arguments(0)]
    [Arguments(50)]
    public void LiteDB_SingleInsert(int index)
    {
        var doc = _testDocuments[index];
        _liteDbCollection.Upsert(doc);
    }

    // ==================== Bulk Insert Benchmarks ====================

    [Benchmark]
    public async Task LiteDocumentStore_BulkInsert()
    {
        // Take first 100 documents for bulk operation
        var batch = _testDocuments.Take(100).Select(d => (d.Id, d)).ToList();
        await _documentStore.UpsertManyAsync(batch);
    }

    [Benchmark]
    public async Task RawDapper_BulkInsert()
    {
        // Take first 100 documents for bulk operation
        var batch = _testDocuments.Take(100).ToList();

        using var transaction = _dapperConnection.BeginTransaction();
        try
        {
            foreach (var doc in batch)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(doc, _jsonOptions);
                var sql = "INSERT OR REPLACE INTO [TestDocument] (id, data) VALUES (@Id, jsonb(@Data))";
                await _dapperConnection.ExecuteAsync(sql, new { Id = doc.Id, Data = json }, transaction);
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    [Benchmark]
    public void LiteDB_BulkInsert()
    {
        // Take first 100 documents for bulk operation
        var batch = _testDocuments.Take(100).ToList();
        _liteDbCollection.Upsert(batch);
    }

    // ==================== Query By ID Benchmarks ====================

    [Benchmark]
    [Arguments("doc-000000")]
    [Arguments("doc-000050")]
    public async Task<TestDocument?> LiteDocumentStore_QueryById(string id)
    {
        return await _documentStore.GetAsync<TestDocument>(id);
    }

    [Benchmark]
    [Arguments("doc-000000")]
    [Arguments("doc-000050")]
    public async Task<TestDocument?> RawDapper_QueryById(string id)
    {
        var sql = "SELECT json(data) as data FROM [TestDocument] WHERE id = @Id";
        var json = await _dapperConnection.QuerySingleOrDefaultAsync<string>(sql, new { Id = id });

        return json != null
            ? System.Text.Json.JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)
            : null;
    }

    [Benchmark]
    [Arguments("doc-000000")]
    [Arguments("doc-000050")]
    public TestDocument? LiteDB_QueryById(string id)
    {
        return _liteDbCollection.FindById(id);
    }

    // ==================== Full Table Scan Benchmarks ====================

    [Benchmark]
    public async Task<int> LiteDocumentStore_FullScan()
    {
        var results = await _documentStore.GetAllAsync<TestDocument>();
        return results.Count();
    }

    [Benchmark]
    public async Task<int> RawDapper_FullScan()
    {
        var sql = "SELECT json(data) as data FROM [TestDocument]";
        var jsonResults = await _dapperConnection.QueryAsync<string>(sql);

        var results = jsonResults.Select(json =>
            System.Text.Json.JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)!
        ).ToList();

        return results.Count;
    }

    [Benchmark]
    public int LiteDB_FullScan()
    {
        var results = _liteDbCollection.FindAll().ToList();
        return results.Count;
    }

    // ==================== Query with Filter Benchmarks ====================

    [Benchmark]
    public async Task<int> LiteDocumentStore_QueryByCategory()
    {
        var results = await _documentStore.QueryAsync<TestDocument, string>("$.Category", "Category 5");
        return results.Count();
    }

    [Benchmark]
    public async Task<int> RawDapper_QueryByCategory()
    {
        var sql = @"
            SELECT json(data) as data 
            FROM [TestDocument] 
            WHERE json_extract(data, '$.Category') = @Category";

        var jsonResults = await _dapperConnection.QueryAsync<string>(sql, new { Category = "Category 5" });

        var results = jsonResults.Select(json =>
            System.Text.Json.JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)!
        ).ToList();

        return results.Count;
    }

    [Benchmark]
    public int LiteDB_QueryByCategory()
    {
        var results = _liteDbCollection.Find(d => d.Category == "Category 5").ToList();
        return results.Count;
    }

    // ==================== Delete Benchmarks ====================

    [Benchmark]
    [Arguments("doc-000099")]
    public async Task<bool> LiteDocumentStore_Delete(string id)
    {
        return await _documentStore.DeleteAsync<TestDocument>(id);
    }

    [Benchmark]
    [Arguments("doc-000099")]
    public async Task<int> RawDapper_Delete(string id)
    {
        var sql = "DELETE FROM [TestDocument] WHERE id = @Id";
        return await _dapperConnection.ExecuteAsync(sql, new { Id = id });
    }

    [Benchmark]
    [Arguments("doc-000099")]
    public bool LiteDB_Delete(string id)
    {
        return _liteDbCollection.Delete(id);
    }
}

/// <summary>
/// Test document class for benchmarking.
/// </summary>
public class TestDocument
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    public int Age { get; set; }
    public bool IsActive { get; set; }
    public string Category { get; set; } = null!;
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public double Score { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
