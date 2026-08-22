using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Virtual columns for point and range queries - range is the thing QueryAsync cannot do,
/// contrasted with the json_extract equality path it uses under the hood.
/// </summary>
internal static class VirtualColumnExample
{
    internal sealed record Product(string Id, string Name, string Category, string Sku, decimal Price, int StockQuantity);

    private const int ProductCount = 2_000;

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Product>();

        Console.WriteLine($"Seeding {ProductCount} products...");
        await store.ExecuteInTransactionAsync(async tx =>
        {
            for (var i = 1; i <= ProductCount; i++)
            {
                var category = (i % 5) switch
                {
                    0 => "Electronics",
                    1 => "Hardware",
                    2 => "Software",
                    3 => "Books",
                    _ => "Accessories",
                };

                await tx.UpsertAsync(
                    $"p{i}",
                    new Product($"p{i}", $"Product {i}", category, $"SKU-{i:D6}", 19.99m + (i % 100), i % 1000));
            }
        });

        Console.WriteLine($"Inserted                     => {await store.CountAsync<Product>()} products\n");

        // The equality path: QueryAsync builds WHERE json_extract(data, '$.Category') = @Value.
        var sw = Stopwatch.StartNew();
        var electronics = (await store.QueryAsync<Product, string>("$.Category", "Electronics")).ToList();
        sw.Stop();
        Console.WriteLine($"json_extract equality        => {sw.Elapsed.TotalMilliseconds:F3} ms, found {electronics.Count}");

        // Add generated columns backed by indexes. REAL affinity matters for Price - TEXT affinity
        // would compare numbers as strings and silently break range queries.
        await store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);
        await store.AddVirtualColumnAsync<Product>(p => p.Price, "price", createIndex: true, columnType: "REAL");
        Console.WriteLine("Virtual columns added        => [category] TEXT, [price] REAL\n");

        // Point query through the virtual column - same shape of query, now backed by a real index.
        sw.Restart();
        var electronicsViaColumn = await QueryByRawWhereAsync(store, "[category] = @Category", ("@Category", "Electronics"));
        sw.Stop();
        Console.WriteLine($"Virtual column equality      => {sw.Elapsed.TotalMilliseconds:F3} ms, found {electronicsViaColumn.Count}");

        // Range query - QueryAsync<T, TValue> only ever emits an equality predicate, so a virtual
        // column plus raw SQL is the only way to get an indexed range scan.
        sw.Restart();
        var expensive = await QueryByRawWhereAsync(store, "[price] > @MinPrice", ("@MinPrice", 100.0));
        sw.Stop();
        Console.WriteLine($"Virtual column range (>100)  => {sw.Elapsed.TotalMilliseconds:F3} ms, found {expensive.Count}");

        // Virtual columns compose with other predicates just like regular columns.
        var combined = await QueryByRawWhereAsync(
            store,
            "[category] = @Category AND [price] < @MaxPrice",
            ("@Category", "Electronics"), ("@MaxPrice", 50.0));
        Console.WriteLine($"Combined category + range    => found {combined.Count}");
    }

    // `whereClause` is interpolated, so it must always be a literal written here - never
    // anything reaching the app from outside. Values belong in `parameters`.
    private static async Task<List<Product>> QueryByRawWhereAsync(
        IDocumentStore store, string whereClause, params (string Name, object Value)[] parameters)
    {
        return await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT json(data) FROM Product WHERE {whereClause}";
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var rows = new List<Product>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(JsonSerializer.Deserialize<Product>(reader.GetString(0))!);
            }

            return rows;
        });
    }
}
