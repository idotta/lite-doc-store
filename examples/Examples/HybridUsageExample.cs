using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Document API and raw SQL side by side over the same tables: a join, an aggregate, a SQL
/// view over JSON data, and a plain relational table living beside the document tables.
/// </summary>
internal static class HybridUsageExample
{
    internal sealed record Customer(string Id, string Name, string Email, string City);

    internal sealed record Order(string Id, string CustomerId, DateTime OrderDate, decimal TotalAmount, string Status);

    internal sealed record OrderWithCustomer(string OrderId, string OrderDate, double TotalAmount, string CustomerName, string CustomerEmail);

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Customer>();
        await store.CreateTableAsync<Order>();

        await store.UpsertAsync("c1", new Customer("c1", "Alice Smith", "alice@example.com", "New York"));
        await store.UpsertAsync("c2", new Customer("c2", "Bob Johnson", "bob@example.com", "Los Angeles"));
        await store.UpsertAsync("c3", new Customer("c3", "Carol Williams", "carol@example.com", "Chicago"));

        await store.UpsertAsync("o1", new Order("o1", "c1", DateTime.UtcNow.AddDays(-5), 150.00m, "Shipped"));
        await store.UpsertAsync("o2", new Order("o2", "c1", DateTime.UtcNow.AddDays(-3), 75.50m, "Delivered"));
        await store.UpsertAsync("o3", new Order("o3", "c2", DateTime.UtcNow.AddDays(-2), 220.00m, "Processing"));
        await store.UpsertAsync("o4", new Order("o4", "c3", DateTime.UtcNow.AddDays(-1), 99.99m, "Shipped"));

        // Raw SQL join across two document tables via json_extract - QueryAsync only filters one table.
        var ordersWithCustomers = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    o.id AS OrderId,
                    json_extract(o.data, '$.OrderDate') AS OrderDate,
                    json_extract(o.data, '$.TotalAmount') AS TotalAmount,
                    json_extract(c.data, '$.Name') AS CustomerName,
                    json_extract(c.data, '$.Email') AS CustomerEmail
                FROM [Order] o
                INNER JOIN Customer c ON json_extract(o.data, '$.CustomerId') = c.id
                ORDER BY OrderDate DESC
                """;

            var rows = new List<OrderWithCustomer>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new OrderWithCustomer(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            return rows;
        });

        Console.WriteLine("Orders with customers:");
        foreach (var order in ordersWithCustomers)
        {
            Console.WriteLine($"  {order.OrderId}  => ${order.TotalAmount:F2} - {order.CustomerName} ({order.CustomerEmail})");
        }

        // Aggregate spending per customer, mixing an aggregate function with json_extract.
        var spending = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    json_extract(c.data, '$.Name') AS Name,
                    SUM(CAST(json_extract(o.data, '$.TotalAmount') AS REAL)) AS Total
                FROM Customer c
                LEFT JOIN [Order] o ON c.id = json_extract(o.data, '$.CustomerId')
                GROUP BY c.id
                ORDER BY Total DESC
                """;

            var rows = new List<(string Name, double Total)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? 0 : reader.GetDouble(1)));
            }

            return rows;
        });

        Console.WriteLine("\nCustomer spending:");
        foreach (var (name, total) in spending)
        {
            Console.WriteLine($"  {name,-16} => ${total:F2}");
        }

        // A SQL view sits on top of the JSON columns exactly like it would over a normal table.
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE VIEW IF NOT EXISTS v_customer_orders AS
                SELECT
                    c.id AS customer_id,
                    json_extract(c.data, '$.Name') AS customer_name,
                    json_extract(c.data, '$.City') AS city,
                    o.id AS order_id,
                    json_extract(o.data, '$.Status') AS status
                FROM Customer c
                LEFT JOIN [Order] o ON c.id = json_extract(o.data, '$.CustomerId')
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        });

        var newYorkOrders = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT customer_name, order_id, status FROM v_customer_orders WHERE city = @City";
            cmd.Parameters.AddWithValue("@City", "New York");

            var rows = new List<(string Name, string OrderId, string Status)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return rows;
        });

        Console.WriteLine("\nOrders from New York (via view):");
        foreach (var (name, orderId, status) in newYorkOrders)
        {
            Console.WriteLine($"  {name} - {orderId} ({status})");
        }

        // A plain relational table living right beside the document tables in the same file.
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS product_inventory (
                    product_id TEXT PRIMARY KEY,
                    quantity INTEGER NOT NULL,
                    warehouse_location TEXT NOT NULL
                )
                """;
            await create.ExecuteNonQueryAsync(ct);

            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO product_inventory (product_id, quantity, warehouse_location) VALUES
                ('prod1', 100, 'Warehouse A'),
                ('prod2', 50, 'Warehouse B'),
                ('prod3', 200, 'Warehouse A')
                """;
            await insert.ExecuteNonQueryAsync(ct);
        });

        var inventory = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT product_id, quantity, warehouse_location FROM product_inventory";

            var rows = new List<(string ProductId, long Quantity, string Location)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            return rows;
        });

        Console.WriteLine("\nTraditional relational table (product_inventory):");
        foreach (var (productId, quantity, location) in inventory)
        {
            Console.WriteLine($"  {productId}  => {quantity} units in {location}");
        }
    }
}
