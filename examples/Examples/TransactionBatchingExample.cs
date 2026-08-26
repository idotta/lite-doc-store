using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Batched vs. individual writes, UpsertManyAsync, rollback on throw, raw SQL enlisted in a
/// transaction, and a multi-table atomic write.
/// </summary>
internal static class TransactionBatchingExample
{
    internal sealed record Order(string Id, string CustomerId, string[] Items, decimal Total, DateTime CreatedAt);

    internal sealed record Customer(string Id, string Name, string Email, bool Active);

    private const int OrderCount = 1_000;

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Order>();
        await store.CreateTableAsync<Customer>();

        var orderTable = store.GetTableName<Order>();

        // Individual writes: each Upsert rents its own connection round trip.
        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= OrderCount; i++)
        {
            await store.UpsertAsync($"o{i}", NewOrder(i));
        }

        sw.Stop();
        var individualMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"Individual writes ({OrderCount})  => {individualMs} ms");

        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM [{orderTable}]";
            await cmd.ExecuteNonQueryAsync(ct);
        });

        // Batched writes: one transaction, one connection held for the whole loop. Write through
        // the transaction (tx.UpsertAsync) - writing through the store here would take a second
        // connection and deadlock against the transaction's write lock.
        sw.Restart();
        await store.ExecuteInTransactionAsync(async tx =>
        {
            for (var i = 1; i <= OrderCount; i++)
            {
                await tx.UpsertAsync($"o{i}", NewOrder(i));
            }
        });
        sw.Stop();
        var batchedMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"Batched in transaction        => {batchedMs} ms ({individualMs / (double)Math.Max(batchedMs, 1):F1}x faster)");

        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM [{orderTable}]";
            await cmd.ExecuteNonQueryAsync(ct);
        });

        // UpsertManyAsync: a single multi-row statement, no per-row round trip at all.
        sw.Restart();
        await store.UpsertManyAsync(Enumerable.Range(1, OrderCount).Select(i => ($"o{i}", NewOrder(i))));
        sw.Stop();
        Console.WriteLine($"UpsertManyAsync                => {sw.ElapsedMilliseconds} ms\n");

        // Rollback on throw: nothing written inside the callback survives.
        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("c1", new Customer("c1", "Alice", "alice@example.com", true));
            await tx.UpsertAsync("c2", new Customer("c2", "Bob", "bob@example.com", true));
        });

        var beforeCount = await store.CountAsync<Customer>();
        try
        {
            await store.ExecuteInTransactionAsync(async tx =>
            {
                await tx.UpsertAsync("c3", new Customer("c3", "Carol", "carol@example.com", true));
                throw new InvalidOperationException("Simulated business error");
            });
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Transaction threw              => {ex.Message}");
        }

        var afterCount = await store.CountAsync<Customer>();
        Console.WriteLine($"Customer count before/after     => {beforeCount} / {afterCount} (rolled back)\n");

        // Raw SQL enlisted in the same transaction as document writes.
        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("c4", new Customer("c4", "David", "david@example.com", true));

            await tx.ExecuteRawAsync(async (connection, ct) =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = $"UPDATE [{tx.GetTableName<Customer>()}] SET data = jsonb_set(data, '$.Name', json('\"David Miller\"')) WHERE id = 'c4'";
                await cmd.ExecuteNonQueryAsync(ct);
            });
        });

        var david = await store.GetAsync<Customer>("c4");
        Console.WriteLine($"Document + raw SQL committed    => {david?.Name}\n");

        // Multi-table atomic write: an order and a customer status flip commit together.
        // Immediate, because this callback reads and then writes: a deferred transaction pins a
        // read snapshot at the GetAsync and fails the later write with SQLITE_BUSY_SNAPSHOT if
        // another connection commits in between - an error busy_timeout cannot retry.
        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("o1001", new Order("o1001", "c1", ["laptop", "mouse", "keyboard"], 1499.99m, DateTime.UtcNow));

            var customer = await tx.GetAsync<Customer>("c1");
            if (customer is not null)
            {
                await tx.UpsertAsync("c1", customer with { Active = false });
            }
        }, TransactionMode.Immediate);

        var order = await store.GetAsync<Order>("o1001");
        var updatedCustomer = await store.GetAsync<Customer>("c1");
        Console.WriteLine($"Multi-table write               => order {order?.Id} (${order?.Total:F2}), customer active = {updatedCustomer?.Active}");
    }

    private static Order NewOrder(int i) =>
        new($"o{i}", $"c{i % 100}", [$"item{i}", $"item{i + 1}"], 99.99m + i, DateTime.UtcNow);
}
