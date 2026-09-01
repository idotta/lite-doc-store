# LiteDocumentStore

[![CI](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml)
[![Code Quality](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml)
[![NuGet](https://img.shields.io/nuget/v/LiteDocumentStore.svg)](https://www.nuget.org/packages/LiteDocumentStore/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Turn a single SQLite `.db` file into a hybrid document + relational store. C# objects are serialized
to JSON and stored in SQLite's binary **JSONB** format, and the same tables stay fully open to raw
SQL, joins and indexes — this is deliberately *not* an opaque document database. Raw ADO.NET over
`Microsoft.Data.Sqlite`, no ORM, no runtime reflection or IL generation, so the library is
Native-AOT / trim compatible.

## Install

```bash
dotnet add package LiteDocumentStore
```

**Requirements:** .NET 10, and SQLite 3.45+ for JSONB — the bundled native SQLite already satisfies
this, and every connection is checked as it opens, so an older one fails fast with
`UnsupportedSqliteVersionException` instead of `no such function: jsonb`.

## Quick start

Register the store through dependency injection and resolve `IDocumentStore`:

```csharp
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLiteDocumentStore(options =>
{
    options.ConnectionString = "Data Source=app.db";
    options.EnableWalMode = true;
    // For Native-AOT, supply source-generated metadata:
    // options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = MyJsonContext.Default };
});

await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IDocumentStore>();

await store.CreateTableAsync<Customer>();
await store.UpsertAsync("c1", new Customer { Name = "Ada", Email = "ada@example.com" });

var customer = await store.GetAsync<Customer>("c1");
```

Without DI, build the store via `IDocumentStoreFactory.CreateAsync(DocumentStoreOptions)`.

## What you get

- **Document CRUD** (`IDocumentStore`): type-safe, fully async, table names derived from the type's
  namespace-qualified name through a pluggable `ITableNamingConvention` — see *Table names* below.
- **Querying**: a JSON-path equality shorthand, plus a composable `DocumentQuery<T>` builder with
  comparison, `Like`/`Glob`, `In`, null and array-contains operators, ordering and paging.
- **Field-level patching**: `DocumentPatch<T>` changes named fields in one statement, so a
  concurrent writer's edits to *other* fields survive — a read-modify-write silently reverts them.
- **Optimistic concurrency**: every row carries a `version`; `GetWithVersionAsync` plus the
  `…WithVersionAsync` writes and deletes are compare-and-swap, and a lost race throws
  `ConcurrencyException` carrying a `ConcurrencyConflictKind` to pick a retry strategy from.
- **Transactions**: `BeginTransactionAsync` / `ExecuteInTransactionAsync`, deferred by default with
  `TransactionMode.Immediate` available for read-then-write work.
- **Blobs**: raw binary payloads with content type, timestamps, versioning, prefix listing, and
  streaming in both directions — including a seekable read stream over SQLite's incremental blob
  I/O, so a large payload is never materialized.
- **Migrations**: versioned `IMigration` steps with checksummed history, applied under a write lock
  so two processes starting together cannot both run the same migration.
- **Indexes**: expression indexes over JSON paths, composite and unique variants, partial-index
  filters, and virtual (generated) columns for hot query paths.
- **Native-AOT / trim compatible**: `<IsAotCompatible>true</IsAotCompatible>`; serialization goes
  through `System.Text.Json` `JsonTypeInfo<T>`.
- **Cross-platform**: tested on Windows, Linux and macOS.

### Querying

```csharp
// JSON path + value
var byName = await store.QueryAsync<Customer, string>("$.Name", "Ada");

// Composable, and index-aware
var q = DocumentQuery<Customer>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
                               .AndIn("$.City", ["Boston", "Denver"])
                               .AndIsNotNull("$.Email")
                               .OrderBy("$.Age", descending: true)
                               .Skip(10).Take(20);

var adults = await store.QueryAsync(q);
var howMany = await store.CountAsync(q);   // predicates only; ordering and paging are ignored
```

Predicates combine with AND only. For joins, aggregates, OR groups and virtual-column seeks, drop to
raw SQL.

### Raw SQL

The connection is on loan for the duration of the callback. `GetTableName<T>()` gives the table the
store uses for `T`, so nothing is hardcoded, and `DeserializeDocument<T>()` reads a `json(data)`
column back with the store's own serializer options. Finish any transaction you open in the
callback: a connection handed back with one still on it is closed rather than pooled, so the leak
costs a connection instead of poisoning the next caller.

```csharp
var table = store.GetTableName<Customer>();

var adults = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT json(data) FROM [{table}] WHERE json_extract(data, '$.Age') >= @Min";
    cmd.Parameters.AddWithValue("@Min", 18);

    var results = new List<Customer>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        var doc = store.DeserializeDocument<Customer>(reader.GetString(0));
        if (doc is not null) results.Add(doc);
    }
    return results;
});
```

`SerializeDocument<T>(value)` is the write half: it returns the same UTF-8 JSON bytes the store
writes, so a raw `INSERT INTO [table] (id, data, version) VALUES (@Id, jsonb(@Data), 1)` stores
documents the store can read back. All three members are on `IDocumentTransaction` too.

Create commands with `connection.CreateCommand()` — a directly constructed `new SqliteCommand(sql,
connection)` leaves `Transaction` null, and Microsoft.Data.Sqlite refuses to execute it while a
transaction is pending.

### Concurrency and transactions

`IDocumentStore` is **thread-safe** and meant to be a singleton — one per database. It owns a
pool of SQLite connections and rents one per operation, so concurrent callers never share a
connection handle. Size the pool with `DocumentStoreOptions.MaxPoolSize`.

A transaction holds one of those connections until it is committed, rolled back or disposed —
`await using` it. One that is never finished holds its slot until the garbage collector finalizes
it, which logs the leak at `Error` and gives the slot back; until then, operations waiting for a
connection fail with `TimeoutException` after `DocumentStoreOptions.PoolWaitTimeoutMs` (30 s by
default, `Timeout.Infinite` to queue indefinitely) rather than hanging.

Because each operation runs on its own connection, operations called directly on the store each
commit on their own. To make several writes atomic, use a transaction and call the operations
**on it**:

```csharp
await using var tx = await store.BeginTransactionAsync();
await tx.UpsertAsync(order.Id, order);
await tx.PutBlobAsync(order.Id, invoicePdf);
await tx.CommitAsync();   // disposing without committing rolls back
```

Or let the store handle commit/rollback for you:

```csharp
await store.ExecuteInTransactionAsync(async tx =>
{
    await tx.UpsertAsync(order.Id, order);
    await tx.DeleteAsync<Draft>(draftId);
});
```

Transactions are independent: two concurrent transactions run on two connections, so neither
can see or roll back the other's writes.

### In-memory databases

Use `DocumentStoreOptions.ForInMemory()` for a private in-memory database, or
`ForSharedInMemory(name)` to share one between stores. A bare `Data Source=:memory:` is
rejected: it belongs to a single connection, so a pooled store would hand every operation its
own empty database. Note that shared-cache in-memory databases lock at table granularity —
overlapping write transactions fail with `SQLITE_LOCKED`, so use a file database for concurrent
write workloads.

## How it works

- **Storage.** One table per document type: `id TEXT PRIMARY KEY, data BLOB NOT NULL, version
  INTEGER NOT NULL DEFAULT 1`. Writes go through `jsonb(@Data)` with UTF-8 JSON bytes; reads come
  back as `SELECT json(data)`. JSONB is binary, so a raw `SELECT data` is not deserializable.
- **Table names.** The default `ITableNamingConvention` uses the type's namespace-qualified name
  with every separator folded to an underscore, and a constructed generic appends its arity then
  each argument by the same rule:

  | type | table |
  |---|---|
  | `Customer` (global namespace) | `Customer` |
  | `MyApp.Sales.Order` | `MyApp_Sales_Order` |
  | `MyApp.Outer+Inner` | `MyApp_Outer_Inner` |
  | `MyApp.Box<int>` | `MyApp_Box_1_System_Int32` |

  Never hardcode a table name — ask the store: `store.GetTableName<T>()`, on a transaction too.
  The fold is deliberately collision-resistant rather than injective, so a store additionally
  **refuses to serve two different types that resolve to one table name** (which would otherwise
  make each type's writes overwrite the other's rows silently). Types the default cannot name —
  open generics, arrays, types nested in a generic, non-ASCII names — throw `NotSupportedException`
  naming the type. Supply your own convention through `DocumentStoreOptions.TableNamingConvention`
  or `WithTableNamingConvention`; to keep names an earlier version wrote, that is five lines:

  ```csharp
  internal sealed class SimpleTypeNameConvention : ITableNamingConvention
  {
      public string GetTableName<T>() => GetTableName(typeof(T));

      public string GetTableName(Type type) => type.Name;
  }
  ```

  An existing database keeps the tables it has, so switching to the folded default means renaming
  them (or plugging the convention above). The same applies to raw SQL inside your own `IMigration`
  implementations and to auto-derived index names, which are `idx_{table}_{path}`.
- **Safety.** All *values* are parameterized. SQL identifiers and JSON paths cannot be bound, so
  they are interpolated — and validated first, in one place: table/index/column names must match
  `[A-Za-z_][A-Za-z0-9_]*`, JSON paths must match `$(.member|[index])*`, and column types come from
  a five-entry whitelist. JSON paths are interpolated *on purpose*: SQLite only matches a query
  against an expression index when the indexed expression appears literally, so binding the path
  would silently disable every index the store creates. `ExecuteRawAsync` is the escape hatch, and
  SQL you write there is yours to parameterize.
- **Connections.** The store opens and PRAGMA-configures connections once, then rents one per
  operation from its own pool. WAL and `synchronous = NORMAL` are the defaults; an option the
  database cannot honour (page size, WAL on an in-memory DB) is refused at open rather than
  silently ignored.

## Dependencies

- .NET 10
- Microsoft.Data.Sqlite
- SQLitePCLRaw.lib.e_sqlite3 — referenced directly and pinned, rather than taken transitively, to
  keep the native SQLite on a version without known advisories
- Microsoft.Extensions.DependencyInjection.Abstractions / Logging.Abstractions

## CI/CD

- **Continuous Integration**: builds, unit + integration tests, and every example run on each push
  and PR, with a coverage floor that fails the build
- **Multi-platform Testing**: tests run on Ubuntu, Windows and macOS
- **Packaging**: the package is packed and its contents asserted on every run; a Native AOT publish
  of `examples/AotVerification` proves the AOT claim by running the binary
- **Code Quality**: formatting and static analysis, plus a CodeQL security scan
- **NuGet Publishing**: automated on GitHub releases, with build provenance attestation
- **Dependency Updates**: Dependabot keeps dependencies up to date, and CI fails on a vulnerable one

See [.github/WORKFLOWS.md](.github/WORKFLOWS.md) for detailed CI/CD documentation.

## Contributing

Contributions are welcome. The solution is at the repository root, so nothing needs a path:

```bash
dotnet build --configuration Release
dotnet test tests/LiteDocumentStore.UnitTests/LiteDocumentStore.UnitTests.csproj
dotnet test tests/LiteDocumentStore.IntegrationTests/LiteDocumentStore.IntegrationTests.csproj
dotnet run --project examples/Examples -- all
```

Then:

1. Fork the repository
2. Create a feature branch
3. Make your changes, with both a unit and an integration test
4. Make sure `dotnet test` and `dotnet format --verify-no-changes` pass
5. Submit a pull request

CI will automatically validate your changes.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
