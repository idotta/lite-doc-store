using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// MigrationRunner with up/down SQL, applied-history, rollback-to-version, and SchemaIntrospector.
/// </summary>
internal static class MigrationsExample
{
    internal sealed record Customer(string Id, string Name, string Email, string City);

    internal sealed record Order(string Id, string CustomerId, DateTime OrderDate, decimal Amount);

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        // There is no DI registration for MigrationRunner - get a connection through
        // ExecuteRawAsync and construct it there. Everything below shares that one connection
        // so migration state stays visible across the whole callback.
        await store.ExecuteRawAsync(RunMigrationsAsync);
    }

    private static async Task RunMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var runner = new MigrationRunner(connection);

        var createTables = new Migration(
            version: 20260822001,
            name: "CreateInitialTables",
            // A hand-written document table must match the schema the store expects, `version`
            // included - without it every Upsert fails with "no such column: version".
            upSql: """
                CREATE TABLE Customer (id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE [Order] (id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0);
                """,
            downSql: """
                DROP TABLE IF EXISTS [Order];
                DROP TABLE IF EXISTS Customer;
                """);

        var emailIndex = new Migration(
            version: 20260822002,
            name: "AddCustomerEmailIndex",
            upSql: "CREATE INDEX IF NOT EXISTS idx_customer_email ON Customer(json_extract(data, '$.Email'));",
            downSql: "DROP INDEX IF EXISTS idx_customer_email;");

        var orderIndexes = new Migration(
            version: 20260822003,
            name: "AddOrderCustomerIndex",
            upSql: "CREATE INDEX IF NOT EXISTS idx_order_customer ON [Order](json_extract(data, '$.CustomerId'));",
            downSql: "DROP INDEX IF EXISTS idx_order_customer;");

        var cityVirtualColumn = new Migration(
            version: 20260822004,
            name: "AddCityVirtualColumn",
            upSql: """
                ALTER TABLE Customer ADD COLUMN city TEXT GENERATED ALWAYS AS (json_extract(data, '$.City')) VIRTUAL;
                CREATE INDEX IF NOT EXISTS idx_customer_city ON Customer(city);
                """,
            downSql: "DROP INDEX IF EXISTS idx_customer_city;"); // SQLite cannot drop a generated column.

        var allMigrations = new[] { createTables, emailIndex, orderIndexes, cityVirtualColumn };

        var appliedCount = await runner.ApplyMigrationsAsync(allMigrations, ct);
        Console.WriteLine($"Applied migrations       => {appliedCount}");
        Console.WriteLine($"Current version          => {await runner.GetCurrentVersionAsync(ct)}");

        var history = (await runner.GetAppliedMigrationsAsync(ct)).OrderBy(m => m.Version).ToList();
        Console.WriteLine("Applied history:");
        foreach (var record in history)
        {
            Console.WriteLine($"  {record.Version} {record.Name,-24} at {record.AppliedAt:yyyy-MM-dd HH:mm:ss}");
        }

        // Seed through raw SQL using the same jsonb(@Data) SQL shape as the store. The store
        // binds @Data as UTF-8 bytes; jsonb() accepts JSON text too, which is what this binds.
        await InsertDocumentAsync(connection, "Customer", "c1", new Customer("c1", "Alice Smith", "alice@example.com", "New York"), ct);
        await InsertDocumentAsync(connection, "Customer", "c2", new Customer("c2", "Bob Johnson", "bob@example.com", "Los Angeles"), ct);
        await InsertDocumentAsync(connection, "Order", "o1", new Order("o1", "c1", DateTime.UtcNow.AddDays(-2), 150.00m), ct);

        var introspector = new SchemaIntrospector(connection);

        var tables = (await introspector.GetTablesAsync(ct)).ToList();
        Console.WriteLine($"\nTables                    => {string.Join(", ", tables.Select(t => t.Name))}");
        Console.WriteLine($"Customer exists           => {await introspector.TableExistsAsync("Customer", ct)}");

        var columns = (await introspector.GetColumnsAsync("Customer", ct)).ToList();
        Console.WriteLine($"Customer columns          => {string.Join(", ", columns.Select(c => c.Name))}");
        Console.WriteLine($"'city' column exists      => {await introspector.ColumnExistsAsync("Customer", "city", ct)}");

        var indexes = (await introspector.GetIndexesAsync("Customer", ct)).ToList();
        Console.WriteLine($"Customer indexes          => {string.Join(", ", indexes.Select(i => i.Name))}");
        Console.WriteLine($"idx_customer_email exists => {await introspector.IndexExistsAsync("idx_customer_email", ct)}");

        Console.WriteLine($"SQLite version            => {await introspector.GetSqliteVersionAsync(ct)}");

        var stats = await introspector.GetDatabaseStatisticsAsync(ct);
        Console.WriteLine($"Database size             => {stats.DatabaseSizeBytes / 1024.0:F2} KB ({stats.PageCount} pages x {stats.PageSize} bytes)");

        // Roll back to just after the email index - drops the order index and the city column's index.
        var rolledBack = await runner.RollbackToVersionAsync(emailIndex.Version, allMigrations, ct);
        Console.WriteLine($"\nRolled back               => {rolledBack} migration(s)");
        Console.WriteLine($"Current version           => {await runner.GetCurrentVersionAsync(ct)}");

        var remainingIndexes = (await introspector.GetIndexesAsync("Customer", ct)).ToList();
        Console.WriteLine($"Customer indexes now      => {string.Join(", ", remainingIndexes.Select(i => i.Name))}");
    }

    private static async Task InsertDocumentAsync<T>(SqliteConnection connection, string table, string id, T data, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO [{table}] (id, data) VALUES (@Id, jsonb(@Data))";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Data", JsonSerializer.Serialize(data));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
