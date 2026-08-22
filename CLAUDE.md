# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

LiteDocumentStore is a .NET library (published to NuGet as `LiteDocumentStore`) that turns a single SQLite `.db` file into a hybrid document + relational store. C# objects are serialized to JSON and stored in SQLite's **JSONB** binary format (requires SQLite 3.45+); the same tables stay fully accessible to raw SQL, joins, and indexes. The design goal is explicitly *not* an opaque document DB — users mix document-style CRUD and relational queries freely via the exposed `Connection`.

Solution is `src/LiteDocumentStore.slnx`. Target is `net10.0`, `LangVersion=latest` (C# 14), nullable + implicit usings on. Data access is raw ADO.NET over `Microsoft.Data.Sqlite` (no Dapper — parameters bound explicitly, results read by ordinal); serialization is `System.Text.Json`. The library is Native-AOT / trim compatible (`<IsAotCompatible>true</IsAotCompatible>`).

## Build, run, test

Solution and all commands live under `src/` (CI sets `working-directory: ./src`). Run from there.

```powershell
cd src

# Build (Release is what CI uses)
dotnet build --configuration Release

# Test — unit (fast, isolated) and integration (real in-memory SQLite) are separate projects
dotnet test tests/LiteDocumentStore.UnitTests/LiteDocumentStore.UnitTests.csproj
dotnet test tests/LiteDocumentStore.IntegrationTests/LiteDocumentStore.IntegrationTests.csproj

# Single test / filter (tests are tagged with [Trait("Category", ...)] and named Method_Scenario_Expected)
dotnet test --filter "Category=Unit"
dotnet test --filter "FullyQualifiedName~UpsertAndGet_RoundTrip"

# Benchmarks (BenchmarkDotNet)
dotnet run -c Release --project tests/LiteDocumentStore.Benchmarks
```

## Project layout

```
src/
  LiteDocumentStore.slnx                    Solution
  LiteDocumentStore/                         The library
    Core/            DocumentStore, DocumentOperations (shared op impls), SqliteConnectionPool
                     + PooledConnection, DocumentStoreTransaction, IDocumentStore /
                     IDocumentOperations / IDocumentTransaction, SqlGenerator,
                     SqliteCommandExtensions (raw ADO helpers), DocumentStoreOptions(+Builder)
    Conventions/     ITableNamingConvention — maps type -> table name
    Factories/       IDocumentStoreFactory, IConnectionFactory (+ Default impls)
    Extensions/      ServiceCollectionExtensions (AddLiteDocumentStore, keyed variant)
    Migrations/      MigrationRunner, IMigration/Migration, SchemaIntrospector
    Serialization/   JsonHelper (STJ, via JsonTypeInfo<T>)
    Exceptions/      LiteDocumentStoreException + Concurrency/Serialization/TableNotFound
                     (ConcurrencyException is thrown by UpsertWithVersionAsync on CAS conflicts)
  tests/
    LiteDocumentStore.UnitTests/             xUnit, mocked/isolated
    LiteDocumentStore.IntegrationTests/      xUnit, real SQLite (mostly :memory:)
    LiteDocumentStore.Benchmarks/            BenchmarkDotNet
```

`DocumentStore` and most internals are `internal sealed`; the test/benchmark projects see them via `InternalsVisibleTo` in the csproj. Consumers only touch the public surface: `IDocumentStore`, `DocumentStoreOptions`, the factories, and the DI extension.

> Note: the `tests/` projects were updated for the Dapper removal (tests that only covered the dropped `QueryAsync(predicate)` / `SelectAsync` APIs were removed; the rest use the raw-ADO helpers in `Core/SqliteCommandExtensions.cs`). The `benchmarks/` project intentionally keeps a `Dapper` package reference as a *comparison baseline* only — it is not a library dependency. All three projects compile and `dotnet test` passes.

## Documentation is stale — trust the code

`README.md` and `.github/instructions/*.md` (Copilot rules) describe an older `Repository` class with a `new Repository("app.db")` constructor and a `SqliteJsonbTypeHandler`. **That API no longer exists.** The real entry point is `IDocumentStore`, obtained through DI (`services.AddLiteDocumentStore(...)`) or `IDocumentStoreFactory.Create/CreateAsync(DocumentStoreOptions)`. When those docs conflict with the source, the source wins.

The loose `.cs` files under `examples/` are documentation, not a compiled project, and most are stale twice over: they call Dapper extension methods on a `store.Connection` property that no longer exists (see the connection model below). Treat them as prose until they are rewritten against `ExecuteRawAsync`. The *conceptual* guidance in those files (JSONB read/write pattern, WAL config, hybrid philosophy, SQLite error codes) is still accurate.

## Architecture

**All SQL is centralized in `SqlGenerator`** (static, one method per statement shape). Nothing else hand-writes SQL against document tables. Table schema is uniform: `id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 0`. The JSONB contract, enforced there, is load-bearing:
- **Write:** `jsonb(@Data)` — `@Data` is UTF-8 JSON *bytes* from `JsonHelper.SerializeToUtf8Bytes`, not a string.
- **Read:** `SELECT json(data)` — converts JSONB binary back to JSON text for deserialization. JSONB is binary; a raw `SELECT data` is not deserializable.
- All *values* are parameterized. Identifiers and JSON paths **cannot** be, so they are interpolated — and every one is validated inside `SqlGenerator`, the single boundary where that happens: `ValidateIdentifier` restricts table/index/column names to `[A-Za-z_][A-Za-z0-9_]*` (bracket quoting alone is not enough — a `]` closes it early), `ValidateJsonPath` enforces `$(.member|[index])*` (a `'` would close the SQL literal `json_extract(data, '…')` sits in), and `ValidateColumnType` whitelists the five SQLite storage classes. Table names come from `ITableNamingConvention`, which is pluggable, so they are validated like anything else.
- The JSON path in `QueryAsync` is interpolated **on purpose**, not bound: SQLite only matches a query against an expression index when the indexed expression appears literally, so binding the path would silently disable every index `CreateIndexAsync` creates. `tests/LiteDocumentStore.IntegrationTests/SqlInjectionIntegrationTests.cs` pins both halves — the injection is rejected, and the index is still used.
- `GenerateQueryWithWhereSql` / `GenerateSelectFieldsSql` / `GenerateSelectFieldsWithWhereSql` take raw SQL fragments and are **unreachable dead code** left from the removed projection APIs. Do not wire them to caller-facing input; delete them instead.

**Connection model.** A `DocumentStore` owns a `SqliteConnectionPool` (`Core/SqliteConnectionPool.cs`) and **rents a connection per operation**, so the store is thread-safe and safe to share across requests. Connections are opened + PRAGMA-configured **once** by `DefaultConnectionFactory` from `DocumentStoreOptions`, then reused; they are never closed while the pool lives, only returned to an idle bag. `DocumentStoreOptions.MaxPoolSize` caps them (default: processor count clamped to [2, 16]).

The store deliberately opts **out** of Microsoft.Data.Sqlite's own pool (`Pooling=False`, forced in `SqliteConnectionPool.Normalize`). That pool gives no "this handle is new" hook, so every rent would have to re-apply the session PRAGMAs — measured at +3 µs, or +68% on a 4.5 µs read. Owning the pool makes renting a semaphore wait plus a bag pop.

Measured cost of the model (`tests/LiteDocumentStore.Benchmarks/ConnectionModelBenchmark.cs` — `ConnectionModelBenchmark` for the primitives, `StorePathBenchmark` for the store's own path; re-run both before changing this design):

| | vs the old single held connection |
|---|---|
| File DB read | **+2.1%** |
| Shared-cache in-memory read | +8.6% (the op is only ~9 µs, so fixed cost weighs more) |
| Allocations | +10% (~160 B: the lease plus the closure in `DocumentStore.RunAsync`) |
| 64 concurrent reads | **3x faster** than a `SemaphoreSlim(1)`-gated single connection |

In-memory users pay a further ~8% for shared-cache locking, which pooling requires (see below). If the per-op allocation ever matters, the closure in `RunAsync` can be removed by inlining rent/dispose into each of the 20 operations — measured as the smaller half of the +2.1%, so it was not judged worth ~80 lines.

Because connections are pooled, a **private** in-memory database is rejected: `Data Source=:memory:` (or `Mode=Memory` without `Cache=Shared`) throws `ArgumentException`, since each pooled connection would get its own empty database. `DocumentStoreOptions.ForInMemory()` therefore returns a *uniquely named shared-cache* memory DB, and the pool eagerly opens one connection at initialization to keep it alive. Caveat: shared-cache in-memory DBs use table-level locks, so overlapping **write transactions** on one in-memory database fail with `SQLITE_LOCKED`, which `busy_timeout` does not retry — concurrency tests need a real file DB.

Disposal runs `PRAGMA wal_checkpoint(TRUNCATE)` on a rented connection, then closes the pool. That rent is **bounded** (`DocumentStore.WalCheckpointRentTimeout`, 5 s) — an unbounded wait would let one leaked lease hang `Dispose` forever — and the checkpoint is skipped on timeout, which costs only the `TRUNCATE`: SQLite checkpoints the WAL itself when the last connection closes. Same reason the checkpoint is gated on `EnableWalMode` up front, so a non-WAL store pays no rent + `PRAGMA journal_mode` round trip per dispose; the trade-off is that an existing WAL database opened with `EnableWalMode = false` skips it.

The pool **never disposes its `SemaphoreSlim`**. `SemaphoreSlim.Dispose()` is safe only once every other operation on it has finished; with a waiter parked in `WaitAsync` it clears the waiter list without completing it, so an operation queued for a connection while the store was disposed would hang forever. A `SemaphoreSlim` that never exposes `AvailableWaitHandle` holds no unmanaged resource, so there is nothing to release. `Return`/`Discard` release the slot **even on the disposed path** — that is what wakes a parked waiter, which then throws `ObjectDisposedException` from `ThrowIfDisposed` instead of hanging.

`_disposed` is an `int` flipped with `Interlocked.Exchange`, so double-dispose is atomic and every operation guard (`ThrowIfDisposed`) sees it. The DI registration is **Singleton only** — the `ServiceLifetime` parameter is gone, because a thread-safe store with its own pool has nothing for a scoped registration to isolate.

**Querying.** Documents are queried by JSON path + value via `QueryAsync<T, TValue>(jsonPath, value)`, which builds `WHERE json_extract(data, '$.Path') = @Value`. The LINQ-predicate `QueryAsync<T>(Expression<Func<T,bool>>)` and the `SelectAsync` projections were **removed** (they required runtime reflection / IL generation that AOT can't support). `CreateIndexAsync`, `CreateCompositeIndexAsync`, and `AddVirtualColumnAsync` still accept LINQ expressions, but only walk **member names** (`DocumentStore.ExtractJsonPath`) to build `$.Path` — no compilation or closure evaluation, so they stay AOT-safe. Property names map **as-is (PascalCase)** to match default STJ serialization. For richer filtering (ranges, virtual-column index seeks, joins), drop to raw SQL via `ExecuteRawAsync`.

**Optimistic concurrency.** Every document row carries a `version` (starts at 1 on insert, incremented on every write, including plain `UpsertAsync`/`UpsertManyAsync`). `UpsertWithVersionAsync<T>(id, data, expectedVersion)` is the compare-and-swap write: `expectedVersion == 0` means "insert, must not exist"; non-zero means "update only if the stored version matches". A 0-row write throws `ConcurrencyException` (DocumentId + TableName). `GetWithVersionAsync<T>` returns `VersionedDocument<T>(Data, Version)` for read-modify-write cycles.

**Blobs.** Raw binary payloads live in a reserved `__store_blobs (id TEXT PK, data BLOB NOT NULL)` table (`SqlGenerator.BlobTableName`), created via `CreateBlobTableAsync()`. `PutBlobAsync`/`GetBlobAsync`/`DeleteBlobAsync`/`BlobExistsAsync` — no JSONB conversion, bytes stored verbatim. Blob operations exist on `IDocumentOperations`, so calling them on an `IDocumentTransaction` commits a document and its blob atomically.

**Transactions.** `BeginTransactionAsync()` returns an `IDocumentTransaction` (`Core/DocumentStoreTransaction.cs`) that holds **one rented connection** for its lifetime; `CommitAsync`/`RollbackAsync` finish it, and disposing without committing rolls back. `ExecuteInTransactionAsync(Func<IDocumentTransaction, Task>)` is the ergonomic wrapper (commit on return, rollback on throw).

The transaction object is the unit of work: operations must be invoked **on it**, because operations invoked on the store rent their own connection and commit independently. That is the point of the design — under the old shared-connection model, commands auto-enlisted in whatever transaction happened to be open on that connection, so a concurrent request's writes silently joined another's transaction and were rolled back with it. Two transactions now run on two connections and cannot see or roll back each other. Batch writes still go through `UpsertManyAsync`/`DeleteManyAsync`, which build a single multi-row statement with explicit `@Id{i}`/`@Data{i}` parameters.

**Raw SQL escape hatch.** The `Connection` property is gone (a pooled connection cannot be handed out to live indefinitely). Use `ExecuteRawAsync(Func<SqliteConnection, CancellationToken, Task<T>>)`, available on both the store and a transaction — on a transaction the callback gets that transaction's connection, so raw commands enlist in it. The connection is valid only inside the callback. Create commands with `connection.CreateCommand()`, which copies the connection's active transaction onto the command — a directly constructed `new SqliteCommand(sql, connection)` leaves `Transaction` null and Microsoft.Data.Sqlite refuses to execute it while a transaction is pending (pinned by `Transaction_ExecuteRawAsync_EnlistsOnlyCommandsCreatedFromTheConnection`).

**Migrations.** `MigrationRunner` tracks applied versions in a `__store_migrations` table; `IMigration` implementations provide `UpAsync`/`DownAsync`. Each apply/rollback is transactional.

## AOT compatibility

The library is Native-AOT / trim compatible as a **single package** (`<IsAotCompatible>true</IsAotCompatible>` turns on the trim + AOT analyzers). How each former blocker was handled:

1. **Serialization** — `JsonHelper` goes through the AOT-safe `JsonTypeInfo<T>` overloads, resolving type metadata from `DocumentStoreOptions.SerializerOptions`. AOT consumers pass options backed by a source-generated `JsonSerializerContext` (`new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }`). When none is supplied, `JsonHelper.CreateDefaultReflectionOptions()` provides a reflection fallback — the single quarantined, `[UnconditionalSuppressMessage]`-annotated spot, used only in non-AOT scenarios.
2. **Dapper** — removed entirely, replaced by `Core/SqliteCommandExtensions.cs` (explicit parameter binding + ordinal reads).
3. **LINQ-predicate query + `SelectAsync` projections** — removed (needed reflection/`Expression.Compile`). `ExpressionToJsonPath` was deleted; the surviving expression APIs only read member names.
4. **`SchemaIntrospector`** — the `dynamic` PRAGMA read was rewritten to ordinal `DbDataReader` access.

When adding features, keep them AOT-clean: no reflection-based serialization (route through `JsonHelper` + `JsonTypeInfo<T>`), no `Expression.Compile`, no `dynamic`. A `dotnet build` must stay free of `IL2xxx`/`IL3xxx` warnings.

## Conventions

- File-scoped namespaces; `sealed` on non-inheritable classes; `readonly` fields; `_camelCase` private fields. Expression-bodied members for one-liners.
- Library code uses `.ConfigureAwait(false)` on awaits and `Async` suffix on async methods.
- Validate arguments up front and fail fast (`ArgumentException`/`ArgumentNullException.ThrowIfNull`). Rethrow inside `catch` on transaction rollback rather than wrapping.
- All public API needs XML doc comments (`GenerateDocumentationFile` is on; missing docs surface as warnings).
- Package versions are inline `<PackageReference Version="...">` in the csproj — there is no Central Package Management here.
- New features need both a unit and an integration test.
- Don't add AI-attribution trailers (`Co-Authored-By: Claude`, "Generated with Claude Code") to commits or PRs.
- Never auto-commit — stage and commit only when the user explicitly asks.

## Where to look first

- `src/LiteDocumentStore/Core/DocumentStore.cs` — the public surface: rents a connection per operation and delegates to `DocumentOperations`.
- `src/LiteDocumentStore/Core/DocumentOperations.cs` — where every document operation is actually implemented (shared by the store and by transactions).
- `src/LiteDocumentStore/Core/SqliteConnectionPool.cs` — connection lifetime, PRAGMA-once configuration, the `:memory:` guard.
- `src/LiteDocumentStore/Core/SqlGenerator.cs` — the JSONB SQL contract; change SQL here, nowhere else.
- `src/LiteDocumentStore/Core/SqliteCommandExtensions.cs` — the raw ADO.NET helpers that replaced Dapper.
- `src/LiteDocumentStore/Serialization/JsonHelper.cs` — the AOT-safe serialization funnel (`JsonTypeInfo<T>` + reflection fallback).
- `src/LiteDocumentStore/Extensions/ServiceCollectionExtensions.cs` — how consumers wire it up (DI + lifetimes).
- `examples/AotVerification.cs` — end-to-end AOT smoke test with a source-generated context.
</content>
