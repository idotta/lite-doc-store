using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LiteDocumentStore.Benchmarks;

/// <summary>
/// Simplified comparison: LiteDocumentStore vs Raw Dapper.
/// Fast execution for quick iterations and adjustments.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, iterationCount: 5, warmupCount: 3)]
public class SimplifiedComparisonBenchmark
{
    private const int BulkOperationCount = 100;

    // LiteDocumentStore
    private IDocumentStore _documentStore = null!;
    private ServiceProvider _serviceProvider = null!;

    // Raw Dapper with SQLite
    private SqliteConnection _dapperConnection = null!;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Test data
    private List<TestDocument> _testDocuments = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _testDocuments = GenerateTestDocuments(BulkOperationCount);
        await SetupLiteDocumentStore();
        await SetupRawDapper();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_documentStore != null)
            await _documentStore.DisposeAsync();

        _serviceProvider?.Dispose();
        _dapperConnection?.Dispose();
    }

    private async Task SetupLiteDocumentStore()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = "Data Source=:memory:";
            options.EnableWalMode = false;
        });

        _serviceProvider = services.BuildServiceProvider();
        _documentStore = _serviceProvider.GetRequiredService<IDocumentStore>();
        await _documentStore.CreateTableAsync<TestDocument>();
    }

    private async Task SetupRawDapper()
    {
        _dapperConnection = new SqliteConnection("Data Source=:memory:");
        await _dapperConnection.OpenAsync();

        var createTable = @"
            CREATE TABLE [TestDocument] (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL
            )";
        await _dapperConnection.ExecuteAsync(createTable);
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

    // ==================== Single Insert ====================

    [Benchmark(Baseline = true)]
    public async Task LiteDocumentStore_SingleInsert()
    {
        var doc = _testDocuments[0];
        await _documentStore.UpsertAsync(doc.Id, doc);
    }

    [Benchmark]
    public async Task RawDapper_SingleInsert()
    {
        var doc = _testDocuments[0];
        var json = JsonSerializer.Serialize(doc, _jsonOptions);
        var sql = "INSERT OR REPLACE INTO [TestDocument] (id, data) VALUES (@Id, jsonb(@Data))";
        await _dapperConnection.ExecuteAsync(sql, new { Id = doc.Id, Data = json });
    }

    // ==================== Bulk Insert ====================

    [Benchmark]
    public async Task LiteDocumentStore_BulkInsert()
    {
        var batch = _testDocuments.Take(50).Select(d => (d.Id, d)).ToList();
        await _documentStore.UpsertManyAsync(batch);
    }

    [Benchmark]
    public async Task RawDapper_BulkInsert()
    {
        var batch = _testDocuments.Take(50).ToList();

        using var transaction = _dapperConnection.BeginTransaction();
        try
        {
            foreach (var doc in batch)
            {
                var json = JsonSerializer.Serialize(doc, _jsonOptions);
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

    // ==================== Query By ID ====================

    [Benchmark]
    public async Task<TestDocument?> LiteDocumentStore_QueryById()
    {
        return await _documentStore.GetAsync<TestDocument>("doc-000025");
    }

    [Benchmark]
    public async Task<TestDocument?> RawDapper_QueryById()
    {
        var sql = "SELECT json(data) as data FROM [TestDocument] WHERE id = @Id";
        var json = await _dapperConnection.QuerySingleOrDefaultAsync<string>(sql, new { Id = "doc-000025" });

        return json != null
            ? JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)
            : null;
    }

    // ==================== Full Table Scan ====================

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
            JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)!
        ).ToList();

        return results.Count;
    }

    // ==================== Query with Filter ====================

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
            JsonSerializer.Deserialize<TestDocument>(json, _jsonOptions)!
        ).ToList();

        return results.Count;
    }

    // ==================== Delete ====================

    [Benchmark]
    public async Task<bool> LiteDocumentStore_Delete()
    {
        return await _documentStore.DeleteAsync<TestDocument>("doc-000099");
    }

    [Benchmark]
    public async Task<int> RawDapper_Delete()
    {
        var sql = "DELETE FROM [TestDocument] WHERE id = @Id";
        return await _dapperConnection.ExecuteAsync(sql, new { Id = "doc-000099" });
    }

    // ==================== Bulk Delete ====================

    [Benchmark]
    public async Task<int> LiteDocumentStore_BulkDelete()
    {
        var idsToDelete = _testDocuments.Take(10).Select(d => d.Id).ToList();
        return await _documentStore.DeleteManyAsync<TestDocument>(idsToDelete);
    }

    [Benchmark]
    public async Task<int> RawDapper_BulkDelete()
    {
        var idsToDelete = _testDocuments.Take(10).Select(d => d.Id).ToList();

        using var transaction = _dapperConnection.BeginTransaction();
        try
        {
            var sql = "DELETE FROM [TestDocument] WHERE id = @Id";
            var result = await _dapperConnection.ExecuteAsync(sql, idsToDelete.Select(id => new { Id = id }), transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ==================== Update ====================

    [Benchmark]
    public async Task LiteDocumentStore_Update()
    {
        var doc = _testDocuments[50];
        doc.Name = "Updated Name";
        doc.Age = 99;
        await _documentStore.UpsertAsync(doc.Id, doc);
    }

    [Benchmark]
    public async Task RawDapper_Update()
    {
        var doc = _testDocuments[50];
        doc.Name = "Updated Name";
        doc.Age = 99;
        var json = JsonSerializer.Serialize(doc, _jsonOptions);
        var sql = "INSERT OR REPLACE INTO [TestDocument] (id, data) VALUES (@Id, jsonb(@Data))";
        await _dapperConnection.ExecuteAsync(sql, new { Id = doc.Id, Data = json });
    }
}
