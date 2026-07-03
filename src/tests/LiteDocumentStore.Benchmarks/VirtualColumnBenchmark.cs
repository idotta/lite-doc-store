using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Benchmarks;

/// <summary>
/// Benchmarks comparing query performance with and without virtual columns.
/// Virtual columns allow SQLite to index extracted JSON fields for faster queries.
/// Uses file-based databases to demonstrate real I/O benefits of indexing.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, iterationCount: 15)]
public class VirtualColumnBenchmark
{
    private IDocumentStore _storeWithVirtual = null!;
    private IDocumentStore _storeWithoutVirtual = null!;
    private ServiceProvider _serviceProviderWithVirtual = null!;
    private ServiceProvider _serviceProviderWithoutVirtual = null!;
    private const int DocumentCount = 50000; // Larger dataset to show index benefits
    private const string DbWithVirtual = "benchmark_with_virtual.db";
    private const string DbWithoutVirtual = "benchmark_without_virtual.db";

    [GlobalSetup]
    public async Task Setup()
    {
        // Clean up old databases
        if (File.Exists(DbWithVirtual)) File.Delete(DbWithVirtual);
        if (File.Exists(DbWithoutVirtual)) File.Delete(DbWithoutVirtual);

        // Setup store WITH virtual columns (file-based)
        var servicesWithVirtual = new ServiceCollection();
        servicesWithVirtual.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = $"Data Source={DbWithVirtual}";
            options.EnableWalMode = true;
            options.PageSize = 4096;
            options.CacheSize = -2000; // 2MB cache
        });

        _serviceProviderWithVirtual = servicesWithVirtual.BuildServiceProvider();
        _storeWithVirtual = _serviceProviderWithVirtual.GetRequiredService<IDocumentStore>();

        // Setup store WITHOUT virtual columns (file-based)
        var servicesWithoutVirtual = new ServiceCollection();
        servicesWithoutVirtual.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = $"Data Source={DbWithoutVirtual}";
            options.EnableWalMode = true;
            options.PageSize = 4096;
            options.CacheSize = -2000; // 2MB cache
        });

        _serviceProviderWithoutVirtual = servicesWithoutVirtual.BuildServiceProvider();
        _storeWithoutVirtual = _serviceProviderWithoutVirtual.GetRequiredService<IDocumentStore>();

        // Create tables and seed data
        await _storeWithVirtual.CreateTableAsync<Product>();
        await _storeWithoutVirtual.CreateTableAsync<Product>();

        var products = new List<(string id, Product data)>();
        for (int i = 0; i < DocumentCount; i++)
        {
            var product = new Product
            {
                Name = $"Product {i}",
                Category = $"Category {i % 50}",
                Price = 10.0m + (i % 100),
                Stock = i % 1000,
                Sku = $"SKU-{i:D6}",
                IsActive = i % 3 != 0,
                Tags = [$"tag{i % 20}", $"tag{i % 30}"],
                Metadata = new ProductMetadata
                {
                    Brand = $"Brand {i % 100}",
                    Weight = 0.5 + i % 50 * 0.1,
                    Dimensions = $"{i % 10}x{i % 8}x{i % 5}",
                    Country = i % 2 == 0 ? "USA" : "Canada"
                }
            };

            products.Add(($"prod-{i}", product));
        }

        // Bulk insert for both stores in batches to avoid SQLite parameter limit (999)
        const int batchSize = 500; // Each document uses 2 parameters (id + data)
        for (int i = 0; i < products.Count; i += batchSize)
        {
            var batch = products.Skip(i).Take(batchSize).ToList();
            await _storeWithVirtual.UpsertManyAsync(batch);
            await _storeWithoutVirtual.UpsertManyAsync(batch);
        }

        // Add virtual columns with indexes to first store
        await _storeWithVirtual.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);
        await _storeWithVirtual.AddVirtualColumnAsync<Product>(p => p.Sku, "sku", createIndex: true);
        await _storeWithVirtual.AddVirtualColumnAsync<Product>(p => p.Metadata.Brand, "brand", createIndex: true);

        // Run ANALYZE to update statistics for query planner
        await _storeWithVirtual.Connection.ExecuteAsync("ANALYZE", commandType: System.Data.CommandType.Text);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _storeWithVirtual.DisposeAsync();
        await _storeWithoutVirtual.DisposeAsync();
        _serviceProviderWithVirtual.Dispose();
        _serviceProviderWithoutVirtual.Dispose();

        // Clear SQLite connection pools and force GC to release connections
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Small delay to ensure file handles are released
        await Task.Delay(100);

        // Clean up database files
        if (File.Exists(DbWithVirtual)) File.Delete(DbWithVirtual);
        if (File.Exists(DbWithoutVirtual)) File.Delete(DbWithoutVirtual);
    }

    [Benchmark(Baseline = true, Description = "Query by category WITHOUT virtual column")]
    public async Task<int> Query_WithoutVirtualColumn_ByCategory()
    {
        var results = await _storeWithoutVirtual.QueryAsync<Product, string>("$.Category", "Category 25");
        return results.Count();
    }

    [Benchmark(Description = "Query by category WITH virtual column and index")]
    public async Task<int> Query_WithVirtualColumn_ByCategory()
    {
        var results = await _storeWithVirtual.QueryAsync<Product, string>("$.Category", "Category 25");
        return results.Count();
    }

    [Benchmark(Description = "Query by SKU WITHOUT virtual column")]
    public async Task<int> Query_WithoutVirtualColumn_BySku()
    {
        var results = await _storeWithoutVirtual.QueryAsync<Product, string>("$.Sku", "SKU-005000");
        return results.Count();
    }

    [Benchmark(Description = "Query by SKU WITH virtual column and index")]
    public async Task<int> Query_WithVirtualColumn_BySku()
    {
        var results = await _storeWithVirtual.QueryAsync<Product, string>("$.Sku", "SKU-005000");
        return results.Count();
    }

    [Benchmark(Description = "Query nested property WITHOUT virtual column")]
    public async Task<int> Query_WithoutVirtualColumn_NestedProperty()
    {
        var results = await _storeWithoutVirtual.QueryAsync<Product, string>("$.Metadata.Brand", "Brand 42");
        return results.Count();
    }

    [Benchmark(Description = "Query nested property WITH virtual column and index")]
    public async Task<int> Query_WithVirtualColumn_NestedProperty()
    {
        var results = await _storeWithVirtual.QueryAsync<Product, string>("$.Metadata.Brand", "Brand 42");
        return results.Count();
    }

    [Benchmark(Description = "Raw SQL: Category query (indexed)")]
    public async Task<int> Query_RawSQL_WithIndex_ByCategory()
    {
        var results = await _storeWithVirtual.Connection.QueryAsync<byte[]>(
            "SELECT data FROM Product WHERE category = 'Category 25'");
        return results.Count();
    }

    [Benchmark(Description = "Raw SQL: Category query (no index)")]
    public async Task<int> Query_RawSQL_NoIndex_ByCategory()
    {
        var results = await _storeWithoutVirtual.Connection.QueryAsync<byte[]>(
            "SELECT data FROM Product WHERE json_extract(data, '$.Category') = 'Category 25'");
        return results.Count();
    }

    [Benchmark(Description = "Raw SQL: SKU query (indexed)")]
    public async Task<int> Query_RawSQL_WithIndex_BySku()
    {
        var results = await _storeWithVirtual.Connection.QueryAsync<byte[]>(
            "SELECT data FROM Product WHERE sku = 'SKU-025000'");
        return results.Count();
    }

    [Benchmark(Description = "Raw SQL: SKU query (no index)")]
    public async Task<int> Query_RawSQL_NoIndex_BySku()
    {
        var results = await _storeWithoutVirtual.Connection.QueryAsync<byte[]>(
            "SELECT data FROM Product WHERE json_extract(data, '$.Sku') = 'SKU-025000'");
        return results.Count();
    }

    [Benchmark(Description = "Add virtual column (column creation overhead)")]
    public async Task AddVirtualColumn_Overhead()
    {
        // Create a fresh database for this test
        var tempDb = "benchmark_temp.db";

        var services = new ServiceCollection();
        services.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = $"Data Source={tempDb}";
            options.EnableWalMode = true;
        });

        using var serviceProvider = services.BuildServiceProvider();
        await using var store = serviceProvider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Product>();

        // Insert a few documents
        for (int i = 0; i < 1000; i++)
        {
            await store.UpsertAsync($"prod-{i}", new Product
            {
                Name = $"Product {i}",
                Category = $"Category {i % 10}",
                Price = 10.0m + i
            });
        }

        // Measure adding virtual column
        await store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);

        // Proper cleanup: dispose resources and clear connection pool
        await store.DisposeAsync();
        serviceProvider.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Cleanup database file
        if (File.Exists(tempDb))
        {
            try { File.Delete(tempDb); } catch { /* ignore if still locked */ }
        }
    }
}

/// <summary>
/// Product entity for virtual column benchmarks.
/// </summary>
public class Product
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public string Sku { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<string> Tags { get; set; } = [];
    public ProductMetadata Metadata { get; set; } = new();
}

public class ProductMetadata
{
    public string Brand { get; set; } = string.Empty;
    public double Weight { get; set; }
    public string Dimensions { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}
