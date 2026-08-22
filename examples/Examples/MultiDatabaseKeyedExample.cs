using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Several stores registered through AddKeyedLiteDocumentStore, resolved with
/// GetRequiredKeyedService and via [FromKeyedServices] in typed services. Keyed stores are always
/// singleton - there is no lifetime parameter to choose.
/// </summary>
internal static class MultiDatabaseKeyedExample
{
    internal sealed record Customer(string Id, string Name, string Email, string City);

    internal sealed record Order(string Id, string CustomerId, string ProductId, int Quantity, decimal Total);

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddKeyedLiteDocumentStore("UsCustomers", DocumentStoreOptions.ForSharedInMemory("UsCustomers"));
        services.AddKeyedLiteDocumentStore("EuCustomers", DocumentStoreOptions.ForSharedInMemory("EuCustomers"));
        services.AddKeyedLiteDocumentStore("Orders", DocumentStoreOptions.ForSharedInMemory("KeyedOrders"));

        // Typed services depending on one specific keyed store via [FromKeyedServices].
        services.AddSingleton<CustomerService>();
        services.AddSingleton<OrderService>();

        await using var provider = services.BuildServiceProvider();

        var usCustomers = provider.GetRequiredKeyedService<IDocumentStore>("UsCustomers");
        var euCustomers = provider.GetRequiredKeyedService<IDocumentStore>("EuCustomers");
        var ordersStore = provider.GetRequiredKeyedService<IDocumentStore>("Orders");

        await usCustomers.CreateTableAsync<Customer>();
        await euCustomers.CreateTableAsync<Customer>();
        await ordersStore.CreateTableAsync<Order>();

        await usCustomers.UpsertAsync("1", new Customer("1", "Alice Smith", "alice@acmeus.com", "New York"));
        await usCustomers.UpsertAsync("2", new Customer("2", "Bob Johnson", "bob@acmeus.com", "Los Angeles"));

        await euCustomers.UpsertAsync("1", new Customer("1", "Claire Dubois", "claire@acmeeu.com", "Paris"));
        await euCustomers.UpsertAsync("2", new Customer("2", "David Schmidt", "david@acmeeu.com", "Berlin"));

        await ordersStore.UpsertAsync("o1", new Order("o1", "1", "p1", 1, 999.99m));

        Console.WriteLine($"US customers        => {await usCustomers.CountAsync<Customer>()}");
        Console.WriteLine($"EU customers        => {await euCustomers.CountAsync<Customer>()}");
        Console.WriteLine($"Orders              => {await ordersStore.CountAsync<Order>()}");

        // Keyed stores are singletons: resolving the same key twice returns the same instance.
        var usAgain = provider.GetRequiredKeyedService<IDocumentStore>("UsCustomers");
        Console.WriteLine($"Same instance twice => {ReferenceEquals(usCustomers, usAgain)}");

        var customerService = provider.GetRequiredService<CustomerService>();
        var orderService = provider.GetRequiredService<OrderService>();

        Console.WriteLine($"CustomerService     => {(await customerService.GetAllAsync()).Count()} customers (via UsCustomers key)");
        Console.WriteLine($"OrderService        => {(await orderService.GetAllAsync()).Count()} orders (via Orders key)");
    }

    private sealed class CustomerService([FromKeyedServices("UsCustomers")] IDocumentStore store)
    {
        public Task<IEnumerable<Customer>> GetAllAsync() => store.GetAllAsync<Customer>();
    }

    private sealed class OrderService([FromKeyedServices("Orders")] IDocumentStore store)
    {
        public Task<IEnumerable<Order>> GetAllAsync() => store.GetAllAsync<Order>();
    }
}
