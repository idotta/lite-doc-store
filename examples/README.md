# LiteDocumentStore Examples

This folder contains executable C# examples demonstrating various features of LiteDocumentStore. All examples use .NET 10's single-file execution capability and can be run directly.

## Running Examples

Each example is a standalone `.cs` file that can be executed with:

```bash
dotnet run <example-name>.cs
```

Or on Unix-like systems (after setting executable permission):
```bash
chmod +x <example-name>.cs
./<example-name>.cs
```

On Windows PowerShell:
```powershell
./<example-name>.cs
```

## Available Examples

### 1. [QuickStart.cs](QuickStart.cs)
**Basic CRUD operations** - Learn the fundamentals

Demonstrates the core document store operations with a simple Customer model:
- Creating an in-memory document store with DI configuration
- Table creation with `CreateTableAsync<T>()`
- Inserting and updating documents with `UpsertAsync()` (insert or replace)
- Retrieving single documents by ID with `GetAsync()`
- Getting all documents with `GetAllAsync()`
- Checking document existence with `ExistsAsync()`
- Counting documents with `CountAsync()`
- Deleting documents with `DeleteAsync()`
- Bulk operations in transactions with `ExecuteInTransactionAsync()`

**Perfect for**: First-time users, understanding the API basics

**Run time**: < 1 second

---

### 2. [VirtualColumn.cs](VirtualColumn.cs)
**Dramatic query performance improvements** - 100x-1000x speedup

Shows how virtual columns transform query performance by extracting JSON properties into indexed SQLite columns:
- Creating and seeding 10,000 products for benchmarking
- Benchmarking queries WITHOUT virtual columns (full table scans)
- Adding virtual columns with `AddVirtualColumnAsync()` and indexes
- Benchmarking queries WITH virtual columns (index seeks)
- Point queries (exact match like `p.Category == "Electronics"`)
- Range queries (like `p.Price > 100`)
- Using raw SQL with virtual columns for complex filters

**Performance highlights** (10K rows):
- Category query: 49ms → 9ms (5x faster)
- SKU lookup: 1ms → 0ms (instant with index)
- Range queries on indexed columns also show significant speedup

**Perfect for**: Performance-critical applications, large datasets, frequently queried fields

**Key concept**: Virtual columns are SQLite's generated columns - they extract JSON values and store them as real columns that can be indexed. `QueryAsync()` automatically uses them.

**Run time**: ~2-3 seconds (includes data seeding)

---

### 3. [HybridUsage.cs](HybridUsage.cs)
**Mix document storage with traditional SQL** - Best of both worlds

Demonstrates that LiteDocumentStore is a HYBRID library - you get convenient document APIs AND full SQLite access:
- Creating document tables (Customer, Order) with the document store
- Inserting documents with `UpsertAsync()`
- Writing raw SQL joins across document tables using `json_extract()`
- Aggregate queries (SUM, GROUP BY) with `store.Connection`
- Creating SQL views over JSON data
- Mixing document tables with traditional relational tables
- Advanced filtering with SQLite functions (LOWER, LIKE)

**SQL Examples**:
- JOIN orders with customers via `json_extract()`
- SUM aggregations grouped by customer
- Creating views that flatten JSON into columns
- Creating traditional tables alongside document tables

**Perfect for**: Real-world applications needing both flexible schemas and relational queries

**Key concept**: The `Connection` property exposes the raw `SqliteConnection` for full ADO.NET access. You're never locked into just the document API - drop down to SQL anytime.

**Run time**: < 1 second

---

### 4. [IndexManagement.cs](IndexManagement.cs)
**Optimize query performance with JSON indexes** - Avoid full table scans

Shows how to create indexes on JSON properties for dramatic query speedups:
- Seeding 5,000 customers for benchmarking
- Benchmarking queries WITHOUT indexes (full table scans)
- Creating single-column indexes with `CreateIndexAsync()`
- Benchmarking queries WITH indexes (index seeks)
- Creating indexes on nested properties (Address.City)
- Creating composite indexes with `CreateCompositeIndexAsync()`
- Demonstrating idempotent index creation (safe to call multiple times)
- Inspecting indexes with raw SQL queries

**Performance highlights** (5K rows):
- Email lookup: 4ms → 0ms with index (instant)
- City query: Significant speedup with nested property index
- Composite index: Efficient for multi-column filters

**Perfect for**: Improving query performance on large datasets without using virtual columns

**Key difference from virtual columns**: 
- **Indexes**: Use `json_extract()` in the index expression, column doesn't exist
- **Virtual columns**: Create actual columns that store extracted values

**Run time**: ~1-2 seconds (includes data seeding)

---

### 5. [Migration.cs](Migration.cs)
**Schema versioning and evolution** - Track and apply schema changes

Demonstrates a complete migration system for managing schema changes over time:
- Defining migrations with version numbers (YYYYMMDDnnn format)
- Each migration has `upSql` (apply) and `downSql` (revert)
- Creating a `MigrationRunner` instance
- Applying pending migrations with `ApplyMigrationsAsync()`
- Migration history tracked in `__store_migrations` table
- Rolling back to previous versions with `RollbackToVersionAsync()`
- Schema introspection with `SchemaIntrospector`:
  - Listing tables and columns
  - Viewing indexes
  - Database statistics (page count, size)

**Migration examples**:
1. Create initial tables (Customer, Order)
2. Add email index for performance
3. Add composite indexes on orders
4. Add virtual column for city with index

**Perfect for**: Production applications, team environments, schema evolution over time

**Key concept**: Migrations are transactional and atomic - each runs in a transaction and is only recorded if successful. Version numbers ensure ordered execution.

**Run time**: ~1-2 seconds

---

### 6. [TransactionBatching.cs](TransactionBatching.cs)
**Maximize performance with transactions** - 50x-100x speedup for bulk operations

Demonstrates how transaction batching dramatically improves performance for bulk operations:
- Benchmarking individual inserts vs. batched inserts
- Using `ExecuteInTransactionAsync()` for automatic transaction management
- Bulk operations with `UpsertManyAsync()` for maximum speed
- Transaction rollback on errors (atomic operations)
- Using `IDbTransaction` parameter to mix document ops with raw SQL
- Multi-table atomic transactions (orders + customers)

**Performance highlights** (1,000 inserts):
- Individual inserts: ~5000ms
- Batched in transaction: ~50-100ms (50x-100x faster)
- UpsertManyAsync: ~30-50ms (100x-167x faster)

**Perfect for**: Data imports, batch processing, ensuring data consistency, bulk operations

**Key concept**: SQLite writes are disk-bound. Each individual insert without a transaction requires a full disk sync. Wrapping operations in a transaction batches all writes into a single disk sync at commit time.

**Run time**: ~5-10 seconds (includes benchmarks)

---

### 7. [MultiDatabase.cs](MultiDatabase.cs)
**Multiple databases with factory pattern** - Manage separate databases for tenants or domains

Demonstrates using `IDocumentStoreFactory` to create and manage multiple independent database instances:
- Creating the factory from DI container
- Creating separate databases for different tenants (multi-tenant architecture)
- Creating domain-separated databases (Products, Orders, Customers)
- Each store manages its own connection and lifecycle
- Different configuration per database (WAL mode, pragmas, etc.)
- Proper cleanup and disposal of multiple stores

**Use cases shown**:
- Multi-tenant: Separate databases for US and EU customers
- Domain separation: Products catalog vs. Orders transactions
- Each database is completely isolated with its own connection

**Perfect for**: Multi-tenant applications, microservices, domain-driven design, test isolation

**Key concept**: The factory is stateless and registered as a singleton. It creates independent store instances on demand, each with its own configuration. This is the recommended pattern when you don't need DI to manage store injection.

**Run time**: < 1 second

---

### 8. [MultiDatabaseKeyed.cs](MultiDatabaseKeyed.cs)
**Multiple databases with keyed DI** - Type-safe dependency injection for multiple databases

Demonstrates using keyed services (requires .NET 8+) to register and inject multiple database instances:
- Registering multiple keyed stores with `AddKeyedLiteDocumentStore()`
- Retrieving stores by key with `GetRequiredKeyedService<IDocumentStore>(key)`
- Different service lifetimes per database (Singleton vs. Scoped)
- Creating typed service classes that depend on specific keyed stores
- Using `[FromKeyedServices]` attribute for constructor injection
- Demonstrating lifetime differences across scopes

**Architecture patterns**:
- US and EU customer databases (regional separation)
- Products catalog (singleton, shared across app)
- Orders database (scoped, new instance per request)
- Typed services (CustomerService, OrderService) with keyed dependencies

**Perfect for**: Clean architecture, dependency injection, ASP.NET Core applications, testability

**Key concept**: Keyed services let the DI container manage multiple store instances with type-safe injection. Use `[FromKeyedServices("key")]` in constructors to inject the right store. Singleton lifetime is best for long-lived stores, Scoped for per-request isolation.

**Run time**: ~1 second

---

### 9. [AotVerification.cs](AotVerification.cs)
**Native AOT compatibility** - Reflection-free JSON with a source-generated context

Proves the library runs under Native AOT / trimming by supplying a source-generated
`JsonSerializerContext` and exercising the full document-store surface with no reflection-based
serialization:
- Defining a `[JsonSerializable]` partial `JsonSerializerContext`
- Wiring it in via `DocumentStoreOptionsBuilder.WithSerializerOptions(...)`
- Running CRUD, bulk writes, `CreateIndexAsync`, `QueryAsync<T,TValue>(jsonPath, value)`,
  `CountAsync`/`ExistsAsync`, and `IsHealthyAsync`
- Sets `#:property PublishAot=true` so it can be AOT-published directly

**Run (JIT)**: `dotnet run AotVerification.cs`

**Publish (AOT)**: `dotnet publish AotVerification.cs -r <your-RID>` (e.g. `win-x64`)

**Perfect for**: Verifying AOT builds, learning the source-generated serialization setup

**Key concept**: For AOT, set `SerializerOptions` to options backed by a source-generated
context (`new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }`). When no options
are supplied, the store falls back to reflection-based serialization (non-AOT only).

**Run time**: < 1 second

---

## Example Structure

Each example follows this pattern:

1. **Shebang line** - `#!/usr/bin/env dotnet run` for Unix execution
2. **Header comment** - Description and run instructions
3. **Package directives** - Using `#:package` for dependencies
4. **Project reference** - Links to the LiteDocumentStore project
5. **Model definitions** - Sample data structures (usually at bottom)
6. **Setup** - Creating store, tables, seeding data
7. **Feature demonstration** - Benchmarks and usage examples
8. **Summary** - Key takeaways printed to console

## Prerequisites

- .NET 10 SDK or later
- SQLite 3.45+ (bundled with modern .NET)
- LiteDocumentStore library (examples reference `../src/LiteDocumentStore/LiteDocumentStore.csproj`)

## Tips for Learning

1. **Start with QuickStart.cs** to understand the API basics
2. **Run TransactionBatching.cs** to learn critical performance optimization
3. **Run VirtualColumn.cs** to see dramatic query performance improvements
4. **Explore HybridUsage.cs** to understand the hybrid SQL approach
5. **Study IndexManagement.cs** for query optimization techniques
6. **Review Migration.cs** for production schema management
7. **Experiment** by modifying examples with your own data models

## Quick Reference Table

| Example | Focus Area | Dataset Size | Run Time | Key API |
|---------|-----------|--------------|----------|---------|
| QuickStart.cs | CRUD basics | 6 records | <1s | `UpsertAsync`, `GetAsync`, `DeleteAsync` |
| TransactionBatching.cs | Bulk performance | 1K orders | ~5-10s | `ExecuteInTransactionAsync`, `UpsertManyAsync` |
| VirtualColumn.cs | Query performance | 10K products | ~2-3s | `AddVirtualColumnAsync`, `QueryAsync`, raw SQL |
| HybridUsage.cs | SQL integration | Small | <1s | `Connection`, raw SQL |
| IndexManagement.cs | Indexing | 5K customers | ~1-2s | `CreateIndexAsync`, `CreateCompositeIndexAsync` |
| Migration.cs | Schema versioning | Small | ~1-2s | `MigrationRunner`, `SchemaIntrospector` |
| MultiDatabase.cs | Factory pattern | Small | <1s | `IDocumentStoreFactory.Create` |
| MultiDatabaseKeyed.cs | Keyed DI services | Small | ~1s | `AddKeyedLiteDocumentStore`, `[FromKeyedServices]` |
| AotVerification.cs | Native AOT | 3 records | <1s | `WithSerializerOptions`, source-gen context |

## Feedback

These examples are part of the LiteDocumentStore documentation. If you find issues or have suggestions for new examples, please open an issue or submit a PR!

---

**Happy coding!** 🚀
