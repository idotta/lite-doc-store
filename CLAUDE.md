# CLAUDE.md

Guidance for Claude Code in this repository. This is the **digest**: each rule is stated once, short.
`docs/ARCHITECTURE.md` (§ = its section) has the full detail; `docs/DESIGN-RATIONALE.md` has the
measured evidence. **Read the § before changing or relaxing a rule** — most look arbitrary until you
see what they cost. Where docs conflict with the source, the source wins.

## What this is
`LiteDocumentStore` (NuGet) turns one SQLite file into a hybrid document + relational store: objects
are stored as **JSONB** (SQLite ≥ 3.45), tables stay open to raw SQL via `ExecuteRawAsync`. `net10.0`,
C# 14, nullable on. Raw ADO.NET over `Microsoft.Data.Sqlite` (no Dapper in the library — benchmarks
keep it as a baseline only), `System.Text.Json`, Native-AOT/trim compatible.

## Build, run, test (all from the repo root)
```powershell
dotnet build --configuration Release
dotnet test tests/LiteDocumentStore.UnitTests/LiteDocumentStore.UnitTests.csproj
dotnet test tests/LiteDocumentStore.IntegrationTests/LiteDocumentStore.IntegrationTests.csproj
dotnet run --project examples/Examples -- all                            # CI runs this
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks
```
Filter with `--filter "Category=Unit"` or `"FullyQualifiedName~Method_Scenario"`.

## Layout
`src/LiteDocumentStore/` — `Core/` (store, `DocumentOperations`, pool, transactions, `SqlGenerator`,
options, guards, `QuietLog`, `Crc32C`), `Blobs/`, `Conventions/`, `Query/` (`DocumentQuery`,
`DocumentPatch`), `Indexing/`, `Factories/`, `Extensions/` (DI), `Migrations/`, `Serialization/`
(`JsonHelper`, `JsonPathResolver`, `ValueBinder`), `Exceptions/`. Tests: `tests/*.UnitTests`
(isolated) and `tests/*.IntegrationTests` (real SQLite). `examples/Examples` (every sample, dispatched
from `Program.cs`) and `examples/AotVerification` (the Native-AOT gate CI publishes and runs).

**Visibility.** `DocumentStore`, `DocumentOperations`, `SqlGenerator`, `MigrationRunner`, the pool and
`TableNameCollisionGuard` are `internal sealed`. Tests see them through an `InternalsVisibleTo` inside
the `ExposeInternalsToTests`-conditioned `ItemGroup`; packing flips it off, `LDS0001` refuses any other
pack path, and CI greps the shipped DLL. **Never add an `InternalsVisibleTo` outside that ItemGroup.**
Public surface, accepted name collisions → §Project layout.

## SQL and JSON paths
- **All SQL lives in `SqlGenerator`.** Schema: `id TEXT PRIMARY KEY, data BLOB NOT NULL, version
  INTEGER NOT NULL DEFAULT 1`. Write `jsonb(@Data)` with UTF-8 bytes; read `SELECT json(data)`.
- Values are always bound. Identifiers and JSON paths are interpolated, so every one goes through
  `ValidateIdentifier` / `ValidateJsonPath` / `ValidateColumnType` (or `IsValidIdentifier`). The only
  exception is `SchemaIntrospector.GetColumnsAsync`'s PRAGMA. A new interpolation site is a regression.
- **The JSON path is interpolated on purpose**: binding it would stop SQLite matching expression
  indexes. Never add a generator that takes a caller SQL fragment — `ExecuteRawAsync` is the hatch.
- **Interpolate `ValidateJsonPath`'s return value, never the raw argument** — it is the canonical
  rendering (quoted only when needed, idempotent), so index DDL and queries emit identical text.
- Grammar: `$` + `.member` / `."quoted"` / `[index]`. Quotes are needed for keys with `.`, `[`, or the
  empty key. Refused: apostrophe (injection boundary), `U+0000`, SQL-doubled `""`, unknown escapes, and
  a `"` inside a member that needs quotes (silently wrong on SQLite 3.45.1). Bare `$.` is an error.
  Member rule owner: `SqlGenerator.MemberFault`; its two callers keep **different messages on purpose**.
  → §The JSON path grammar
- Derived index names are `idx_{table}_{fold}_{digest}` (`IndexNameDigest`, CRC-32C, 6 hex) and must
  pass `RequireDerivableName`; Tier 2 / widened / indexed paths need an explicit name. → §Index DDL

## Table naming
`DefaultTableNamingConvention` folds the namespace-qualified name (`MyApp.Sales.Order` →
`MyApp_Sales_Order`); collision-resistant, **not** injective, by design. `TableNameCollisionGuard`
wraps any convention, claims names `OrdinalIgnoreCase` and screens them as identifiers
(`InvalidOperationException`). Unnameable types throw `NotSupportedException`. Tests resolve names via
`store.GetTableName<T>()`. → §Table naming

## Connections and pool
- One `SqliteConnectionPool` per store; **rent per operation**, PRAGMAs applied once per physical
  connection. Provider pooling is forced off. Re-run `ConnectionModelBenchmark` + `StorePathBenchmark`
  before changing this.
- A private `:memory:` DB is rejected; `ForInMemory()` gives a shared-cache one, kept alive by a
  **reserved keeper** connection. Concurrency tests need a **file** DB (shared-cache locks are
  table-level, `SQLITE_LOCKED` is not retried).
- `Return` probes `HasPendingTransaction` (only probe left) and closes dirty connections. The return
  path is `try`/`finally`; release-path logs go through `QuietLog` (must never throw). Rent, commit and
  rollback stay loud.
- Rent is bounded by `PoolWaitTimeoutMs` (copied in `Clone()`). Leaked transactions are recovered by the
  finalizer via `AbandonLease`. The pool **never disposes its `SemaphoreSlim`**, and every site that
  banks a connection re-checks `_disposed` after `_idle.Add`.
- Version/page-size guards run in the pool on every open. `IConnectionFactory` declares **only**
  `CreateConnection(Async)` — never re-add `Configure*`. Decorate `DefaultConnectionFactory`.
- PRAGMA order: `page_size` before `journal_mode`; `foreign_keys` always stated; WAL on in-memory
  refused; `DefaultTimeout` derived from `BusyTimeoutMs`. → §PRAGMA correctness

## Options
`Validate()` runs in `DocumentStoreFactory.CreateStore`, `Build()` and the store constructor; it starts
with `SqliteConnectionStringGuard` (structural, case-sensitive classification → §Connection-string
classification) and ends with `ThrowIfSerializerOptionsUnusable` (both checks, both boundaries).
Options are **snapshotted** via `Clone()` (DI registration clones at registration time); every read
after must come from the snapshot. **A settable property missing from `Clone()` silently reverts** —
`OptionsSnapshotTests` guards it. `SerializerOptions` and `TableNamingConvention` stay shared by design.

## Operations
- **Argument validation precedes `ThrowIfDisposed()` and the rent**, via `DocumentOperations.Validate*`
  called at **both** boundaries (store and transaction). Don't hoist a guard out of the operations.
- Every async member takes `CancellationToken cancellationToken = default`. Command helpers take the
  token **before** the trailing `params` tuple.
- **Queries**: `DocumentQuery<T>` (AND-only, structured records, ≤ 900 bound params). Count/Exists
  ignore paging; **`DeleteAsync(query)` honours it**. Values bind in the serializer's shape via
  `ValueBinder` (enum fallback refused). Ranges refuse UTC/Local `DateTime` and `DateTimeOffset`;
  `OrderBy` on a date path is chronological. LINQ predicates / `SelectAsync` stay removed.
  Expressions resolve to the **serialized** key via `JsonPathResolver`. → §Querying
- **Concurrency**: every write bumps `version`. `WithVersion` CAS throws `ConcurrencyException`; `0`
  means "must not exist" on write but "matches version 0" on delete. → §Optimistic concurrency
- **Patching** emits `jsonb_set`/`jsonb_remove` — **never `json_set`/`json_remove`** (de-binaries the
  column). Patches can't target bare `$` (both boundaries), can't insert, cap at 499 sets / 999 removes.
- **Corrupt rows**: row presence decides not-found, payload decides corrupt (`CorruptDataException`,
  not a subclass of `DocumentSerializationException`). Every read goes through `EnsureDocumentPayload`.
- **Index DDL**: options arrive by overload; `IndexFilter` is value-free; existing index / virtual
  column definitions are **compared** (identical → skip, different → throw), before and after create.
  Projecting DDL rejects bare `$`. → §Index DDL
- **Batches** chunk at 500 (wrapped in a transaction when multi-chunk); everything is validated and
  serialized before the first chunk.

## Blobs
`__store_blobs` table, payload column **last** (metadata reads stay fast); no length column.
`ListBlobsAsync` prefix is a half-open key range, not `LIKE`. Streamed write: reserve + fill are atomic
(own transaction, or a `SAVEPOINT` inside a caller's; `RELEASE` runs uncancellable).
`OpenBlobReadAsync` owns its own out-of-pool connection, bounded by a second budget, and is **absent
from `IDocumentTransaction`**. A non-BLOB `data` is corrupt on all five payload reads. → §Blobs

## Transactions, raw SQL, migrations
- A transaction holds one connection; every member (incl. `GetTableName`/`Serialize`/`Deserialize`)
  checks `ActiveTransaction()`. Operations must be invoked **on the transaction**. `TransactionMode`
  `Deferred = 0` is load-bearing; `Immediate` is opt-in for read-then-write. No general BUSY retry.
- `ExecuteRawAsync`: build commands with `connection.CreateCommand()`. **A raw-accessed connection is
  always retired** (no opt-out, ~335 µs per call on WAL) — prefer one callback doing many statements.
- Migrations run only via `IDocumentStore`, each in its own `BEGIN IMMEDIATE` with the membership check
  inside. Read methods never write. Versions must be positive. Checksums are CRC-32C of up SQL;
  legacy SHA-256 rows are rewritten once. Migration authors: no transactions, no store callbacks,
  override `Checksum` when changing `UpAsync`. → §Migrations

## AOT
Serialize only through `JsonHelper` + `JsonTypeInfo<T>`; the reflection fallback is quarantined and
refused when dynamic code is unsupported. No `Expression.Compile`, no `dynamic`, no reflection
serialization; builds must stay free of `IL2xxx`/`IL3xxx`. **No `System.Security.Cryptography`** —
it pulls in OpenSSL; use `Core/Crc32C.cs`. → §AOT compatibility

## Conventions
- File-scoped namespaces, `sealed`, `readonly`, `_camelCase`, expression-bodied one-liners;
  `.ConfigureAwait(false)` + `Async` suffix; fail fast; rethrow (don't wrap) on rollback.
- Public API needs XML docs — missing ones **fail the build** (`CS1591` as error on packable projects).
- Versions live in `Directory.Packages.props`. New features need a unit **and** integration test.
- **New options arrive by overload, never an inserted parameter** (trailing tokens would break).
- No AI-attribution trailers in commits/PRs. Never commit unless explicitly asked.

**Start reading at** `Core/DocumentStore.cs` → `DocumentOperations.cs` → `SqlGenerator.cs`.
