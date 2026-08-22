namespace LiteDocumentStore.Examples;

/// <summary>
/// Several independent stores created through IDocumentStoreFactory - a multi-tenant split and a
/// domain split, each store owning its own connection pool and disposed explicitly.
/// </summary>
internal static class MultiDatabaseExample
{
    internal sealed record Customer(string Id, string Name, string Email, string City);

    internal sealed record Product(string Id, string Name, decimal Price, string Category);

    internal sealed record Order(string Id, string CustomerId, string ProductId, int Quantity, decimal Total);

    public static async Task RunAsync()
    {
        // The factory needs no container: its parameterless constructor composes the default
        // connection factory and naming convention. DI is only convenient when you want the
        // stores themselves injected (see the keyed example).
        IDocumentStoreFactory factory = new DocumentStoreFactory();

        // Multi-tenant split: one database per tenant.
        await using var tenantA = factory.Create(DocumentStoreOptions.Builder().UseSharedInMemory("TenantA").Build());
        await using var tenantB = factory.Create(DocumentStoreOptions.Builder().UseSharedInMemory("TenantB").Build());

        await tenantA.CreateTableAsync<Customer>();
        await tenantB.CreateTableAsync<Customer>();

        await tenantA.UpsertAsync("1", new Customer("1", "Alice Smith", "alice@acmeus.com", "New York"));
        await tenantA.UpsertAsync("2", new Customer("2", "Bob Johnson", "bob@acmeus.com", "Los Angeles"));

        await tenantB.UpsertAsync("1", new Customer("1", "Claire Dubois", "claire@acmeeu.com", "Paris"));
        await tenantB.UpsertAsync("2", new Customer("2", "David Schmidt", "david@acmeeu.com", "Berlin"));
        await tenantB.UpsertAsync("3", new Customer("3", "Elena Rossi", "elena@acmeeu.com", "Rome"));

        Console.WriteLine($"Tenant A customers  => {await tenantA.CountAsync<Customer>()}");
        Console.WriteLine($"Tenant B customers  => {await tenantB.CountAsync<Customer>()}");

        // Domain split: separate databases for unrelated data domains.
        await using var products = factory.Create(DocumentStoreOptions.Builder().UseSharedInMemory("Products").Build());
        await using var orders = factory.Create(DocumentStoreOptions.Builder().UseSharedInMemory("Orders").Build());

        await products.CreateTableAsync<Product>();
        await orders.CreateTableAsync<Order>();

        await products.UpsertAsync("p1", new Product("p1", "Laptop", 999.99m, "Electronics"));
        await products.UpsertAsync("p2", new Product("p2", "Mouse", 29.99m, "Electronics"));
        await products.UpsertAsync("p3", new Product("p3", "Desk Chair", 199.99m, "Furniture"));

        await orders.UpsertAsync("o1", new Order("o1", "1", "p1", 1, 999.99m));
        await orders.UpsertAsync("o2", new Order("o2", "2", "p2", 2, 59.98m));

        Console.WriteLine($"Products catalog    => {await products.CountAsync<Product>()} items");
        Console.WriteLine($"Orders              => {await orders.CountAsync<Order>()} transactions");

        // Each store owns its own connection pool, so each one has to be disposed - the
        // `await using` declarations above do it even if something above throws.
    }
}
