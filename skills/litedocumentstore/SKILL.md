---
name: litedocumentstore
description: How to use the LiteDocumentStore .NET library (NuGet `LiteDocumentStore`) correctly from application code - a hybrid document + relational store over a single SQLite file using JSONB. Use whenever code references LiteDocumentStore, IDocumentStore, IDocumentOperations, IDocumentTransaction, DocumentStoreOptions, DocumentStoreFactory, AddLiteDocumentStore, DocumentQuery<T>, DocumentPatch<T>, IndexOptions, SqlMigration/IMigration, PutBlobAsync/OpenBlobReadAsync, ConcurrencyException or UpsertWithVersionAsync, or when the task is storing C# objects as JSON documents in SQLite from .NET (document CRUD, JSON-path queries, optimistic concurrency, expression indexes, blobs, migrations, Native AOT).
---

# LiteDocumentStore

Stores C# objects as SQLite **JSONB** in uniform tables (`id TEXT PRIMARY KEY, data BLOB, version
INTEGER`); the same tables stay open to raw SQL through `ExecuteRawAsync`. `net10.0`, SQLite 3.45+
(bundled), Native-AOT compatible. Namespace `LiteDocumentStore`; exceptions in
`LiteDocumentStore.Exceptions`.

## Setup

```csharp
// DI (always singleton; the container disposes it)
builder.Services.AddLiteDocumentStore(o => o.ConnectionString = "Data Source=app.db");

// Without DI (DocumentStore is internal) / in tests
await using IDocumentStore store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForFile("app.db"));
await using var test = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());

await store.CreateTableAsync<Customer>();   // nothing auto-creates tables; idempotent, run at startup
await store.CreateBlobTableAsync();         // only if you use blobs
```

## Core patterns

```csharp
await store.UpsertAsync("c1", customer);                      // id is a separate argument
Customer? c = await store.GetAsync<Customer>("c1");           // null when absent
var many = await store.GetManyAsync<Customer>(["c1", "c2"]);  // missing id = absent key

var q = DocumentQuery<Customer>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
    .AndIn("$.City", ["Seattle", "Denver"]).OrderBy("$.Age", descending: true).Take(10);
IEnumerable<Customer> page = await store.QueryAsync(q);

await store.PatchAsync("c1", DocumentPatch<Customer>.Set("$.City", "Boston").AndRemove("$.Email"));

var cur = await store.GetWithVersionAsync<Customer>("c1");
await store.UpsertWithVersionAsync("c1", cur!.Data with { Age = 41 }, cur.Version); // 0 = must not exist

await store.ExecuteInTransactionAsync(async tx =>
{
    var a = await tx.GetAsync<Customer>("c1");               // call operations ON tx
    await tx.UpsertAsync("c1", a! with { City = "Austin" });
}, TransactionMode.Immediate);

await store.CreateIndexAsync<Customer>(x => x.Email!, "idx_customer_email", new IndexOptions { Unique = true });

var table = store.GetTableName<Customer>();                   // never hardcode table names
long n = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT count(*) FROM [{table}] WHERE json_extract(data, '$.Age') > @a";
    cmd.Parameters.AddWithValue("@a", 30);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
});
```

## Rules that bite

1. **One store per database, shared and disposed.** It is thread-safe and owns a pool.
2. **Create tables first**, or you get a raw `SqliteException: no such table`.
3. **Keyword connection strings** (`"Data Source=app.db"`, or `ForFile`). Never `:memory:` — use
   `ForInMemory()` / `ForSharedInMemory(name)`.
4. **JSON paths name the serialized key** (`$.email` under camelCase). A wrong path fails silently.
   Only the expression DDL overloads (`x => x.Email`) resolve names for you.
5. **In a transaction, call `tx`, never `store`**, and always `await using` it. Use
   `TransactionMode.Immediate` for read-then-write (avoids unretryable `SQLITE_BUSY_SNAPSHOT`).
6. **`UpsertAsync` replaces the whole document.** Use `PatchAsync` for field changes and
   `*WithVersionAsync` when a write depends on a read.
7. **Table names are namespace-qualified** (`MyApp_Sales_Order`); use `GetTableName<T>()`. Moving a
   type to another namespace moves its table.
8. **Raw SQL keeps the JSONB contract**: write `jsonb(@Data)` with `SerializeDocument<T>` bytes, read
   `SELECT json(data)` + `DeserializeDocument<T>`, never `json_set`/`json_remove` on `data`. Each
   store-level `ExecuteRawAsync` opens a fresh connection — batch statements into one callback.
9. **Builders are immutable** (`DocumentQuery`, `DocumentPatch`, `IndexFilter`): use the returned value.
10. **Queries are AND-only.** `Count`/`Exists` ignore paging; `DeleteAsync(query)` honours it.
11. **No ranges over UTC/Local `DateTime` or `DateTimeOffset`** — store ticks or Unix ms instead.
12. **Values are scalars in your C# type**; they bind the way your serializer writes the path.
13. **The document type is the static `T`**: upserting a `Dog` through an `Animal` variable stores an
    `Animal`. Pass `<Dog>` or configure STJ polymorphism.
14. **Under AOT/trimming**, supply a source-generated `JsonSerializerContext` with every document type.
15. **Dispose `OpenBlobReadAsync` streams** — each holds a connection and a read lock.
16. **Pass mode/options explicitly**: a bare `default` binds to the `CancellationToken` overload.

## Exceptions

| Exception | Meaning |
|---|---|
| `ConcurrencyException` (`Kind`) | A versioned write/delete or a patch matched no row — re-read and retry |
| `CorruptDataException` | Row exists, payload unreadable (only raw SQL causes it) — delete or overwrite |
| `DocumentSerializationException` | JSON incompatible with `T`, or type missing from the context |
| `MigrationOutOfOrder/ChecksumMismatchException` | Back-filled or edited migration |
| `IncompatiblePageSize/UnsupportedSqliteVersionException` | Raised on open; `PageSize = 0` skips the check |
| `InvalidOperationException` | Table-name collision, index name with a different definition, tx used after end |
| `TimeoutException` | Pool exhausted — look for leaked transactions or streams |
| `SqliteException` | Untranslated: no such table, UNIQUE, `SQLITE_BUSY`/517, `SQLITE_LOCKED` |

`CorruptDataException` and `DocumentSerializationException` are siblings; catch them separately.

## Reference map

Read the file for the area you are touching before writing code in it:

| File | Read when |
|---|---|
| [setup-and-configuration](references/setup-and-configuration.md) | Options, presets, DI (keyed/multiple DBs), custom connection factory, table naming, pool, health |
| [documents-and-concurrency](references/documents-and-concurrency.md) | CRUD, batches, ids, versions, CAS retry loops, not-found vs corrupt |
| [querying-and-patching](references/querying-and-patching.md) | `DocumentQuery<T>`, operators, values, dates, paged deletes, `DocumentPatch<T>`, path grammar |
| [indexing-and-schema](references/indexing-and-schema.md) | Expression/composite/unique/partial indexes, virtual columns, `SchemaIntrospector` |
| [transactions-and-raw-sql](references/transactions-and-raw-sql.md) | Transactions, `SQLITE_BUSY`, `ExecuteRawAsync`, raw JSONB, cancellation |
| [blobs](references/blobs.md) | Binary payloads, streaming, blob metadata/listing, blob CAS |
| [migrations](references/migrations.md) | `SqlMigration`, custom `IMigration`, checksums, rollback |
| [aot-and-serialization](references/aot-and-serialization.md) | Native AOT, trimming, `JsonSerializerContext`, naming policies |
