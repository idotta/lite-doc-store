---
name: litedocumentstore
description: How to use the LiteDocumentStore .NET library (NuGet `LiteDocumentStore`) correctly from application code - a hybrid document + relational store over a single SQLite file using JSONB. Use whenever code references LiteDocumentStore, IDocumentStore, IDocumentOperations, IDocumentTransaction, DocumentStoreOptions, DocumentStoreFactory, AddLiteDocumentStore, DocumentQuery<T>, DocumentPatch<T>, IndexOptions, SqlMigration/IMigration, PutBlobAsync/OpenBlobReadAsync, ConcurrencyException or UpsertWithVersionAsync, or when the task is storing C# objects as JSON documents in SQLite from .NET (document CRUD, JSON-path queries, optimistic concurrency, expression indexes, blobs, migrations, Native AOT).
---

# LiteDocumentStore

LiteDocumentStore turns one SQLite `.db` file into a document store. C# objects are serialized
with System.Text.Json and stored as SQLite **JSONB** in uniform tables
(`id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1`). The same tables
stay open to raw SQL (joins, aggregates, `json_extract`) through `ExecuteRawAsync`. The library
targets `net10.0`, needs SQLite 3.45+ (the bundled one qualifies), and is Native-AOT compatible.

Namespaces: everything is in `LiteDocumentStore`; the exceptions are in
`LiteDocumentStore.Exceptions`.

## Install and set up

```bash
dotnet add package LiteDocumentStore
```

**DI (ASP.NET Core / generic host)** - always a singleton:

```csharp
using LiteDocumentStore;

builder.Services.AddLiteDocumentStore(o =>
{
    o.ConnectionString = "Data Source=app.db";   // keyword form, never a bare path
    // o.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default }; // AOT
});
// inject IDocumentStore; the container disposes it
```

**Without DI** - `DocumentStore` is internal; use the factory and dispose the store:

```csharp
await using IDocumentStore store = await new DocumentStoreFactory()
    .CreateAsync(DocumentStoreOptions.ForFile("app.db"));
```

**Tests** - an isolated in-memory database per store:

```csharp
await using var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
```

Create every table before using it. Nothing auto-creates tables:

```csharp
await store.CreateTableAsync<Customer>();   // idempotent; run on every startup
await store.CreateBlobTableAsync();         // only if you use blobs
```

## Core patterns

```csharp
public sealed record Customer(string Id, string Name, string City, int Age, string? Email, string[] Tags);

// CRUD. The id is a separate key argument; it is NOT read from the document.
await store.UpsertAsync("c1", new Customer("c1", "Ada", "Seattle", 36, "ada@x.io", ["vip"]));
Customer? c = await store.GetAsync<Customer>("c1");                 // null when absent
IReadOnlyDictionary<string, Customer> many = await store.GetManyAsync<Customer>(["c1", "c2"]); // missing = absent key
await store.UpsertManyAsync([("c2", c2), ("c3", c3)]);               // chunked, all-or-nothing
bool removed = await store.DeleteAsync<Customer>("c1");

// Query: AND-only predicates over SERIALIZED JSON paths, plus ordering and paging.
var q = DocumentQuery<Customer>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
    .AndIn("$.City", ["Seattle", "Denver"])
    .AndArrayContains("$.Tags", "vip")
    .OrderBy("$.Age", descending: true).Skip(20).Take(10);
IEnumerable<Customer> page = await store.QueryAsync(q);
long total = await store.CountAsync(q);                              // ignores paging
var bySimpleEquality = await store.QueryAsync<Customer, string>("$.City", "Seattle");

// Patch: field-level change, one statement, one version bump, never inserts.
long v = await store.PatchAsync("c2", DocumentPatch<Customer>.Set("$.City", "Boston").AndRemove("$.Email"));

// Optimistic concurrency (compare-and-swap).
var cur = await store.GetWithVersionAsync<Customer>("c2");           // VersionedDocument<T>(Data, Version)
await store.UpsertWithVersionAsync("c2", cur!.Data with { Age = 41 }, cur.Version); // ConcurrencyException if stale
await store.UpsertWithVersionAsync("new", doc, expectedVersion: 0);  // 0 = insert, must not exist

// Transaction: call operations ON tx. Immediate for read-then-write.
await store.ExecuteInTransactionAsync(async tx =>
{
    var a = await tx.GetAsync<Customer>("c2");
    await tx.UpsertAsync("c2", a! with { City = "Austin" });
    await tx.PutBlobAsync("c2-avatar", avatarBytes);
}, TransactionMode.Immediate);

// Index (expression form resolves the serialized key for you).
await store.CreateIndexAsync<Customer>(x => x.Email!, "idx_customer_email",
    new IndexOptions { Unique = true, Filter = IndexFilter.IsNotNull("$.Email") });

// Raw SQL escape hatch. Table names come from GetTableName<T>().
var table = store.GetTableName<Customer>();
long n = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT count(*) FROM [{table}] WHERE json_extract(data, '$.Age') > @a";
    cmd.Parameters.AddWithValue("@a", 30);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
});
```

## Rules that bite

1. **Create one store per database and share it.** It is thread-safe and owns a connection
   pool. Dispose it (`await using`, or let DI do it). A store per request multiplies pools and
   file handles.
2. **Call `CreateTableAsync<T>()` / `CreateBlobTableAsync()` first.** Otherwise you get a raw
   `SqliteException: no such table`. `TableNotFoundException` exists but is never thrown.
3. **Connection strings are keyword form.** Write `"Data Source=app.db"`, or use `ForFile(path)`
   / `UseFile(path)`. A bare `"app.db"` throws `ArgumentException`.
4. **Never use `Data Source=:memory:`.** It is rejected, because each pooled connection would see
   its own empty database. Use `ForInMemory()` (isolated) or `ForSharedInMemory(name)` (shared by
   name in the process). In-memory requires `EnableWalMode = false`; the presets set that.
5. **JSON paths name the serialized key, not the CLR property.** Under a camelCase policy write
   `$.email`, not `$.Email`. A wrong path fails silently: a query returns nothing, an index is
   NULL in every row, and a patch adds a stray field. Only the expression-based DDL overloads
   (`x => x.Email`) resolve names through the serializer.
6. **Inside a transaction, call operations on `tx`, never on `store`.** A store call uses
   another connection and commits on its own. A store write after `tx` has written blocks on
   the write lock and then fails with `SQLITE_BUSY` (on an in-memory store it fails at once with
   `SQLITE_LOCKED`). A store *read* inside the block silently returns the state before `tx`.
7. **Always `await using` a transaction.** Dispose without commit rolls back. A leaked one holds a
   pool slot, and later operations throw `TimeoutException` after `PoolWaitTimeoutMs` (30 s).
8. **Use `TransactionMode.Immediate` for read-then-write.** In a deferred transaction, a
   concurrent commit makes the later write fail with `SQLITE_BUSY_SNAPSHOT` (517), which no
   wait can fix.
9. **`UpsertAsync` replaces the whole document (last writer wins).** Use `PatchAsync` for
   field changes, and `*WithVersionAsync` when a write depends on what you read. Otherwise you
   silently revert concurrent edits.
10. **Never hardcode table names.** Default names are namespace-qualified
    (`MyApp.Sales.Order` -> `MyApp_Sales_Order`). Use `store.GetTableName<T>()`. Moving a type
    to another namespace moves its table, so existing data appears to be gone.
11. **Raw SQL keeps the JSONB contract.** Write with `jsonb(@Data)`, binding the `byte[]` from
    `SerializeDocument<T>`. Read with `SELECT json(data)` and `DeserializeDocument<T>`. Never
    `json_set`/`json_remove` on `data`, because they turn the column into TEXT. Build commands with
    `connection.CreateCommand()`.
12. **Every store-level `ExecuteRawAsync` call opens a fresh physical connection** (about 60-335
    µs). Batch several statements into one callback.
13. **Builders are immutable.** `q.And(...)` returns a new query. Discarding the result silently
    drops the filter. The same applies to `DocumentPatch` and `IndexFilter`.
14. **`CountAsync`/`ExistsAsync(query)` ignore paging, while `DeleteAsync(query)` honours it.**
    `DeleteAsync(All().OrderBy(...).Take(1000))` deletes exactly that page. Paging without
    `OrderBy` deletes an unspecified page.
15. **Range operators refuse UTC/Local `DateTime` and every `DateTimeOffset`,** because ISO text
    does not sort chronologically. Store instants as `long` ticks or Unix ms and range over
    those. Equality on dates is fine.
16. **Query and patch values are scalars:** string, bool, integral types, float, double,
    decimal, DateTime, DateTimeOffset, Guid, byte[] and enums. Pass values as your C# type —
    `Where("$.Status", Equal, Status.Active)` — and the store binds them the way your serializer
    writes that path (string enums, naming policy and custom converters included). An enum on a
    path the serializer metadata cannot resolve (a typo, a derived-only key, a dictionary entry)
    throws `ArgumentException` when the query runs; a range over an enum stored as its name throws
    too.
17. **AND only, no OR.** Use `WhereIn` for alternatives on one field. Anything richer goes
    through `ExecuteRawAsync`.
18. **Under Native AOT (or trimming),** supply `SerializerOptions` with a source-generated
    `JsonSerializerContext` listing every document type.
19. **`OpenBlobReadAsync` returns a `Stream` you must dispose.** It holds its own connection and
    a read lock until then.
20. **Index names:** pass an explicit name when you will refer to the index later. Re-creating
    a name with a *different* definition throws `InvalidOperationException`; drop it first.
21. **Overloads:** `ExistsAsync<T>(null!)` and `DeleteAsync<T>(null!)` are ambiguous and do not
    compile. A bare `default` in `BeginTransactionAsync(default)`,
    `ExecuteInTransactionAsync(a, default)`, `PutBlobAsync(id, data, default)` or
    `MigrateAsync(m, default)` compiles and binds to the `CancellationToken` overload (Deferred
    mode, no `BlobWriteOptions`, default `MigrationOptions`). Pass the mode or options explicitly.
22. **A patch `Set` more than one slot past the end of an array, or below a scalar, is a silent
    no-op that still bumps the version.** Use it on object keys and existing array slots only.
23. **The document type is the static `T`, not the runtime type.** `UpsertAsync(id, value)`
    infers `T` from the variable's declared type, which picks both the table and the
    serialization contract. Upserting a `Dog` through an `Animal` (or interface) variable stores
    it in Animal's table with only Animal's members. Pass the concrete type
    (`UpsertAsync<Dog>(...)`), or configure STJ polymorphism (`[JsonPolymorphic]`/
    `[JsonDerivedType]`) on the base type.

## Exceptions

| Exception | When | Do |
|---|---|---|
| `ConcurrencyException` (`Kind`: `AlreadyExists` / `VersionMismatch` / `DocumentNotFound`) | A `*WithVersionAsync` write or delete, or a patch, matched no row. A patch on a missing id is `DocumentNotFound`. | Re-read and retry, or report the conflict |
| `CorruptDataException` | A row exists but its payload is unreadable (SQL NULL / JSON `null` document, or a blob that is not a BLOB). Only raw SQL can cause this. | Delete or overwrite the row |
| `DocumentSerializationException` | JSON incompatible with `T`, a serialization failure, or a type missing from the source-generated context | Fix the type or the context |
| `MigrationOutOfOrderException` | A never-applied migration below the current version | Renumber, or `AllowOutOfOrder = true` |
| `MigrationChecksumMismatchException` | The up SQL of an applied migration was edited | Add a new migration instead |
| `IncompatiblePageSizeException` | The existing file's page size differs from `PageSize` (default 4096) | `PageSize = 0` |
| `UnsupportedSqliteVersionException` | SQLite < 3.45 | Use the bundled SQLite |
| `ArgumentException` / `ArgumentNullException` / `ArgumentOutOfRangeException` | A bad id, path, value, option or operator combination. Thrown before any I/O. | Fix the call |
| `InvalidOperationException` | Table-name collision between two types; an index name holding a different definition; using a transaction after commit/rollback | See the reference files |
| `TimeoutException` | No pooled connection free within `PoolWaitTimeoutMs` | Find leaked transactions or streams |
| `ObjectDisposedException` | Use after disposal (`IsHealthyAsync` returns `false` instead) | |
| `Microsoft.Data.Sqlite.SqliteException` | Not translated: no such table, UNIQUE constraint failed, `SQLITE_BUSY` (5), busy snapshot (517), `SQLITE_LOCKED` | Inspect `SqliteErrorCode` |

All library exceptions derive from `LiteDocumentStoreException : System.Data.DataException`.
`CorruptDataException` and `DocumentSerializationException` are siblings, so catch them
separately.

## Reference map

Read the file for the area you are touching before writing code in it:

| File | Read when |
|---|---|
| [references/setup-and-configuration.md](references/setup-and-configuration.md) | Choosing options or presets, the builder, DI (keyed / multiple DBs), a custom connection factory, table naming, pool sizing, health checks |
| [references/documents-and-concurrency.md](references/documents-and-concurrency.md) | CRUD, batches, ids, versions, CAS retry loops, not-found vs corrupt |
| [references/querying-and-patching.md](references/querying-and-patching.md) | `DocumentQuery<T>`, operators, value types, dates, paging deletes, `DocumentPatch<T>`, the JSON path grammar |
| [references/indexing-and-schema.md](references/indexing-and-schema.md) | Expression, composite, unique and partial indexes, virtual columns, dropping indexes, `EXPLAIN QUERY PLAN`, `SchemaIntrospector` |
| [references/transactions-and-raw-sql.md](references/transactions-and-raw-sql.md) | Transactions, `TransactionMode`, `SQLITE_BUSY` handling, `ExecuteRawAsync`, raw JSONB reads and writes, cancellation |
| [references/blobs.md](references/blobs.md) | Binary payloads, streaming upload and download, blob metadata and listing, blob CAS |
| [references/migrations.md](references/migrations.md) | Versioned schema changes, `SqlMigration`, custom `IMigration`, checksums, rollback |
| [references/aot-and-serialization.md](references/aot-and-serialization.md) | Native AOT or trimming, `JsonSerializerContext`, naming policies, what is not supported |
