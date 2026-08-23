using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Single-property, nested-property, and composite indexes; idempotent re-creation; and a
/// before/after timing on a seeded dataset.
/// </summary>
internal static class IndexManagementExample
{
    internal sealed record Address(string Street, string City, string State, string Country);

    internal sealed record Customer(string Id, string FirstName, string LastName, string Email, int Age, Address Address);

    // Seeded rows are capped well below production scale so the example finishes in a couple of
    // seconds; the relative before/after speedup still comes through clearly.
    private const int CustomerCount = 2_000;

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Customer>();

        Console.WriteLine($"Seeding {CustomerCount} customers...");
        await store.ExecuteInTransactionAsync(async tx =>
        {
            for (var i = 1; i <= CustomerCount; i++)
            {
                var city = i % 10 == 0 ? "New York" : i % 10 == 1 ? "Los Angeles" : "Chicago";
                await tx.UpsertAsync(
                    $"c{i}",
                    new Customer(
                        $"c{i}",
                        $"FirstName{i}",
                        $"LastName{i % 200}",
                        $"customer{i}@example.com",
                        25 + (i % 50),
                        new Address($"{i} Main St", city, "NY", "USA")));
            }
        });

        Console.WriteLine($"Inserted                 => {await store.CountAsync<Customer>()} customers\n");

        var targetEmail = "customer1000@example.com";

        var sw = Stopwatch.StartNew();
        var before = (await store.QueryAsync<Customer, string>("$.Email", targetEmail)).ToList();
        sw.Stop();
        Console.WriteLine($"Email lookup (no index)  => {sw.Elapsed.TotalMilliseconds:F3} ms, found {before.Count}");

        await store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");

        sw.Restart();
        var after = (await store.QueryAsync<Customer, string>("$.Email", targetEmail)).ToList();
        sw.Stop();
        Console.WriteLine($"Email lookup (indexed)   => {sw.Elapsed.TotalMilliseconds:F3} ms, found {after.Count}");

        // Indexes work on nested properties too - the member chain maps to $.Address.City.
        await store.CreateIndexAsync<Customer>(c => c.Address.City, "idx_customer_city");
        sw.Restart();
        var newYorkers = (await store.QueryAsync<Customer, string>("$.Address.City", "New York")).ToList();
        sw.Stop();
        Console.WriteLine($"City lookup (indexed)    => {sw.Elapsed.TotalMilliseconds:F3} ms, found {newYorkers.Count}");

        // A composite index needs both predicates in the same query to be used - QueryAsync only
        // filters a single path, so drop to raw SQL to combine LastName + Age.
        await store.CreateCompositeIndexAsync<Customer>(
            [c => c.LastName, c => c.Age],
            "idx_customer_lastname_age");

        sw.Restart();
        var composite = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT json(data) FROM [{store.GetTableName<Customer>()}]
                WHERE json_extract(data, '$.LastName') = @LastName
                  AND json_extract(data, '$.Age') = @Age
                """;
            // LastName cycles every 200 rows and Age every 50, both dividing 200 evenly, so every
            // customer named "LastName100" lands on age 25 - pick that combination on purpose.
            cmd.Parameters.AddWithValue("@LastName", "LastName100");
            cmd.Parameters.AddWithValue("@Age", 25);

            var rows = new List<Customer>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(store.DeserializeDocument<Customer>(reader.GetString(0))!);
            }

            return rows;
        });
        sw.Stop();
        Console.WriteLine($"LastName+Age (composite) => {sw.Elapsed.TotalMilliseconds:F3} ms, found {composite.Count}");

        // Index creation is idempotent - re-creating the same named index is a no-op, not an error.
        await store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");
        await store.CreateIndexAsync<Customer>(c => c.Email, "idx_customer_email");
        Console.WriteLine("Re-created idx_customer_email twice => no error (safe)");
    }
}
