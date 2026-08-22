using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// CRUD basics: registering a store, creating a table, writing, reading, updating and deleting.
/// </summary>
internal static class QuickStartExample
{
    internal sealed record Customer(string Id, string Name, string Email, int Age, string City);

    public static async Task RunAsync()
    {
        // The store is registered as a singleton and is safe to share across threads.
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Customer>();

        await store.UpsertAsync("1", new Customer("1", "Alice Smith", "alice@example.com", 34, "Seattle"));
        await store.UpsertManyAsync(
        [
            ("2", new Customer("2", "Bob Johnson", "bob@example.com", 41, "Portland")),
            ("3", new Customer("3", "Carol Williams", "carol@example.com", 29, "Seattle")),
        ]);

        var alice = await store.GetAsync<Customer>("1");
        Console.WriteLine($"Get 1              => {alice?.Name} ({alice?.City})");

        // Records make updates a copy: upsert overwrites the whole document.
        if (alice is not null)
        {
            await store.UpsertAsync("1", alice with { City = "Tacoma" });
            Console.WriteLine($"After update       => {(await store.GetAsync<Customer>("1"))?.City}");
        }

        var seattle = await store.QueryAsync<Customer, string>("$.City", "Seattle");
        Console.WriteLine($"Query $.City       => {seattle.Count()} in Seattle");

        Console.WriteLine($"Count              => {await store.CountAsync<Customer>()}");
        Console.WriteLine($"Exists 2           => {await store.ExistsAsync<Customer>("2")}");

        // A transaction is its own unit of work: write through the transaction, not the store.
        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("4", new Customer("4", "David Brown", "david@example.com", 52, "Boise"));
            await tx.UpsertAsync("5", new Customer("5", "Eve Davis", "eve@example.com", 38, "Boise"));
        });

        Console.WriteLine($"After transaction  => {await store.CountAsync<Customer>()}");

        Console.WriteLine($"Delete 3           => {await store.DeleteAsync<Customer>("3")}");
        Console.WriteLine($"Delete 4 and 5     => {await store.DeleteManyAsync<Customer>(["4", "5"])}");
        Console.WriteLine($"Final count        => {await store.CountAsync<Customer>()}");
    }
}
