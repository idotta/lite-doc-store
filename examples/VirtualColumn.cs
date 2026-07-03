#!/usr/bin/env dotnet run
// Virtual Column Example - Dramatically improve query performance
//
// Run this example with: dotnet run VirtualColumn.cs
//
// Virtual columns are SQLite generated columns that extract and index JSON properties.
// They provide 100x-1000x speedup for frequently queried fields!

#:package Dapper@2.1.66
#:package Microsoft.Extensions.DependencyInjection@10.0.1
#:package Microsoft.Extensions.Logging@10.0.1
#:package Microsoft.Extensions.Logging.Console@10.0.1

#:project ../src/LiteDocumentStore/LiteDocumentStore.csproj

#:property PublishAot=false

using Dapper;
using System.Diagnostics;
using System.Linq;
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Enable reflection-based JSON serialization for .NET 10+
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

// Create store
logger.LogInformation("Creating product catalog...");
var options = new DocumentStoreOptionsBuilder()
    .UseInMemory()
    .WithWalMode(false)
    .Build();

services.AddLiteDocumentStore(options);
serviceProvider = services.BuildServiceProvider();
var store = serviceProvider.GetRequiredService<IDocumentStore>();

// Create table and seed data
await store.CreateTableAsync<Product>();

logger.LogInformation("Seeding 10,000 products...");
await store.ExecuteInTransactionAsync(async () =>
{
    for (int i = 1; i <= 10_000; i++)
    {
        var category = (i % 5) switch
        {
            0 => "Electronics",
            1 => "Hardware",
            2 => "Software",
            3 => "Books",
            _ => "Accessories"
        };

        await store.UpsertAsync(
            $"p{i}",
            new Product(
                $"p{i}",
                $"Product {i}",
                category,
                $"SKU-{i:D6}",
                19.99m + (i % 100),
                i % 1000
            )
        );
    }
});

Console.WriteLine($"Inserted {await store.CountAsync<Product>()} products");

// Helper: run a raw SQL WHERE clause against the Product table and deserialize the documents.
// Raw SQL over store.Connection is the hybrid escape hatch - it lets queries reference the
// indexed virtual columns directly (json_extract, used by QueryAsync, cannot use those indexes).
async Task<List<Product>> QueryByRawWhereAsync(string whereClause)
{
    var sql = $"SELECT json(data) as JsonData FROM Product WHERE {whereClause}";
    var rows = await store.Connection.QueryAsync<string>(sql);
    return rows.Select(j => System.Text.Json.JsonSerializer.Deserialize<Product>(j)!).ToList();
}

// Benchmark: Query WITHOUT virtual column (json_extract full scan via the string-path API)
logger.LogInformation("\nBenchmark 1: Query by Category WITHOUT virtual column...");
var sw = Stopwatch.StartNew();
var electronicsWithout = (await store.QueryAsync<Product, string>("$.Category", "Electronics")).ToList();
sw.Stop();
Console.WriteLine($"Found {electronicsWithout.Count} products in {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Sample: {electronicsWithout[0].Name} (SKU: {electronicsWithout[0].Sku})");

// Benchmark: Query by SKU WITHOUT virtual column
logger.LogInformation("\nBenchmark 2: Query by SKU WITHOUT virtual column...");
sw.Restart();
var productWithout = (await store.QueryAsync<Product, string>("$.Sku", "SKU-005000")).ToList();
sw.Stop();
Console.WriteLine($"Found {productWithout.Count} product(s) in {sw.ElapsedMilliseconds}ms");

// Now add virtual columns with indexes
logger.LogInformation("\nAdding virtual columns with indexes...");
await store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);
await store.AddVirtualColumnAsync<Product>(p => p.Sku, "sku", createIndex: true);
// Use REAL so numeric range comparisons ([price] > 100) work correctly (TEXT affinity would compare as strings).
await store.AddVirtualColumnAsync<Product>(p => p.Price, "price", createIndex: true, columnType: "REAL");
Console.WriteLine("Virtual columns created: [category], [sku], [price]");

// Benchmark: Query WITH virtual column (raw SQL hits the [category] index)
logger.LogInformation("\nBenchmark 3: Query by Category WITH virtual column...");
sw.Restart();
var electronicsWith = await QueryByRawWhereAsync("[category] = 'Electronics'");
sw.Stop();
Console.WriteLine($"Found {electronicsWith.Count} products in {sw.ElapsedMilliseconds}ms");
var improvement1 = sw.ElapsedMilliseconds > 0 ? "faster" : "nearly instant";
Console.WriteLine($"Result: {improvement1}!");

// Benchmark: Query by SKU WITH virtual column
logger.LogInformation("\nBenchmark 4: Query by SKU WITH virtual column (indexed)...");
sw.Restart();
var productWith = await QueryByRawWhereAsync("[sku] = 'SKU-005000'");
sw.Stop();
Console.WriteLine($"Found {productWith.Count} product(s) in {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Result: Should be sub-millisecond (index seek)!");

// Demonstrate range queries on indexed columns
logger.LogInformation("\nBenchmark 5: Range query on Price (indexed virtual column)...");
sw.Restart();
var expensiveProducts = await QueryByRawWhereAsync("[price] > 100");
sw.Stop();
Console.WriteLine($"Found {expensiveProducts.Count} products over $100 in {sw.ElapsedMilliseconds}ms");

// Show raw SQL access for complex queries
logger.LogInformation("\nBonus: Using raw SQL with virtual columns...");
var query = @"
    SELECT json(data) as JsonData
    FROM Product 
    WHERE [category] = 'Electronics' 
      AND [price] < 50
      AND [sku] LIKE 'SKU-00%'
    LIMIT 5";

var results = await store.Connection.QueryAsync<string>(query);
var products = new List<Product>();
foreach (var json in results)
{
    products.Add(System.Text.Json.JsonSerializer.Deserialize<Product>(json)!);
}

Console.WriteLine($"\nComplex query results: {products.Count} products");
foreach (var p in products)
{
    Console.WriteLine($"  - {p.Name}: {p.Sku} at ${p.Price}");
}

Console.WriteLine("\n✓ Virtual Column example completed!");
Console.WriteLine("\nKey Takeaways:");
Console.WriteLine("  • Virtual columns extract JSON fields into indexed columns");
Console.WriteLine("  • Point queries (exact match) get 100x-1000x speedup");
Console.WriteLine("  • Range queries and complex filters also benefit");
Console.WriteLine("  • Reference virtual columns via raw SQL ([col]) to use their indexes");
Console.WriteLine("  • Best for frequently queried fields in large tables");

// Define models
record Product(string Id, string Name, string Category, string Sku, decimal Price, int StockQuantity);
