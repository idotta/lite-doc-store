using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// The structured <see cref="DocumentQuery{T}"/> API: comparisons, LIKE, IN, null tests, array
/// membership, ordering, paging and a filtered count - all bound, all AOT-safe.
/// </summary>
internal static class StructuredQueryExample
{
    internal sealed record Customer(
        string Id,
        string Name,
        string City,
        int Age,
        string? Email,
        DateTime SignedUpAt,
        string[] Tags);

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Customer>();
        await store.UpsertManyAsync(Seed());
        Console.WriteLine($"Seeded                     => {await store.CountAsync<Customer>()} customers\n");

        // Range comparison - the thing QueryAsync<T, TValue> cannot express.
        await ShowAsync(store, "Age >= 30", DocumentQuery<Customer>
            .Where("$.Age", QueryOperator.GreaterThanOrEqual, 30));

        // LIKE: % and _ wildcards, case-insensitive for ASCII.
        await ShowAsync(store, "City LIKE 'S%'", DocumentQuery<Customer>
            .Where("$.City", QueryOperator.Like, "S%"));

        // GLOB is the case-sensitive cousin, with * and ? wildcards.
        await ShowAsync(store, "Name GLOB 'C*'", DocumentQuery<Customer>
            .Where("$.Name", QueryOperator.Glob, "C*"));

        await ShowAsync(store, "City IN (...)", DocumentQuery<Customer>
            .WhereIn("$.City", ["Seattle", "Denver"]));

        // A JSON null and an absent path both count as null.
        await ShowAsync(store, "Email IS NULL", DocumentQuery<Customer>.WhereIsNull("$.Email"));
        await ShowAsync(store, "Email IS NOT NULL", DocumentQuery<Customer>.WhereIsNotNull("$.Email"));

        // ArrayContains walks the array with json_each - no LIKE over the serialized text.
        await ShowAsync(store, "Tags contains 'vip'", DocumentQuery<Customer>
            .WhereArrayContains("$.Tags", "vip"));

        // A DateTime is normalized to the text System.Text.Json wrote, so it compares correctly.
        await ShowAsync(store, "SignedUpAt >= 2023", DocumentQuery<Customer>
            .Where("$.SignedUpAt", QueryOperator.GreaterThanOrEqual, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        // Predicates combine with AND, in any order; every builder call returns a new query.
        var anded = DocumentQuery<Customer>
            .Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
            .And("$.City", QueryOperator.Like, "S%")
            .AndIsNotNull("$.Email")
            .AndArrayContains("$.Tags", "vip");
        await ShowAsync(store, "AND of four", anded);

        await ShowAsync(store, "Order by Age asc", DocumentQuery<Customer>.All().OrderBy("$.Age"));
        await ShowAsync(store, "Order by Age desc", DocumentQuery<Customer>.All().OrderBy("$.Age", descending: true));

        // Paging: OFFSET then LIMIT over a stable ordering.
        var page = DocumentQuery<Customer>.All().OrderBy("$.Age", descending: true).Skip(1).Take(3);
        await ShowAsync(store, "Skip 1 / Take 3", page);

        // CountAsync applies the predicates and ignores ordering and paging.
        Console.WriteLine($"Count of the paged query   => {await store.CountAsync(page)} (paging ignored)");
        Console.WriteLine($"Count of 'AND of four'     => {await store.CountAsync(anded)}\n");

        // The path is interpolated, not bound, so an expression index over the same path is used.
        await store.CreateIndexAsync<Customer>(c => c.City, "idx_customer_city");
        Console.WriteLine($"Plan for City = 'Seattle'  => {await ExplainCityLookupAsync(store)}");
    }

    private static IEnumerable<(string, Customer)> Seed()
    {
        (string Id, string Name, string City, int Age, string? Email, int Year, string[] Tags)[] rows =
        [
            ("1", "Alice", "Seattle", 34, "alice@example.com", 2021, ["vip", "beta"]),
            ("2", "Bob", "Portland", 41, "bob@example.com", 2023, ["beta"]),
            ("3", "Carol", "Seattle", 29, null, 2022, ["vip"]),
            ("4", "Dave", "Denver", 52, "dave@example.com", 2024, []),
            ("5", "Erin", "San Diego", 30, null, 2020, ["vip", "legacy"]),
            ("6", "Frank", "Denver", 45, "frank@example.com", 2023, ["legacy"]),
            ("7", "Grace", "Sacramento", 38, "grace@example.com", 2025, ["vip", "beta"]),
        ];

        foreach (var (id, name, city, age, email, year, tags) in rows)
        {
            yield return (id, new Customer(
                id, name, city, age, email, new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc), tags));
        }
    }

    private static async Task ShowAsync(IDocumentStore store, string label, DocumentQuery<Customer> query)
    {
        var names = (await store.QueryAsync(query)).Select(c => c.Name);
        Console.WriteLine($"{label,-26} => {string.Join(", ", names)}");
    }

    // The structured query emits json_extract(data, '$.City') = @p0 - reproduce that shape here
    // to read its plan back; SqlGenerator itself is internal to the library.
    private static Task<string> ExplainCityLookupAsync(IDocumentStore store) =>
        store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"EXPLAIN QUERY PLAN SELECT json(data) as data FROM [{store.GetTableName<Customer>()}] " +
                "WHERE json_extract(data, '$.City') = @p0";
            command.Parameters.AddWithValue("@p0", "Seattle");

            await using var reader = await command.ExecuteReaderAsync(ct);
            var steps = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                steps.Add(reader.GetString(3));
            }

            return string.Join(" | ", steps);
        });
}
