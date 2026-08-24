# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

LiteDocumentStore is a .NET library (published to NuGet as `LiteDocumentStore`) that turns a single SQLite `.db` file into a hybrid document + relational store. C# objects are serialized to JSON and stored in SQLite's **JSONB** binary format (requires SQLite 3.45+); the same tables stay fully accessible to raw SQL, joins, and indexes. The design goal is explicitly *not* an opaque document DB — users mix document-style CRUD and relational queries freely via the exposed `Connection`.

Solution is `LiteDocumentStore.slnx` at the repository root. Target is `net10.0`, `LangVersion=latest` (C# 14), nullable + implicit usings on. Data access is raw ADO.NET over `Microsoft.Data.Sqlite` (no Dapper — parameters bound explicitly, results read by ordinal); serialization is `System.Text.Json`. The library is Native-AOT / trim compatible (`<IsAotCompatible>true</IsAotCompatible>`).

## Build, run, test

Everything runs from the repository root — the solution is there, so no command needs a path.

```powershell
# Build (Release is what CI uses)
dotnet build --configuration Release

# Test — unit (fast, isolated) and integration (real in-memory SQLite) are separate projects
dotnet test tests/LiteDocumentStore.UnitTests/LiteDocumentStore.UnitTests.csproj
dotnet test tests/LiteDocumentStore.IntegrationTests/LiteDocumentStore.IntegrationTests.csproj

# Single test / filter (tests are tagged with [Trait("Category", ...)] and named Method_Scenario_Expected)
dotnet test --filter "Category=Unit"
dotnet test --filter "FullyQualifiedName~UpsertAndGet_RoundTrip"

# Examples (every sample, or one by name)
dotnet run --project examples/Examples -- all

# Benchmarks (BenchmarkDotNet)
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks
```

## Project layout

```
LiteDocumentStore.slnx                       Solution (repository root)
Directory.Build.props                        Deterministic + ContinuousIntegrationBuild (CI only)
src/
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
    Exceptions/      LiteDocumentStoreException + Concurrency/Serialization/TableNotFound/
                     UnsupportedSqliteVersion (ConcurrencyException + ConcurrencyConflictKind
                     come from Upsert/DeleteWithVersionAsync on CAS conflicts;
                     UnsupportedSqliteVersionException from the 3.45+ guard on connection open)
tests/
  LiteDocumentStore.UnitTests/               xUnit, mocked/isolated
  LiteDocumentStore.IntegrationTests/        xUnit, real SQLite (mostly :memory:)
benchmarks/
  LiteDocumentStore.Benchmarks/              BenchmarkDotNet
examples/
  Examples/                                  Every sample, dispatched by name from Program.cs
  AotVerification/                           Native AOT gate, published + run by CI
```

`DocumentStore` and most internals are `internal sealed`; the test/benchmark projects see them via `InternalsVisibleTo` in the csproj. Consumers only touch the public surface: `IDocumentStore`, `DocumentStoreOptions`, the factories, and the DI extension.

> Note: the `tests/` projects were updated for the Dapper removal (tests that only covered the dropped `QueryAsync(predicate)` / `SelectAsync` APIs were removed; the rest use the raw-ADO helpers in `Core/SqliteCommandExtensions.cs`). The `benchmarks/` project intentionally keeps a `Dapper` package reference as a *comparison baseline* only — it is not a library dependency. All three projects compile and `dotnet test` passes.

## Documentation is stale — trust the code

`README.md` and `.github/instructions/*.md` (Copilot rules) describe an older `Repository` class with a `new Repository("app.db")` constructor and a `SqliteJsonbTypeHandler`. **That API no longer exists.** The real entry point is `IDocumentStore`, obtained through DI (`services.AddLiteDocumentStore(...)`) or `IDocumentStoreFactory.Create/CreateAsync(DocumentStoreOptions)`. When those docs conflict with the source, the source wins.

`examples/` is now two real projects in the `.slnx`, so samples cannot rot silently: `examples/Examples` is one console app holding every sample (`dotnet run --project examples/Examples -- all`, or a single name — CI runs `all` on every push), and `examples/AotVerification` is the Native AOT gate that CI publishes with `-warnaserror` and then runs. The nine old loose file-based `.cs` samples were rewritten against `ExecuteRawAsync` and deleted.

## Architecture

**All SQL is centralized in `SqlGenerator`** (static, one method per statement shape). Nothing else hand-writes SQL against document tables. Table schema is uniform: `id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1`. The JSONB contract, enforced there, is load-bearing:
- **Write:** `jsonb(@Data)` — `@Data` is UTF-8 JSON *bytes* from `JsonHelper.SerializeToUtf8Bytes`, not a string.
- **Read:** `SELECT json(data)` — converts JSONB binary back to JSON text for deserialization. JSONB is binary; a raw `SELECT data` is not deserializable.
- All *values* are parameterized. Identifiers and JSON paths **cannot** be, so they are interpolated — and every one is validated inside `SqlGenerator`, the single boundary where that happens: `ValidateIdentifier` restricts table/index/column names to `[A-Za-z_][A-Za-z0-9_]*` (bracket quoting alone is not enough — a `]` closes it early), `ValidateJsonPath` enforces `$(.member|[index])*` (a `'` would close the SQL literal `json_extract(data, '…')` sits in), and `ValidateColumnType` whitelists the five SQLite storage classes. Table names come from `ITableNamingConvention`, which is pluggable, so they are validated like anything else.
- The JSON path in `QueryAsync` is interpolated **on purpose**, not bound: SQLite only matches a query against an expression index when the indexed expression appears literally, so binding the path would silently disable every index `CreateIndexAsync` creates. `tests/LiteDocumentStore.IntegrationTests/SqlInjectionIntegrationTests.cs` pins both halves — the injection is rejected, and the index is still used.
- Every generator validates its identifiers and paths. The three that did not — `GenerateQueryWithWhereSql` / `GenerateSelectFieldsSql` / `GenerateSelectFieldsWithWhereSql`, dead leftovers of the removed projection APIs that took raw SQL fragments — were **deleted**. Do not reintroduce a generator that accepts a caller-supplied SQL fragment; `ExecuteRawAsync` is the escape hatch for that.

**Connection model.** A `DocumentStore` owns a `SqliteConnectionPool` (`Core/SqliteConnectionPool.cs`) and **rents a connection per operation**, so the store is thread-safe and safe to share across requests. Connections are opened + PRAGMA-configured **once** by `DefaultConnectionFactory` from `DocumentStoreOptions`, then reused; they are never closed while the pool lives, only returned to an idle bag. `DocumentStoreOptions.MaxPoolSize` caps them (default: processor count clamped to [2, 16]).

The store deliberately opts **out** of Microsoft.Data.Sqlite's own pool (`Pooling=False`, forced in `SqliteConnectionPool.Normalize`). That pool gives no "this handle is new" hook, so every rent would have to re-apply the session PRAGMAs — measured at +3 µs, or +68% on a 4.5 µs read. Owning the pool makes renting a semaphore wait plus a bag pop.

Measured cost of the model (`benchmarks/LiteDocumentStore.Benchmarks/ConnectionModelBenchmark.cs` — `ConnectionModelBenchmark` for the primitives, `StorePathBenchmark` for the store's own path; re-run both before changing this design):

| | vs the old single held connection |
|---|---|
| File DB read | **+2.1%** |
| Shared-cache in-memory read | +8.6% (the op is only ~9 µs, so fixed cost weighs more) |
| Allocations | +10% (~160 B: the lease plus the closure in `DocumentStore.RunAsync`) |
| 64 concurrent reads | **3x faster** than a `SemaphoreSlim(1)`-gated single connection |

In-memory users pay a further ~8% for shared-cache locking, which pooling requires (see below). If the per-op allocation ever matters, the closure in `RunAsync` can be removed by inlining rent/dispose into each of the 20 operations — measured as the smaller half of the +2.1%, so it was not judged worth ~80 lines.

Because connections are pooled, a **private** in-memory database is rejected: `Data Source=:memory:` (or `Mode=Memory` without `Cache=Shared`) throws `ArgumentException`, since each pooled connection would get its own empty database. `DocumentStoreOptions.ForInMemory()` therefore returns a *uniquely named shared-cache* memory DB, and the pool eagerly opens one connection at initialization to keep it alive. Caveat: shared-cache in-memory DBs use table-level locks, so overlapping **write transactions** on one in-memory database fail with `SQLITE_LOCKED`, which `busy_timeout` does not retry — concurrency tests need a real file DB.

**SQLite version guard.** `jsonb()` is the library's core premise and shipped in SQLite 3.45.0, so `SqliteConnectionPool` runs `SqliteVersionGuard.EnsureSupported(Async)` on **every physical connection as it is opened** and throws `UnsupportedSqliteVersionException` (carrying `ActualVersion` + `MinimumVersion`) instead of letting the first write fail with `no such function: jsonb`. The guard lives in the pool, not in `DefaultConnectionFactory`, because `IConnectionFactory` is public — a consumer-supplied factory would otherwise open unguarded connections. The async path reads the version through `SchemaIntrospector.GetSqliteVersionAsync`; the sync path queries `SELECT sqlite_version()` directly (the introspector is async-only). The result is deliberately **not cached**: it is process-wide constant, but a pool opens at most `MaxPoolSize` connections in its lifetime, so caching would trade process-wide mutable state for a handful of sub-microsecond queries. `IsHealthyAsync` re-checks through the same guard and maps the exception to `false` at warning level. Because SQLite lets a user-defined function override a built-in, the too-old path is actually testable — `SqliteVersionGuardIntegrationTests` spoofs `sqlite_version()` to `3.44.2`.

Disposal runs `PRAGMA wal_checkpoint(TRUNCATE)` on a rented connection, then closes the pool. That rent is **bounded** (`DocumentStore.WalCheckpointRentTimeout`, 5 s) — an unbounded wait would let one leaked lease hang `Dispose` forever — and the checkpoint is skipped on timeout, which costs only the `TRUNCATE`: SQLite checkpoints the WAL itself when the last connection closes. Same reason the checkpoint is gated on `EnableWalMode` up front, so a non-WAL store pays no rent + `PRAGMA journal_mode` round trip per dispose; the trade-off is that an existing WAL database opened with `EnableWalMode = false` skips it.

The pool **never disposes its `SemaphoreSlim`**. `SemaphoreSlim.Dispose()` is safe only once every other operation on it has finished; with a waiter parked in `WaitAsync` it clears the waiter list without completing it, so an operation queued for a connection while the store was disposed would hang forever. A `SemaphoreSlim` that never exposes `AvailableWaitHandle` holds no unmanaged resource, so there is nothing to release. `Return`/`Discard` release the slot **even on the disposed path** — that is what wakes a parked waiter, which then throws `ObjectDisposedException` from `ThrowIfDisposed` instead of hanging.

`_disposed` is an `int` flipped with `Interlocked.Exchange`, so double-dispose is atomic and every operation guard (`ThrowIfDisposed`) sees it. The DI registration is **Singleton only** — the `ServiceLifetime` parameter is gone, because a thread-safe store with its own pool has nothing for a scoped registration to isolate.

**Cancellation.** Every async member takes `CancellationToken cancellationToken = default` — the ~20 document/blob operations on `IDocumentOperations`, `IsHealthyAsync`, both `ExecuteRawAsync` overloads, `BeginTransactionAsync`/`ExecuteInTransactionAsync`, `CommitAsync`/`RollbackAsync`, all of `MigrationRunner` and `SchemaIntrospector`, `IMigration.UpAsync`/`DownAsync`, `IDocumentStoreFactory.CreateAsync`, and `IConnectionFactory`. On the store the token does two things: it cancels the wait for a free pooled connection (`RunAsync` passes it to `_pool.RentAsync`, which is why that private helper takes it *and* the caller's lambda captures it), and it reaches the ADO command. It cannot interrupt a statement already running — Microsoft.Data.Sqlite does SQLite I/O synchronously — so a cancelled token is observed *before* the command starts, never part-way through. On a transaction there is no rent, so the command is the only cancellation point; `CancellationTests.TransactionOperation_WithAnAlreadyCancelledToken_Throws` is the test that actually pins the token reaching ADO.

The helpers in `Core/SqliteCommandExtensions.cs` take the token **before** their trailing `params (string, object?)[]` — `ExecuteAsync(sql, cancellationToken, ("Id", id))`. C# allows one params parameter and it must come last; dropping `params` so the token could trail would force an explicit array at every call site, including the many that bind nothing. The two synchronous helpers (`Execute`, `QueryFirstString`) stay tokenless: every path that uses them is itself synchronous and has no caller token — the PRAGMA configuration in `DefaultConnectionFactory.ConfigureConnection`, `SqliteVersionGuard.EnsureSupported` on the sync connection-open path, and the WAL checkpoint on disposal.

**Querying.** Two APIs, both AOT-safe. The simple one is `QueryAsync<T, TValue>(jsonPath, value)` — `WHERE json_extract(data, '$.Path') = @Value`. The composable one is `DocumentQuery<T>` (`Query/`), an immutable builder consumed by `QueryAsync<T>(DocumentQuery<T>)`, `CountAsync<T>(DocumentQuery<T>)` and `ExistsAsync<T>(DocumentQuery<T>)` on `IDocumentOperations`, so it works on the store and inside a transaction:

```csharp
var q = DocumentQuery<Customer>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
                               .AndIn("$.City", ["Boston", "Denver"])
                               .AndIsNotNull("$.Email")
                               .OrderBy("$.Age", descending: true)
                               .Skip(10).Take(20);
```

Operators: `Equal`, `NotEqual`, `GreaterThan(OrEqual)`, `LessThan(OrEqual)`, `Like`, `Glob`, `In`, `IsNull`, `IsNotNull`, `ArrayContains` (`EXISTS (SELECT 1 FROM json_each(data, '$.Tags') WHERE value = @p0)`). Predicates combine with **AND only** — no OR groups. `Skip` without `Take` emits `LIMIT -1 OFFSET n`, which SQLite requires.

`CountAsync` and `ExistsAsync` apply the query's **predicates only** — ordering and paging are ignored, so `ExistsAsync` on a query paged past the end of its match still reports `true`. `GenerateFilteredExistsSql` wraps a `LIMIT 1` subquery in `SELECT EXISTS(...)`, so it stops at the first matching row instead of counting them all, and it reuses the same `AppendWhere` pass as the count, which is what keeps the interpolated path matching a `CreateIndexAsync` expression index (pinned by an `EXPLAIN QUERY PLAN` assertion in `DocumentQueryIntegrationTests`). One API-shape consequence: `ExistsAsync<T>` is now overloaded on `string id` and `DocumentQuery<T>`, so a bare `ExistsAsync<T>(null!)` no longer compiles — the call needs a cast to say which overload it means.

The builder never accepts a SQL fragment: `SqlGenerator.GenerateQuerySql`/`GenerateFilteredCountSql`/`GenerateFilteredExistsSql` take the structured `QueryPredicate`/`QueryOrdering` records and return a `GeneratedQuery(Sql, ParameterValues)` — values bound `@p0..@pN` in one left-to-right pass, so SQL and parameter order cannot drift. Paths are still interpolated (validated by `ValidateJsonPath`) for the index-matching reason above; a query binding more than `SqlGenerator.MaxBoundParameters` (900) throws rather than hitting `SQLITE_MAX_VARIABLE_NUMBER`. Arguments are validated at *build* time, so a bad path or a nonsensical operator/value pairing throws at the call site.

**Bound values are normalized to what STJ wrote into the document** (`DocumentQuery<T>.NormalizeBoundValue`, shared with the older overload). ADO otherwise binds a shape that matches nothing *silently*: `DateTime`/`DateTimeOffset` as `"2024-03-01 00:00:00"` vs the stored `"2024-03-01T00:00:00"`, `byte[]` as a blob vs base64 text, `decimal` as TEXT vs the REAL `json_extract` yields, `float` widened instead of round-tripped, `ulong` above `long.MaxValue` wrapped negative. Each was measured against real SQLite, not assumed. This assumes default serialization — a custom converter for one of those types breaks the alignment.

The LINQ-predicate `QueryAsync<T>(Expression<Func<T,bool>>)` and the `SelectAsync` projections stay **removed** (runtime reflection / IL generation that AOT can't support). `CreateIndexAsync`, `CreateCompositeIndexAsync`, `DropIndexAsync<T>`, and `AddVirtualColumnAsync` still accept LINQ expressions, but only walk **member names** (`DocumentStore.ExtractJsonPath`) to build `$.Path` — no compilation or closure evaluation, so they stay AOT-safe. Property names map **as-is (PascalCase)** to match default STJ serialization. For joins, aggregates and virtual-column seeks, drop to raw SQL via `ExecuteRawAsync`.

**Optimistic concurrency.** Every document row carries a `version` (starts at 1 on insert, incremented on every write, including plain `UpsertAsync`/`UpsertManyAsync`). The DDL default is `1` too, so a row a consumer inserts with their own SQL is CAS-able like any other. `GetWithVersionAsync<T>` returns `VersionedDocument<T>(Data, Version)` for read-modify-write cycles.

`UpsertWithVersionAsync<T>(id, data, expectedVersion)` is the compare-and-swap write and `DeleteWithVersionAsync<T>(id, expectedVersion)` the compare-and-swap delete (added because a plain `DeleteAsync` ignores the version, so a read-modify-delete could silently drop a concurrent update). Non-zero `expectedVersion` means "only if the stored version matches" on both. `0` does **not** mean the same thing on both: on the write it means "insert, must not exist" — and when the id *is* taken, the write retries as a `version = 0`-guarded update, so a legacy row left at 0 by the old column default is lifted to 1 instead of being un-CAS-able forever — while on the delete it carries no insert sense at all and simply matches a row still sitting at 0, which is how such a legacy row is removed. Both write paths end in `RETURNING version`, so the returned value is the version SQLite stored, not `expectedVersion + 1`.

A 0-row write or delete throws `ConcurrencyException` carrying `DocumentId`, `TableName`, `ExpectedVersion`, `ActualVersion` and a `ConcurrencyConflictKind` (`AlreadyExists` / `VersionMismatch` / `DocumentNotFound`), so a caller picks a retry strategy from the enum instead of string-matching the message. `ActualVersion` costs one `SELECT version` on the conflict path only — the happy path pays nothing for it.

`DocumentOperations.BuildConflictAsync` classifies that conflict, and takes insert intent as an explicit `insertAttempt` flag rather than inferring it from `expectedVersion == 0`: `AlreadyExists` is only reachable from the write's insert branch, so a versioned *delete* rejected by a row at version 1 reports `VersionMismatch`, as the enum's own contract says it should. Inferring intent from the number alone mislabelled exactly the delete-a-legacy-row-at-0 case the previous paragraph exists to support.

Both `ActualVersion` and `Kind` are **a post-conflict observation**, and their XML docs say so. The stored-version read is a separate statement from the guarded mutation, so outside a transaction another connection can update, delete or recreate the row in between; the values are exact only when the operation runs through an existing transaction, which holds the SQLite locks across both statements. Deliberately not fixed by wrapping every guarded write in a transaction: that taxes the happy path to make failure metadata temporally exact, and opening a transaction *after* the 0-row result buys nothing — the race is already over. Nor can one statement do it: `UPDATE … RETURNING` yields no row on failure, and `;`-chained statements are not atomic.

**Null documents are never dropped.** A row whose stored JSON deserializes to null (only reachable through raw SQL — every store write rejects a null document) used to be skipped, so `GetAllAsync`/`QueryAsync` returned fewer documents than the table held, and `GetWithVersionAsync`/`GetAsync` returned null, indistinguishable from not-found. All of them now throw `SerializationException` naming the id and table. That is why `GenerateGetAllSql`, `GenerateQueryByJsonPathSql` and `GenerateQuerySql` select `id, json(data)` and read through `QueryStringPairsAsync`: the id has to travel with the document to be named. A genuinely absent id is still absent, not an error — `GetAsync` returns `default`, `GetManyAsync` omits the key.

**Blobs.** Raw binary payloads live in a reserved `__store_blobs (id TEXT PK, data BLOB NOT NULL)` table (`SqlGenerator.BlobTableName`), created via `CreateBlobTableAsync()`. `PutBlobAsync`/`GetBlobAsync`/`DeleteBlobAsync`/`BlobExistsAsync` — no JSONB conversion, bytes stored verbatim. Blob operations exist on `IDocumentOperations`, so calling them on an `IDocumentTransaction` commits a document and its blob atomically.

**Transactions.** `BeginTransactionAsync()` returns an `IDocumentTransaction` (`Core/DocumentStoreTransaction.cs`) that holds **one rented connection** for its lifetime; `CommitAsync`/`RollbackAsync` finish it, and disposing without committing rolls back. `ExecuteInTransactionAsync(Func<IDocumentTransaction, Task>)` is the ergonomic wrapper (commit on return, rollback on throw). Every operation on the transaction object goes through `ActiveTransaction()` first, so a call made after commit/rollback/disposal throws (`InvalidOperationException`, or `ObjectDisposedException` once disposed) instead of running on a connection the pool has already handed to another renter.

The transaction object is the unit of work: operations must be invoked **on it**, because operations invoked on the store rent their own connection and commit independently. That is the point of the design — under the old shared-connection model, commands auto-enlisted in whatever transaction happened to be open on that connection, so a concurrent request's writes silently joined another's transaction and were rolled back with it. Two transactions now run on two connections and cannot see or roll back each other. Batch writes still go through `UpsertManyAsync`/`DeleteManyAsync`, which build multi-row statements with explicit `@Id{i}`/`@Data{i}` parameters.

**Batch chunking.** A batch is split into chunks of `SqlGenerator.MaxBatchItemsPerStatement` (500) items — an upsert binds 2N parameters, so one unbounded statement blew past `SQLITE_MAX_VARIABLE_NUMBER` (32766) at ~16383 items and approached `SQLITE_MAX_SQL_LENGTH`. `GenerateBulkUpsertSql`/`GenerateBulkDeleteSql` throw `ArgumentOutOfRangeException` above the cap, so a future caller cannot reintroduce the unbounded shape. `DocumentOperations.RunBatchAsync` runs the chunk loop and sums affected rows; a multi-chunk batch is wrapped in a transaction so it stays all-or-nothing, and a single-chunk batch is left alone (one statement is already atomic). `DocumentOperations` takes an `inAmbientTransaction` flag — `true` from `DocumentStoreTransaction`, `false` from `DocumentStore` — so inside `ExecuteInTransactionAsync` no nested transaction is started and the chunks commit or roll back with the caller's. The flag is explicit rather than probed off `SqliteConnection`, which does not expose its pending transaction publicly.

Every item is validated **and serialized** before the first chunk runs, so a bad item anywhere in the input throws with nothing written. `UpsertManyAsync` rejects duplicate ids with an `ArgumentException` naming the id and both indices — SQLite would otherwise fail the whole statement with the opaque `ON CONFLICT DO UPDATE command does not affect row a second time`. `DeleteManyAsync` instead drops repeats silently: an `id IN (...)` list is unambiguous and the deleted-row count is unaffected.

`GetManyAsync<T>` borrows that 500-item chunk size but runs its own loop rather than `RunBatchAsync`, which sums affected-row counts a read never produces and opens a transaction a read does not need. `SqlGenerator.GenerateBulkGetSql` reuses the `id IN (@Id0..@IdN)` shape, `@Id{i}` naming and caps of `GenerateBulkDeleteSql`, and rows come back through `SqliteCommandExtensions.QueryStringPairsAsync`, a two-ordinal `id, json(data)` reader. The result is an `IReadOnlyDictionary<string, T>` and not a list precisely so the caller can tell which ids were missing: a missing id is an absent key, never a null value. Repeats are dropped ordinally for `DeleteManyAsync`'s reason — an `IN` list is unambiguous and the result is keyed by id anyway — and an empty input short-circuits to `ReadOnlyDictionary<string, T>.Empty` without a round trip. Because a large read spans several statements, it is a point-in-time snapshot only when the call is made on a transaction.

**Raw SQL escape hatch.** The `Connection` property is gone (a pooled connection cannot be handed out to live indefinitely). Use `ExecuteRawAsync(Func<SqliteConnection, CancellationToken, Task<T>>)`, available on both the store and a transaction — on a transaction the callback gets that transaction's connection, so raw commands enlist in it. The connection is valid only inside the callback. Create commands with `connection.CreateCommand()`, which copies the connection's active transaction onto the command — a directly constructed `new SqliteCommand(sql, connection)` leaves `Transaction` null and Microsoft.Data.Sqlite refuses to execute it while a transaction is pending (pinned by `Transaction_ExecuteRawAsync_EnlistsOnlyCommandsCreatedFromTheConnection`).

Three synchronous members on `IDocumentOperations` make that hatch actually usable, so a caller never re-derives what the store already knows: `GetTableName<T>()` resolves the table through the store's *configured* `ITableNamingConvention` (not a copy of the default rule — a custom convention is honoured), `SerializeDocument<T>(value)` returns the same UTF-8 JSON bytes the store writes, for binding to a raw `jsonb(@Data)`, and `DeserializeDocument<T>(json)` turns a raw `SELECT json(data)` column back into `T` using the store's `SerializerOptions`. They need no connection, take no token, and are present on both the store and a transaction. Without them a consumer had to hardcode the table name beside the type and reimplement STJ with options the store never exposed — `JsonHelper` and `SqliteCommandExtensions` stay `internal`. `DeserializeDocument` returns `default` for null/empty JSON and throws `SerializationException` on malformed input; `SerializeDocument` throws `ArgumentNullException` on a null value. Deliberately *not* added: any predicate/projection query API — richer filtering stays raw SQL (see the removed `GenerateQueryWithWhereSql` family).

The same reasoning keeps teardown on `IDocumentOperations` rather than behind the hatch: `DeleteAllAsync<T>` (`DELETE FROM [t]`, returns the rows deleted, table survives), `DropTableAsync<T>` and the two `DropIndexAsync` overloads mean a test fixture or an admin path never hand-writes a `DROP`. Both drops are `IF EXISTS`, so they are idempotent and re-creating afterwards works. `DropIndexAsync<T>(x => x.Email)` derives its name through the same `ExtractJsonPath` + `GenerateIndexName` pair `CreateIndexAsync` uses, so it drops exactly the index that call created; an index created under an explicit name has to go through the string overload.

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
- Package versions are centralized in `Directory.Packages.props` (Central Package Management); csproj files carry a bare `<PackageReference Include="..." />` with no version.
- New features need both a unit and an integration test.
- Don't add AI-attribution trailers (`Co-Authored-By: Claude`, "Generated with Claude Code") to commits or PRs.
- Never auto-commit — stage and commit only when the user explicitly asks.

## Where to look first

- `src/LiteDocumentStore/Core/DocumentStore.cs` — the public surface: rents a connection per operation and delegates to `DocumentOperations`.
- `src/LiteDocumentStore/Core/DocumentOperations.cs` — where every document operation is actually implemented (shared by the store and by transactions).
- `src/LiteDocumentStore/Core/SqliteConnectionPool.cs` — connection lifetime, PRAGMA-once configuration, the `:memory:` guard.
- `src/LiteDocumentStore/Core/SqlGenerator.cs` — the JSONB SQL contract; change SQL here, nowhere else.
- `src/LiteDocumentStore/Query/DocumentQuery.cs` — the composable filter builder; validation and bound-value normalization live here.
- `src/LiteDocumentStore/Core/SqliteCommandExtensions.cs` — the raw ADO.NET helpers that replaced Dapper.
- `src/LiteDocumentStore/Serialization/JsonHelper.cs` — the AOT-safe serialization funnel (`JsonTypeInfo<T>` + reflection fallback).
- `src/LiteDocumentStore/Extensions/ServiceCollectionExtensions.cs` — how consumers wire it up (DI + lifetimes).
- `examples/AotVerification/` — end-to-end AOT smoke test with a source-generated context; CI publishes it Native-AOT with warnings as errors and runs the binary.
- `examples/Examples/Program.cs` — the sample dispatcher; add a new sample to the array there.
</content>
