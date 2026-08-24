using System.Globalization;
using LiteDocumentStore.Examples;

// Samples print timings and money; pin the culture so the output reads the same everywhere.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var examples = new (string Name, string Description, Func<Task> Run)[]
{
    ("quickstart", "CRUD basics: create, upsert, get, update, delete", QuickStartExample.RunAsync),
    ("hybrid", "Document API and raw SQL over the same tables", HybridUsageExample.RunAsync),
    ("structured-query", "DocumentQuery filters, ordering, paging, filtered count", StructuredQueryExample.RunAsync),
    ("patch", "Field-level updates that survive a concurrent writer", PatchExample.RunAsync),
    ("indexes", "Expression indexes, composite indexes, before/after timings", IndexManagementExample.RunAsync),
    ("virtual-columns", "Generated columns for indexed range queries", VirtualColumnExample.RunAsync),
    ("transactions", "Transaction batching, rollback, raw SQL inside a transaction", TransactionBatchingExample.RunAsync),
    ("migrations", "store migrations, up/down SQL, schema introspection", MigrationsExample.RunAsync),
    ("multi-database", "Several databases through IDocumentStoreFactory", MultiDatabaseExample.RunAsync),
    ("multi-database-keyed", "Several databases through keyed DI", MultiDatabaseKeyedExample.RunAsync),
};

var requested = args.Length == 0 ? null : args[0].ToLowerInvariant();

if (requested is null or "list" or "--help" or "-h")
{
    Console.WriteLine("Usage: dotnet run --project examples/Examples -- <example>|all");
    Console.WriteLine();
    foreach (var (name, description, _) in examples)
    {
        Console.WriteLine($"  {name,-22} {description}");
    }

    Console.WriteLine($"  {"all",-22} Run every example in order");
    return 0;
}

if (requested == "all")
{
    foreach (var (name, _, run) in examples)
    {
        Console.WriteLine($"=== {name} ===");
        await run();
        Console.WriteLine();
    }

    return 0;
}

var match = examples.FirstOrDefault(e => e.Name == requested);
if (match.Run is null)
{
    Console.Error.WriteLine($"Unknown example '{requested}'. Run without arguments to list them.");
    return 1;
}

await match.Run();
return 0;
