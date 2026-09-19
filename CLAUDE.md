# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

**Companion:** `docs/DESIGN-RATIONALE.md` holds the measured evidence behind the rules here — the
before/after numbers, the exact failures, the rejected alternatives and what a regression looks like.
This file states the rule and links to it as `→ rationale#<anchor>`. **Read the linked section before
changing or relaxing a rule**; most of them look arbitrary until you see what they cost.

## What this is

LiteDocumentStore is a .NET library (published to NuGet as `LiteDocumentStore`) that turns a single
SQLite `.db` file into a hybrid document + relational store. C# objects are serialized to JSON and
stored in SQLite's **JSONB** binary format (requires SQLite 3.45+); the same tables stay fully
accessible to raw SQL, joins, and indexes. The design goal is explicitly *not* an opaque document DB —
users mix document-style CRUD and relational queries freely, dropping to raw SQL through
`ExecuteRawAsync` (the old `Connection` property is gone).

Solution is `LiteDocumentStore.slnx` at the repository root. Target is `net10.0`, `LangVersion=latest`
(C# 14), nullable + implicit usings on. Data access is raw ADO.NET over `Microsoft.Data.Sqlite` (no
Dapper — parameters bound explicitly, results read by ordinal); serialization is `System.Text.Json`.
The library is Native-AOT / trim compatible (`<IsAotCompatible>true</IsAotCompatible>`).

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
docs/DESIGN-RATIONALE.md                     Why the rules here are what they are (measured evidence)
src/
  LiteDocumentStore/                         The library
    Core/            DocumentStore, DocumentOperations (shared op impls), SqliteConnectionPool
                     + PooledConnection, DocumentStoreTransaction + TransactionMode,
                     IDocumentStore / IDocumentOperations / IDocumentTransaction, SqlGenerator,
                     SqliteCommandExtensions (raw ADO helpers), DocumentStoreOptions(+Builder),
                     VersionedDocument, SqliteSessionState (the pool's dirty-connection probes),
                     QuietLog (logging that cannot throw, for the release paths),
                     and the three guards the options and pool run:
                     SqliteConnectionStringGuard, SqlitePageSizeGuard, SqliteVersionGuard
    Blobs/           BlobInfo, BlobWriteOptions, BlobLimits, BlobIdPrefix (the prefix key
                     range), BlobReadStream (incremental blob I/O), BlobStreamSlot
    Conventions/     ITableNamingConvention — maps type -> table name
    Query/           DocumentQuery, DocumentPatch + the predicate/ordering/patch records
    Indexing/        IndexOptions, IndexFilter (+ IndexFilterTerm) — index DDL options
    Factories/       IDocumentStoreFactory, IConnectionFactory (+ Default impls)
    Extensions/      ServiceCollectionExtensions (AddLiteDocumentStore, keyed variant)
    Migrations/      MigrationRunner (internal), IMigration/Migration, MigrationOptions,
                     MigrationHistoryRecord, SchemaIntrospector
    Serialization/   JsonHelper (STJ, via JsonTypeInfo<T>), JsonPathResolver (expression -> serialized path)
    Exceptions/      LiteDocumentStoreException + Concurrency/CorruptData/DocumentSerialization/
                     TableNotFound/UnsupportedSqliteVersion/IncompatiblePageSize
tests/
  LiteDocumentStore.UnitTests/               xUnit, mocked/isolated
  LiteDocumentStore.IntegrationTests/        xUnit, real SQLite (mostly :memory:)
benchmarks/
  LiteDocumentStore.Benchmarks/              BenchmarkDotNet
examples/
  Examples/                                  Every sample, dispatched by name from Program.cs
  AotVerification/                           Native AOT gate, published + run by CI
```

**Which exception means what.** `ConcurrencyException` (+ `ConcurrencyConflictKind`) comes from the
`WithVersion` writes and deletes on CAS conflicts. `CorruptDataException` comes from a row whose payload
cannot be read, document or blob. `DocumentSerializationException` is a JSON serialization failure or a
JSON type-metadata failure, nothing else. `UnsupportedSqliteVersionException` and
`IncompatiblePageSizeException` both come from guards on connection open.
`MigrationOutOfOrder`/`MigrationChecksumMismatch` come from `MigrateAsync`.

**Accessibility.** `DocumentStore`, `DocumentOperations`, `SqlGenerator`, `MigrationRunner` and the pool
are `internal sealed`; the test/benchmark projects see them via `InternalsVisibleTo` in the csproj (CI's
Pack job fails if a friend name leaks into the shipped DLL).

The public surface consumers touch is `IDocumentStore` / `IDocumentOperations` / `IDocumentTransaction`,
`DocumentStoreOptions(+Builder)`, the factories and the DI extension, plus the value types those
signatures take and return: `DocumentQuery<T>`, `DocumentPatch<T>`, `IndexOptions`/`IndexFilter`,
`TransactionMode`, `VersionedDocument<T>`, `BlobInfo`/`BlobWriteOptions`,
`IMigration`/`Migration`/`MigrationOptions`/`MigrationHistoryRecord`, `SchemaIntrospector`,
`ITableNamingConvention` and `DefaultTableNamingConvention` (public so a custom convention can delegate
to it), `DefaultConnectionFactory` (public for the same reason, and `sealed` for the same reason: a
custom `IConnectionFactory` decorates it by holding one and forwarding, not by deriving from it), and
the exceptions. `TableNameCollisionGuard`, the wrapper every store puts around the configured
convention, stays `internal`.

> Note: the `tests/` projects were updated for the Dapper removal (tests that only covered the dropped
> `QueryAsync(predicate)` / `SelectAsync` APIs were removed; the rest use the raw-ADO helpers in
> `Core/SqliteCommandExtensions.cs`). The `benchmarks/` project intentionally keeps a `Dapper` package
> reference as a *comparison baseline* only — it is not a library dependency.

## Documentation

`README.md` and `.github/WORKFLOWS.md` were re-checked against the source and the workflows; the
`.github/instructions/*.md` Copilot rules were deleted rather than repaired (they had no `applyTo:`
frontmatter, so Copilot never applied them, and they taught `json_set`/`json_remove` against the `data`
column — see patching for why that corrupts it). CLAUDE.md is the architecture guide and
`docs/DESIGN-RATIONALE.md` its evidence; where any doc still conflicts with the source, the source wins.

`examples/` is two real projects in the `.slnx`, so samples cannot rot silently: `examples/Examples` is
one console app holding every sample (`dotnet run --project examples/Examples -- all`, or a single name
— CI runs `all` on every push), and `examples/AotVerification` is the Native AOT gate that CI publishes
with `-warnaserror` and then runs.

## Architecture

### SQL generation

**All SQL is centralized in `SqlGenerator`** (static, one method per statement shape). Nothing else
hand-writes SQL against document tables. Table schema is uniform:
`id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1`.

The JSONB contract, enforced there, is load-bearing:

- **Write:** `jsonb(@Data)` — `@Data` is UTF-8 JSON *bytes* from `JsonHelper.SerializeToUtf8Bytes`, not
  a string.
- **Read:** `SELECT json(data)` — converts JSONB binary back to JSON text for deserialization. JSONB is
  binary; a raw `SELECT data` is not deserializable.
- All *values* are parameterized. Identifiers and JSON paths **cannot** be, so they are interpolated —
  and every one is validated inside `SqlGenerator`, the single boundary where that happens:
  `ValidateIdentifier` restricts table/index/column names to `[A-Za-z_][A-Za-z0-9_]*` (bracket quoting
  alone is not enough — a `]` closes it early), `ValidateJsonPath` enforces `$(.member|[index])*`, and
  `ValidateColumnType` whitelists the five SQLite storage classes. Table names come from
  `ITableNamingConvention`, which is pluggable, so they are validated like anything else.
- **The JSON path is interpolated on purpose, not bound.** SQLite only matches a query against an
  expression index when the indexed expression appears literally, so binding the path would silently
  disable every index `CreateIndexAsync` creates. `SqlInjectionIntegrationTests` pins both halves.
  → rationale#sql-paths
- **Never reintroduce a generator that accepts a caller-supplied SQL fragment.** The three that did
  (`GenerateQueryWithWhereSql` / `GenerateSelectFieldsSql` / `GenerateSelectFieldsWithWhereSql`) were
  deleted; `ExecuteRawAsync` is the escape hatch for that. → rationale#querying

### The JSON path grammar

A **member is one or more characters, none of which is an apostrophe, a `.`, a `[` or `U+0000`**. This
mirrors SQLite's own **unquoted** path label, which terminates only at `.` or `[`. A newline and a tab
are **accepted** — measured, each reads back its own key — and a positive control pins that.
→ rationale#sql-paths

Four exclusions, each for a different reason:

- **The apostrophe is the injection boundary.** The path is interpolated into a single-quoted SQL
  literal, so it is the only character that can escape one. It is deliberately **not** supported by
  doubling: rewriting the emitted text would stop it matching the expression index.
- **`.` and `[` are structural** — `$.a.b` is unambiguously the nested path `a` → `b`, never the single
  key `a.b`. The empty member is rejected because SQLite errors on it.
- **`U+0000` is rejected permanently**, by both `SqlGenerator.ValidateJsonPath` and
  `JsonPathResolver.ValidPathMember`. `sqlite3_prepare` reads a NUL-terminated string, so a NUL
  truncates the statement; bound as a parameter (which this library never does) it silently reads the
  wrong key. It is **not** a Tier 2 shape — quoting does not rescue it.

**Tier 2 (quoted segments) is open and deliberately deferred.** Three shapes remain unreachable *and*
reachable by quoting: a key containing `.`, a key containing `[`, and the empty key. The dotted one is
why it is worth doing — `jsonb_set`/`jsonb_remove` **silently no-op** on it. Tier 2 is a tokenizer
emitting a canonical rendering (`$."a.b"`, `"` escaped JSON-style as `\"`, **not** by SQL doubling),
plus the same rendering in `JsonPathResolver`. Do not try to "finish" `U+0000` inside it.

**Derived index names must be screened, at four sites.** `GenerateIndexName` is `idx_{table}_{path}`
with separators flattened and everything else kept, so a widened member (`$.full-name` →
`idx_T_full-name`) or an array index (`$.Tags[0]` → `idx_T_Tags[0]`) produces a name
`ValidateIdentifier` rejects — against an `indexName` no caller passed. `RequireDerivableName` screens
the derived name through **`SqlGenerator.IsValidIdentifier`**, the non-throwing form of the identifier
rule, so that rule keeps one owner. The four sites are `CreateIndexAsync`, `CreateCompositeIndexAsync`,
the composite loop, and `DropIndexAsync<T>(Expression)` (which reports against `expression`, its only
parameter). An explicit index name works in every case. Making the scheme injective is still its own
job. → rationale#sql-paths

### Table naming

`DefaultTableNamingConvention` (`Conventions/TableNamingConventions.cs`, **public** so a custom
convention can delegate to it) folds the type's namespace-qualified name: namespace parts, then the
declaring-type chain, then the simple name, joined with `_`; a constructed generic appends its arity and
then each argument by the same rule.

`MyApp.Sales.Order` → `MyApp_Sales_Order`, `MyApp.Outer+Inner` → `MyApp_Outer_Inner`,
`MyApp.Box<int>` → `MyApp_Box_1_System_Int32`, and a global-namespace type keeps its bare name. It is
exposed as a stateless `Instance`, used by `DocumentStore`, `DocumentStoreFactory` and the DI
registration alike so no path allocates a second one.

**The fold is collision-resistant, not injective, and that is deliberate.** Two families are knowingly
accepted (a segment's underscore is indistinguishable from a separator; a generic argument's extent is
not recoverable). Closing either needs a self-delimiting encoding that is not a name anyone types into a
SQL client, and readable tables are the point of a store that stays open to raw SQL. The arity is kept
even though it does not delimit, because it separates two same-named generics of different arity.
→ rationale#table-naming

**`TableNameCollisionGuard` makes the residual loud, and screens the name.** It is `internal`, wraps
**whatever convention is configured**, is applied in `DocumentStore`'s constructor, keeps a
`ConcurrentDictionary<string, Type>` of claims **keyed `OrdinalIgnoreCase`**, and throws
`InvalidOperationException` naming both types when a second type claims a name. The comparer is load-bearing: SQLite folds ASCII case in identifiers, so
`[Order]` and `[order]` are one table, and C# names are case-sensitive. The guard is per store instance
and in process. Cost is one dictionary hit per operation, on a path whose cheapest operation is ~4.5 µs.

**It also runs the identifier rule over the name, at the one point every operation passes**, through
`SqlGenerator.IsValidIdentifier` — the same non-throwing form `RequireDerivableName` uses, so the rule
keeps one owner — and throws `InvalidOperationException` naming the configured convention's **type** and
the name it returned. That exception type is the decision, not an oversight: a convention-produced name
has no caller parameter behind it (the caller passed a type), so an `ArgumentException` would have to
invent one, which is the mis-attribution class this rule exists to end. Only a custom convention can
reach it — the default fold throws `NotSupportedException` instead of producing such a name. Closing it
here makes two downstream blames correct rather than patching them: `RequireDerivableName` can honestly
blame the caller's path, since `idx_` joined to two identifiers is an identifier, and no generator's
`tableName` check is reachable through the typed surface any more. The break reaches
`GetTableName<T>()` too, which handed such a name back silently and now throws, so a consumer who was
bracket-quoting it themselves inside `ExecuteRawAsync` loses that. → rationale#table-naming

**Types the fold cannot name throw `NotSupportedException`** naming the type (and, when reached through
a generic argument, the requested type as well) rather than producing a name the identifier validator
blames on `tableName`: open generic definitions, generic parameters, arrays, pointers, by-ref types,
types nested inside a **generic** type, and any namespace or name segment that is not an ASCII SQL
identifier — C# admits Unicode identifiers, `ValidateIdentifier` does not.

**Breaking**, pre-1.0 and deliberate: every table name changes for any type outside the global
namespace, so an existing database needs its tables renamed or a five-line `type.Name` convention
plugged in (documented in README). Auto-derived index names change with it, as does raw SQL inside a
consumer's own `IMigration`. Generic document types start working where they used to throw. Tests
resolve names through `store.GetTableName<T>()` (or `DefaultTableNamingConvention.Instance`); three of
the four benchmark files keep hardcoded literals on purpose. → rationale#table-naming

### Connection model

A `DocumentStore` owns a `SqliteConnectionPool` (`Core/SqliteConnectionPool.cs`) and **rents a
connection per operation**, so the store is thread-safe and safe to share across requests. Connections
are opened + PRAGMA-configured **once** by `DefaultConnectionFactory` from `DocumentStoreOptions`, then
reused; a returned connection goes back to an idle bag unless it comes back broken or dirty.

`MaxPoolSize` caps the **leasable** ones (default: processor count clamped to [2, 16]) — not the
connections the store holds: blob read streams are a second budget of the same size, and a shared-cache
in-memory store keeps one more, so the store holds up to `2 × MaxPoolSize + 1`. That is steady state,
not a hard ceiling on *handles* — a leaked transaction's slot comes back through `AbandonLease` before
its connection does, so a replacement is opened while the abandoned handle waits on its finalizer.

The store deliberately opts **out** of Microsoft.Data.Sqlite's own pool (`Pooling=False`, forced in
`SqliteConnectionPool.Normalize`): that pool gives no "this handle is new" hook, so every rent would
re-apply the session PRAGMAs — measured at **+68% on a 4.5 µs read**. → rationale#connection-model

**Re-run `ConnectionModelBenchmark` and `StorePathBenchmark` before changing this design.** The model
costs +2.1% on a file-DB read, +8.6% on a shared-cache in-memory read, +10% allocations, and is **3x
faster** than a `SemaphoreSlim(1)`-gated single connection at 64 concurrent reads. → rationale#connection-model

**A private in-memory database is rejected.** `Data Source=:memory:` (or `Mode=Memory` without
`Cache=Shared`) throws `ArgumentException`, since each pooled connection would get its own empty
database. `DocumentStoreOptions.ForInMemory()` therefore returns a *uniquely named shared-cache* memory
DB. Caveat: shared-cache in-memory DBs use table-level locks, so overlapping **write transactions** on
one in-memory database fail with `SQLITE_LOCKED`, which `busy_timeout` does not retry — concurrency
tests need a real file DB.

**The reserved keeper.** A shared-cache in-memory database is destroyed when its **last** connection
closes, so for those stores — and only those — `Initialize` keeps the connection it opens as a *keeper*:
never leased, never discarded, never counted in `ConnectionCount`, closed last at disposal. One
permanent extra handle per in-memory store, one extra open per store lifetime, and **no steady-state
rent, return or discard overhead**. A file database gets no keeper. `InMemoryKeeperIntegrationTests`
pins every path that closes an established connection. → rationale#connection-model

**The `DefaultConnectionFactory` seam.** It is public and `sealed`, so a custom `IConnectionFactory`
decorates it by delegation. Because `SqliteConnection` is unsealed and `CreateCommand()` is virtual, a
factory can hand the store a connection subclass that observes every statement a path issues while
keeping correct PRAGMA behaviour — which is how `BlobSavepointCancellationIntegrationTests` and
`IndexCreationRaceIntegrationTests` turn microsecond-wide races into deterministic tests.

### Dirty-session guard

A connection goes back into the idle bag **only if it comes back clean**, because a connection carrying
a stranded transaction poisons whoever rents it next. The store's own paths cannot produce that (every
internal transaction is committed or rolled back by disposal), but `ExecuteRawAsync` and an
`IMigration`'s own SQL hand out the raw connection, and **four measured shapes** come back dirty.

**Two probes are needed because neither sees the other's shapes** (`Core/SqliteSessionState.cs`):

- `HasPendingTransaction` — SQLite's own answer via `raw.sqlite3_get_autocommit` (hence the direct
  `SQLitePCLRaw.core` reference). **42 ns, no allocation**, so `Return` runs it on every operation.
- `HasManagedTransaction` — asks the provider through `CreateCommand().Transaction`. **223 ns and
  192 B**, so only `ReturnAfterExternalAccess` pays it: the two `ExecuteRawAsync` overloads and
  `RunMigrationAsync`.

A dirty connection is closed with a warning naming the shape, never handed back or thrown about: the
caller's operation already returned, or already failed with its own exception. A transaction's own
connection needs none of the probing — a raw `COMMIT` inside `IDocumentTransaction.ExecuteRawAsync`
makes disposal fail, which already sets `_connectionCompromised`.

**This is only safe for a shared-cache in-memory database because of the reserved keeper** — that
qualification is not decoration, it is the defect this guard once had. → rationale#connection-model

**The whole return path is `try`/`finally`**, and three rules hold because nothing on it may throw while
the caller's exception may already be propagating: only the **probes** sit inside a `catch` (and a
throwing probe is answered as *dirty*, then disposed exactly once below); every cleanup log goes through
`QuietLog`; and `CloseQuietly` retries the close **once**, because that measured failure clears itself.
`RawSqlSessionStateIntegrationTests` pins all of it at `MaxPoolSize = 1`, asserting through a store
transaction rather than a plain write. → rationale#connection-model

### Release-path logging (`Core/QuietLog.cs`)

**A log must never be able to lose the resource around it** — neither the hand-back on a release path,
nor the connection just opened on a rent path. Every such path is guarded by a one-shot flag
(`_released`, `_disposed`, `_disposing`), so a throw does not merely lose the message: it skips the
release, and no retry can ever reach it again.

`QuietLog` is the shared `internal static` wrapper — `LogDebugQuietly`/`LogInformationQuietly`/
`LogWarningQuietly`, each forwarding its template and arguments verbatim under a scoped `CA2254`
suppression so structured logging survives. **Fourteen sites** use it: five in
`DocumentStoreTransaction`, six in `DocumentStore`'s two WAL-checkpoint copies, three in
`BlobReadStream`'s disposal helpers.

The wrapper is half of each guard; the other half is that the hand-back moved into a `finally`. Either
half alone fixes the shipped bug — deliberate, because the `finally` also covers whatever is added to
those methods later.

**`CommitAsync`, `RollbackAsync` and the pool's rent path stay loud**, because a caller is waiting there
and should learn that their logger is broken. Loud does not mean unguarded:
`SqliteConnectionPool.Announce` runs in a `try` whose `catch` routes the just-opened connection through
`DiscardBrokenConnection` before rethrowing, with the provider `await` *outside* that `try`.
`LoggerFaultLeaseIntegrationTests` pins it all at `MaxPoolSize = 1`. → rationale#quiet-log

### Pool lifetime and budgets

**The operation rent is bounded.** `DocumentStoreOptions.PoolWaitTimeoutMs` (default 30 s,
`Timeout.Infinite` for the old unbounded wait) bounds `RentAsync(CancellationToken)`; past it the rent
throws a `TimeoutException` naming the option, the value, `MaxPoolSize` and the disposal hint. The two
timeout overloads stay as they were — the WAL-checkpoint disposal keeps its own 5 s. Behavioural break:
a call that used to queue indefinitely now fails. **It must be copied in `Clone()`**, which is where the
pool reads it from.

**A leaked transaction is recovered and reported.** `~DocumentStoreTransaction` logs at Error and calls
`PooledConnection.Abandon()` → `AbandonLease()`, which uncounts and releases the slot **without touching
the connection** (a finalizer must not touch provider objects with finalizers of their own). The log
sits in a `try` whose `finally` does the release, inside a `catch`-all. `GC.SuppressFinalize` goes in
`Release()`, not `Dispose` — commit and rollback release without disposing, so `Release` is the one
choke point every completion path passes. Three `CA1816` suppressions carry that reasoning.

Two consequences: the abandoned connection keeps its lock until the provider finalizes it, so a *write*
right after a recovered leak can still fail `SQLITE_BUSY` (the pinning test probes with a read); and
`ConnectionCount` under-reports live handles for that window while live handles can transiently exceed
`MaxPoolSize`. → rationale#pool-budget

**The pool never disposes its `SemaphoreSlim`.** `Dispose()` on one with a parked waiter clears the
waiter list without completing it, so an operation queued while the store was disposed would hang
forever; a semaphore that never exposes `AvailableWaitHandle` holds no unmanaged resource anyway.
`Return`/`Discard` release the slot **even on the disposed path** — that is what wakes a parked waiter,
which then throws `ObjectDisposedException` from `ThrowIfDisposed` instead of hanging.

**Every site that banks a connection re-checks `_disposed` after the `_idle.Add`** (`DrainIfDisposed`).
Checking before is not enough: `Dispose` drains the bag exactly once, so a later add leaks the
connection — measured at **313 of 400** barrier-synchronized attempts. Three sites bank and all three
re-check: `ReturnCore`'s clean branch plus `Initialize`/`InitializeAsync`. `Discard` needs none of it.
→ rationale#connection-model

`_disposed` is an `int` flipped with `Interlocked.Exchange`, so double-dispose is atomic and every
operation guard (`ThrowIfDisposed`) sees it. The DI registration is **Singleton only** — the
`ServiceLifetime` parameter is gone, because a thread-safe store with its own pool has nothing for a
scoped registration to isolate.

**SQLite version guard.** `jsonb()` shipped in SQLite 3.45.0, so `SqliteConnectionPool` runs
`SqliteVersionGuard.EnsureSupported(Async)` on **every physical connection as it is opened** and throws
`UnsupportedSqliteVersionException` (carrying `ActualVersion` + `MinimumVersion`) instead of letting the
first write fail with `no such function: jsonb`. The async path reads the version through
`SchemaIntrospector.GetSqliteVersionAsync`, the sync path queries `SELECT sqlite_version()` directly —
the introspector is async-only. It lives in the pool, not in `DefaultConnectionFactory`, because
`IConnectionFactory` is public — a consumer-supplied factory would otherwise open unguarded
connections. Same reason for `SqlitePageSizeGuard`. The result is deliberately **not cached**.
`IsHealthyAsync` re-checks through the same guard and maps the exception to `false` at warning level.

### Options validation and the snapshot

`DocumentStoreOptions.Validate()` is called from `DocumentStoreFactory.CreateStore`, so it covers the DI
path too — `AddLiteDocumentStore` hands the factory an options object nothing else has validated, and
options built by hand never pass through `DocumentStoreOptionsBuilder`. It rejects a non-power-of-2
`PageSize`, a negative `BusyTimeoutMs`, a `MaxPoolSize` below 1, a `PoolWaitTimeoutMs` of 0 or negative
other than `Timeout.Infinite`, and a blank `AdditionalPragmas` entry, each naming the offending option
as `ParamName`; `Build()` calls it too, so a builder cannot produce options the factory then refuses.

**`MaxPoolSize` and `PoolWaitTimeoutMs` validate in their setter as well, and name the option there
too.** Both are `field` auto-properties, so an out-of-range value is refused before `Validate()` can
ever see it — which is also why those two `Validate()` branches and `Build()`'s `MaxPoolSize` check are
unreachable belt-and-braces. Each setter passes the option's own name as `ParamName`, not the setter's
implicit `value`: one condition reports one name wherever it fires, and `value` names nothing a caller
can go and fix. → rationale#options-snapshot

**Two serializer rejections live together in `ThrowIfSerializerOptionsUnusable`**, resolver-less first
then AOT-null, and **both must run at both boundaries**:

- A supplied `SerializerOptions` must carry a `TypeInfoResolver`. `JsonSerializer.Serialize(value,
  options)` populates a missing one; `GetTypeInfo(Type)` — which the AOT-safe path must use — does not.
- A **null** `SerializerOptions` is refused when `RuntimeFeature.IsDynamicCodeSupported` is false. ILC
  substitutes that property as a constant, so the branch is decided at publish time.

`ParamName` is `nameof(SerializerOptions)` at both, naming the option to fix rather than the `options`
bag, which costs a scoped `CA2208` suppression. Honouring half of the pair is the exact asymmetry the
pairing exists to prevent. → rationale#options-snapshot

**Options are snapshotted, not re-checked.** `CreateStore` clones the caller's options and validates the
*clone*, then reads nothing from the caller's object again — the naming convention and the store both
come from the snapshot. `DocumentStore`'s constructor clones and validates once more, so the guarantee
holds for the internal constructor the test projects use and for any path that skips the factory. **Every
read in that constructor must come from the snapshot**; re-reading `options` one line further down
reopens the window. `SqliteConnectionPool.Normalize` clones a third time, because the pool is
constructed directly in tests and should defend itself.

The window was real: a caller-supplied `ILoggerFactory` runs as arbitrary code between `Validate()` and
construction, on the mutable object the caller still holds, and an ordinary concurrent setter reaches it
with no custom logger at all. → rationale#options-snapshot

**The DI registration snapshots too, and it is the only one of the four `Clone()`s whose *moment* is
part of the contract** — it runs at registration, before the three above. Both instance overloads
(`AddLiteDocumentStore(options)` / `AddKeyedLiteDocumentStore(key, options)`) call `options.Clone()` in
the method body and capture the clone, not the caller's instance. Capturing the instance deferred the
whole configuration to the factory's clone at *first resolution*, so what a store opened depended on
when DI happened to resolve it: registering one object under two keys with a mutation in between made
**both** keys open the second database, and on the unkeyed path the surviving `TryAddSingleton`
registration silently adopted the second registration's configuration. `Clone()` does not validate, so
registration still cannot throw beyond its `ThrowIfNull`s — an invalid value is still reported by the
first resolution. The two `Action<…>` overloads inherit it by forwarding, which also detaches an
options reference a delegate retained; the extra clone per registration is not a hot path.
→ rationale#options-snapshot

**What the snapshot cannot detach.** `SerializerOptions` stays the caller's instance *deliberately* (the
source-generated resolver and its metadata cache must be shared for AOT correctness).
`TableNamingConvention` stays the caller's instance *necessarily* — it is a behaviour object, and
`TableNameCollisionGuard` invokes it on every operation, so `ITableNamingConvention`'s docs **require**
an implementation to be deterministic: one type, one name, for a store's lifetime. That is a documented
requirement, not a runtime guard. `AdditionalPragmas` is genuinely detached.

**`Clone()` completeness is load-bearing for every store construction**: a settable property left out
silently reverts to its default on every store.
`OptionsSnapshotTests.Clone_CopiesEverySettablePublicProperty` reflects over the type and fails the run
rather than a consumer's store.

**Metadata failures are translated, but `GetTypeInfo`'s `InvalidOperationException` is caught around
that one statement only** — in `JsonHelper.ResolveTypeInfo<T>`, not around the whole operation. STJ
propagates an exception other than `JsonException`/`NotSupportedException` unchanged out of a custom
`JsonConverter`, so a wider clause relabels a converter's own failure as a metadata failure — an
unhelpful error becomes a confidently wrong one. The `JsonException` and `NotSupportedException` clauses
deliberately *do* wrap both statements, because both legitimately arrive from either side.
→ rationale#json-metadata

### PRAGMA correctness

Four settings used to be applied and silently ignored. These are deliberate breaking changes over
silence: a store that cannot honour an option now refuses to open rather than pretending.
→ rationale#pragmas

- **`PRAGMA page_size` must precede `journal_mode`.** SQLite refuses to change the page size of a
  database in WAL mode, so the old order made `PageSize` a no-op *even on a brand-new file*. On an
  *existing* database the PRAGMA is ignored regardless of order, so `SqlitePageSizeGuard` reads the
  value back on every physical connection and throws `IncompatiblePageSizeException` (carrying
  `RequestedPageSize`/`ActualPageSize`). **`PageSize = 0` is the escape hatch** — no statement, no
  check, keep whatever the database has. Converting an existing database needs a `VACUUM`, and that
  only works outside WAL mode.
- **`EnableForeignKeys = false` did nothing.** The provider opens connections with `foreign_keys`
  already ON, so both states are now stated: `PRAGMA foreign_keys = ON|OFF`.
- **WAL mode on an in-memory database is refused.** `PRAGMA journal_mode = WAL` answers `memory` there —
  not an error, not honoured — and it armed the dispose-time checkpoint against a database with no WAL.
- **`BusyTimeoutMs` was a floor, not a bound.** `PRAGMA busy_timeout` bounds SQLite's handler *within
  one attempt*; the provider then re-runs the whole attempt while its command timeout has not elapsed.
  `DefaultConnectionFactory` now also sets `connection.DefaultTimeout` from `BusyTimeoutMs` (seconds,
  rounded up, **floored at 1** — 0 means *retry forever* to the provider), unless the connection string
  states `Default Timeout`/`Command Timeout`, which wins. It is applied **before** the PRAGMA block,
  since those statements run under the command timeout too. This sits in the factory rather than the
  pool: it applies an option, it is not a correctness guard.

**Deliberately not given typed options:** `mmap_size`, `temp_store`, `journal_size_limit`,
`wal_autocheckpoint`, `auto_vacuum` — `AdditionalPragmas` already applies any of them to every physical
connection. `PRAGMA optimize` on close and a general `SQLITE_BUSY` retry policy stay rejected.

**What a custom `IConnectionFactory` owes.** A delegating decorator inherits everything above for free.
A factory that does **not** delegate owes every option it claims to honour — `EnableForeignKeys` and the
`DefaultTimeout` derivation fail **silently** if omitted, `PageSize` is **detected** by
`SqlitePageSizeGuard`, and only the in-memory/WAL combination costs a factory nothing (the connection
string guard rejects it during options validation). `ApplyCommandTimeout` stays `private` and
`SqliteConnectionStringGuard`/`SqliteCommandExtensions` stay `internal`: a delegating factory never
needs them, and a non-delegating one can re-derive both from public BCL surface. → rationale#pragmas

**Disposal** runs `PRAGMA wal_checkpoint(TRUNCATE)` on a rented connection, then closes the pool. That
rent is **bounded** (`DocumentStore.WalCheckpointRentTimeout`, 5 s) and the checkpoint is skipped on
timeout — SQLite checkpoints the WAL itself when the last connection closes. It is gated on
`EnableWalMode` up front, so a non-WAL store pays no rent per dispose.

### Connection-string classification

`SqliteConnectionStringGuard` classifies the data source **structurally, not by spelling**, and runs
during options validation so it fails before any connection is opened. It discards any `#` fragment
(from the **raw** string, before decoding — a `%23` is data), splits a `file:` data source at its first
`?`, and parses the query the way SQLite does: `&`-separated, keys *and* values percent-decoded via
`Uri.UnescapeDataString` (**not** `WebUtility.UrlDecode`), and the **last** occurrence of a repeated
parameter winning. The `Mode=`/`Cache=` keywords fill in only what the query omits.

Classification is **case-sensitive, mirroring SQLite** rather than over-rejecting — `FILE::MEMORY:`,
`mode=MEMORY` and an uppercase `FILE:` prefix name no in-memory database, so the guard leaves them to
SQLite.

It rejects three things, in this order:

1. **A private in-memory database** — including `file::memory:`, an empty URI filename however shared
   the cache claims to be, a `cache=private` that wins by being last, a filename that decodes to
   nothing, and a `cache=shared` sitting behind a `#`. `Data Source=:memory:` stays private whatever
   `Cache=` says; `file::memory:?cache=shared` is genuinely shared and is accepted.
2. **WAL mode on an in-memory database.**
3. **An empty data source, independently of the in-memory classification** — `Data Source=`,
   `Data Source=file:`, `Data Source=file:?cache=shared`, or no `Data Source` keyword at all. SQLite
   opens a private temporary database for an empty filename, so the pool multiplies one configured
   database into one per connection. This check sits **after** the in-memory rejection so the in-memory
   spellings keep reporting the more specific diagnosis; the WAL rejection needed no equivalent split.

`ForSharedInMemory(cacheName)` validates its own argument rather than letting the guard blame a
`ConnectionString` the caller never typed: blank, or containing `;`, `?`, `&` or `#`. The `;` matters
most — it appended a second `Data Source` keyword and created an **on-disk file** with every guard
silent. `%` is deliberately *not* rejected. `DocumentStoreOptionsBuilder.UseSharedInMemory` delegates to
the preset, so one validation covers both entry points. → rationale#connection-string-guard

### Argument validation and cancellation

**Arguments are validated before the connection is rented, and before `ThrowIfDisposed`.** Each guard is
an `internal static Validate*` on `DocumentOperations`, called from **both** boundaries: `DocumentStore`
inline ahead of `RunAsync`, and the `DocumentOperations` method itself because `IDocumentTransaction`
holds its own connection and never rents. Hoisting a guard *out* of the operations would silently lose
it on the transaction path. The double call is free and keeps message and `ParamName` from drifting
(pinned by a store-vs-transaction parity assertion on type, `ParamName` **and** `Message`).

**Ordering is a rule for the whole surface: argument validation precedes `ThrowIfDisposed()`.** A bad
argument is a caller bug that is true whatever state the store is in.

Four deliberate limits: the `*Many` guards hoist only the `ThrowIfNull(items|ids)` (the per-element loop
would otherwise consume a one-shot enumerable twice); validation intrinsic to SQL generation stays in
`SqlGenerator`; the expression DDL overloads hoist only the `ThrowIfNull` on the expression; and the two
composite-index validators scan for a null or blank element **before** parsing any, so a two-fault array
reports the cheap structural fault first. Limit 4 is pinned by two `[Fact]`s in
`ArgumentValidationTests`, because nothing else fails if the interleaved order comes back.
→ rationale#argument-validation

**Cancellation.** Every async member takes `CancellationToken cancellationToken = default`. On the store
the token cancels the wait for a free pooled connection *and* reaches the ADO command; it cannot
interrupt a statement already running, so a cancelled token is observed *before* the command starts. On
a transaction there is no rent, so the command is the only cancellation point.

The helpers in `Core/SqliteCommandExtensions.cs` take the token **before** their trailing
`params (string, object?)[]` — `ExecuteAsync(sql, cancellationToken, ("Id", id))` — because C# allows
one params parameter and it must come last. The two synchronous helpers (`Execute`, `QueryFirstString`)
stay tokenless: every path that uses them is itself synchronous and has no caller token.

### Querying

Two APIs, both AOT-safe. The simple one is `QueryAsync<T, TValue>(jsonPath, value)` —
`WHERE json_extract(data, '$.Path') = @Value`. The composable one is `DocumentQuery<T>` (`Query/`), an
immutable builder consumed by `QueryAsync<T>`, `CountAsync<T>` and `ExistsAsync<T>` on
`IDocumentOperations`, so it works on the store and inside a transaction:

```csharp
var q = DocumentQuery<Customer>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
                               .AndIn("$.City", ["Boston", "Denver"])
                               .AndIsNotNull("$.Email")
                               .OrderBy("$.Age", descending: true)
                               .Skip(10).Take(20);
```

Operators: `Equal`, `NotEqual`, `GreaterThan(OrEqual)`, `LessThan(OrEqual)`, `Like`, `Glob`, `In`,
`IsNull`, `IsNotNull`, `ArrayContains` (`EXISTS (SELECT 1 FROM json_each(data, '$.Tags') WHERE value =
@p0)`). Predicates combine with **AND only** — no OR groups. `Skip` without `Take` emits
`LIMIT -1 OFFSET n`, which SQLite requires.

`CountAsync` and `ExistsAsync` apply the query's **predicates only** — ordering and paging are ignored,
so `ExistsAsync` on a query paged past the end of its match still reports `true`.
`GenerateFilteredExistsSql` wraps a `LIMIT 1` subquery in `SELECT EXISTS(...)` and reuses the same
`AppendWhere` pass as the count, which is what keeps the interpolated path matching an expression index
(pinned by an `EXPLAIN QUERY PLAN` assertion). One API-shape consequence: `ExistsAsync<T>` is overloaded
on `string id` and `DocumentQuery<T>`, so a bare `ExistsAsync<T>(null!)` no longer compiles.

**The builder never accepts a SQL fragment.** `GenerateQuerySql`/`GenerateFilteredCountSql`/
`GenerateFilteredExistsSql` take the structured `QueryPredicate`/`QueryOrdering` records and return a
`GeneratedQuery(Sql, ParameterValues)` — values bound `@p0..@pN` in one left-to-right pass, so SQL and
parameter order cannot drift. A query binding more than `SqlGenerator.MaxBoundParameters` (900) throws
rather than hitting `SQLITE_MAX_VARIABLE_NUMBER`. Arguments are validated at *build* time.

**Bound values are normalized to what STJ wrote into the document**
(`DocumentQuery<T>.NormalizeBoundValue`, shared with the older overload). ADO otherwise binds a shape
that matches nothing *silently* — `DateTime`, `byte[]`, `decimal`, `float` and wide `ulong` each
measured against real SQLite. This assumes default serialization; a custom converter for one of those
types breaks the alignment. `ValidateValue` also rejects a non-finite `float`/`double`.
→ rationale#querying

The LINQ-predicate `QueryAsync<T>(Expression<Func<T,bool>>)` and the `SelectAsync` projections stay
**removed** (runtime reflection / IL generation that AOT can't support). `CreateIndexAsync`,
`CreateCompositeIndexAsync`, `DropIndexAsync<T>` and `AddVirtualColumnAsync` still accept LINQ
expressions, but only walk **member names** to build the path — no compilation or closure evaluation.
For joins, aggregates and virtual-column seeks, drop to raw SQL via `ExecuteRawAsync`.

### Expression-to-path resolution

**The expression names the *serialized* key, not the CLR member.**
`Serialization/JsonPathResolver.cs` resolves every segment through
`SerializerOptions.GetTypeInfo(type).Properties`, matching the CLR member on
`JsonPropertyInfo.AttributeProvider` and emitting `JsonPropertyInfo.Name` — the same metadata
`JsonHelper` serializes through, so an index is by construction created over the path the documents
carry. A default-configured store sees byte-identical SQL. Reading `Member.Name` instead created valid
indexes over paths no row has, and SQLite counts each NULL in a unique index as distinct, so a declared
unique constraint accepted every duplicate, silently. → rationale#json-path-resolver

The derivation refuses rather than guessing — `ArgumentException` when `SerializerOptions` has no
metadata for a type along the path, when the member has no serialized counterpart, when it is
`[JsonIgnore]`d, when it is `[JsonExtensionData]` (tested as `JsonPropertyInfo.IsExtensionData`: its
entries serialize into the *containing* object, so the member's own name appears in no document), and
when the serialized name is not expressible as a JSON path — reported against the member that produced
it. The chain must bottom out at the **lambda's own parameter**; `Convert` nodes are unwrapped at every
hop, so `x => ((Base)x).Name` still resolves.
own parameter**; `Convert` nodes are unwrapped at every hop, so `x => ((Base)x).Name` still resolves.

One consequence: on a store whose names diverge, the derived index name changes with the path, so an
existing database keeps its old vacuous index and `DropIndexAsync<T>` no longer names it.

### Optimistic concurrency

Every document row carries a `version` (starts at 1 on insert, incremented on every write, including
plain `UpsertAsync`/`UpsertManyAsync`). The DDL default is `1` too, so a row a consumer inserts with
their own SQL is CAS-able like any other. `GetWithVersionAsync<T>` returns `VersionedDocument<T>(Data,
Version)`.

`UpsertWithVersionAsync<T>(id, data, expectedVersion)` is the CAS write and
`DeleteWithVersionAsync<T>(id, expectedVersion)` the CAS delete (a plain `DeleteAsync` ignores the
version, so a read-modify-delete could silently drop a concurrent update). Non-zero means "only if the
stored version matches" on both. **`0` does not mean the same thing on both**: on the write it means
"insert, must not exist" — and when the id *is* taken, the write retries as a `version = 0`-guarded
update, lifting a legacy row to 1 instead of leaving it un-CAS-able forever — while on the delete it
carries no insert sense and simply matches a row still sitting at 0. Both write paths end in
`RETURNING version`, so the returned value is what SQLite stored.

A 0-row write or delete throws `ConcurrencyException` carrying `DocumentId`, `TableName`,
`ExpectedVersion`, `ActualVersion` and a `ConcurrencyConflictKind` (`AlreadyExists` / `VersionMismatch`
/ `DocumentNotFound`). `BuildConflictAsync` takes insert intent as an explicit `insertAttempt` flag
rather than inferring it from `expectedVersion == 0`, because inferring mislabelled the
delete-a-legacy-row-at-0 case.

**`ActualVersion` and `Kind` are a post-conflict observation, and their XML docs say so** — the
stored-version read is a separate statement, so outside a transaction the row can change in between.
Deliberately not fixed by wrapping every guarded write in a transaction. → rationale#concurrency

### Patching

`PatchAsync<T>(id, DocumentPatch<T>)` and `PatchWithVersionAsync<T>(id, patch, expectedVersion)` change
named fields in one statement. **The point is the concurrency window, not convenience:** a
read-modify-write reserializes the whole document, so it silently reverts a concurrent writer's edits to
fields the caller never touched.

`DocumentPatch<T>` (`Query/DocumentPatch.cs`) is immutable and mirrors `DocumentQuery<T>`:
`Set(path, value)` / `Remove(path)` start one, `AndSet` / `AndRemove` extend it. Several ops = one
statement, one round trip, **one** version bump. A path may be touched once — a `Set` plus a `Remove` of
the same path is rejected at build time. Within each function SQLite applies paths left to right, so
*related* paths compose in call order.

**`SqlGenerator.GeneratePatchSql` emits `jsonb_remove(jsonb_set(data, …), …)` — never
`json_set`/`json_remove`**, whose text return would silently de-binary the `data` column and break the
JSONB contract every read depends on. Sets are applied inside the removes.

**A patch path must reach below the document root.** `$` is grammatically valid and the *reading* paths
still accept it, but `DocumentPatch` and `GeneratePatchSql` both pass `allowRoot: false`:
`jsonb_set(data, '$', 5)` replaces the whole document with a scalar, **reports success and bumps the
version**; `jsonb_remove(data, '$')` yields SQL NULL and leaks a raw NOT NULL constraint error. The
guard is the *bare* `$` only — `$[0]` stays legal — and sits at **both** boundaries, builder and
generator, each mutation-tested separately. → rationale#patching

**The cap that matters is `SQLITE_MAX_FUNCTION_ARG` (1000), not `SQLITE_MAX_VARIABLE_NUMBER`:** a set
spends two function arguments while binding one parameter, and a remove binds none.
`MaxPatchSetOperations` (499) and `MaxPatchRemoveOperations` (999) are independent and throw at
generation; integration tests execute a patch at each cap against real SQLite.

**A rejection names the caller's own parameter.** Nothing in `DocumentPatch<T>` counts operations, so
`GeneratePatchSql` is the first *and only* validator of both caps and is reached straight from
`PatchAsync` — it therefore takes a `paramName` and is handed `nameof(patch)`, rather than reporting its
own `operations`. Same rule one level up: the duplicate-path check lives in the private
`WithOperation`, and is handed `nameof(jsonPath)` by `AndSet`/`AndRemove` rather than blaming its own
`operation`. Threading the name, not duplicating the check, is the pattern — the cap and the identifier
and path rules each keep one owner. → rationale#patching

Values are scalars only — so a patch needs no `JsonTypeInfo<TValue>`, which a consumer's
source-generated context has no reason to include for an `int` — and are normalized through
`DocumentQuery<T>.ValidateValue`/`NormalizeBoundValue` (both `internal` for this), so a patched field
still matches a query over it. Three types travel as JSON *text* wrapped in `json(@pN)` — the `AsJson`
flag on `PatchOperation`: `bool`, `decimal`, and a `ulong`
above `long.MaxValue`. `Set(path, null)` binds SQL NULL, which `jsonb_set` writes as JSON null — the
field stays present, unlike `Remove`. Nested objects stay an `ExecuteRawAsync` job.

A patch carries no full document, so it **cannot insert**: a missing id is a `ConcurrencyException` with
`DocumentNotFound`. `BuildConflictAsync` therefore takes `long? expectedVersion`, where null means an
unguarded operation and the stored-version read is skipped entirely.

### Corrupt rows

**Row presence decides not-found; the payload decides corrupt.** A genuinely absent id is still absent
(`GetAsync` returns `default`, `GetManyAsync` omits the key), but a row that reads back as nothing
throws `CorruptDataException` naming the id and table — never a skipped row, which would be undetectable
data loss. That is why `GenerateGetAllSql`, `GenerateQueryByJsonPathSql` and `GenerateQuerySql` select
`id, json(data)` and read through `QueryStringPairsAsync`.

**A row reads back as nothing in two shapes and both count:** `jsonb('null')` (the 4-character text
`null`) and a **SQL NULL `data` column**. `ExecuteScalar` maps both "no row" and "row whose column is
NULL" to null, which is why `GetAsync` reads through `QueryFirstStringRowAsync` rather than
`QueryFirstStringAsync`, and why both single-row reads check emptiness *before* deserializing.
`QueryFirstStringAsync` is unchanged and still used for schema and PRAGMA reads.

**Every document read funnels through `DocumentOperations.EnsureDocumentPayload` before deserializing**,
and that ordering is what makes the contract independent of `T` — without it, a value-type `T` got a
fabricated zero row from the collection readers, and the literal `null` surfaced as two different
exceptions depending on whether `T` was a class or a struct. Neither shape is reachable through a store
write. `DeserializeDocument`, the raw-SQL helper, deliberately keeps its own contract (`default` for
null/empty/`null` JSON, `DocumentSerializationException` on malformed). → rationale#corrupt-rows

**`CorruptDataException` does not derive from `DocumentSerializationException`** — catching one must not
catch the other. A corrupt row carries `Id`, `TableName`, `TargetType` and `StoredTypeName`; well-formed
JSON merely incompatible with `T` is still a `DocumentSerializationException`. Neither covers a `data`
column holding bytes that are not JSONB at all: SQLite fails inside `json(data)` and the
`SqliteException` surfaces untranslated, since classifying provider error text is something this library
does not do. `ExceptionNameCollisionTests` uses both bare names in one file, so the CS0104 collision the
rename fixed fails the build if it comes back.

### Index DDL

`IndexOptions` (`Indexing/IndexOptions.cs`) carries `Unique`, `Collation`, `Descending` and `Filter`,
and arrives through an **overload** of `CreateIndexAsync<T>`/`CreateCompositeIndexAsync<T>` — not an
inserted parameter, which would break every caller passing the trailing `CancellationToken`
positionally. Adding the overloads made `<see cref="CreateIndexAsync{T}"/>` ambiguous (CS0419), so those
doc references carry full signatures.

`IndexFilter` (`Indexing/IndexFilter.cs`) is the partial-index `WHERE`, and is **value-free on
purpose**: `IsNull(path)` / `IsNotNull(path)` plus `AndIsNull`/`AndIsNotNull`. SQLite forbids bound
parameters in a partial index, so a value-comparing filter would have to inline a SQL literal — a new
injection surface. Richer filters stay an `ExecuteRawAsync` job.

`SqlGenerator.BuildCreateIndexSql` is the one place the DDL is assembled, and it validates the two
interpolated pieces — the collation name (`ValidateIdentifier`, so custom collations work but `]` and
quotes do not) and the filter paths (`ValidateJsonPath`, re-checked even though `IndexFilter` validates
at build time). Default options emit exactly the statement the generators emitted before options
existed: no `UNIQUE`, no `COLLATE`, no direction (ascending is SQLite's own default, so no `ASC` is
written) and no `WHERE`. On a composite index the collation and direction apply to **every** column;
mixed per-column direction stays raw SQL.

**The `sqlite_master` name pre-check compares definitions rather than skipping blindly.** Identical
text — the ordinary idempotent re-create — is a Debug-logged skip; a difference is an
`InvalidOperationException` naming the index and **both** definitions. This is
`TableNameCollisionGuard`'s precedent applied to index names, and it exists because the derivation is
not injective: the worst collision silently downgraded an expression index to a permanent table scan.

The comparison is exact because **one generator produces both sides** — the three index generators take
an `ifNotExists` flag and the comparison form is generated with it false, since SQLite reconstructs the
header canonically and drops `IF NOT EXISTS`. No string surgery on the executed text.

**The comparison is made twice**, because the executed statement keeps `IF NOT EXISTS`: right for two
callers racing to create the *same* index, wrong for a conflicting one. So the stored definition is
re-read *after* the create and compared again. Both races are pinned deterministically in
`IndexCreationRaceIntegrationTests`. The statement is still generated **before** the pre-check runs, so
a bad collation or path throws whether or not the index exists. → rationale#index-ddl

**`AddVirtualColumnAsync` checks the index definition before its `ALTER`**, and ahead of the
column-exists check rather than between it and the `ALTER`, so "refuse before any work" holds by
construction. The post-create check still runs.

**Deliberate break:** re-creating an index under a name that already exists with *different*
`IndexOptions` used to no-op, and `IndexOptionsIntegrationTests` pinned that no-op; it now throws, and
that test asserts the throw instead. `DropIndexAsync` first is still how an index's options are
changed. **What this does not close:** `DropIndexAsync<T>(x => x.A.B)` still drops whatever holds the
derived name, so a collision can still drop the wrong index — that needs an injective scheme and is its
own job.

**String-path DDL overloads** (`CreateIndexAsync<T>(string jsonPath, …)`,
`CreateCompositeIndexAsync<T>(string[] jsonPaths, …)`, `AddVirtualColumnAsync<T>(string jsonPath, …)`,
each with and without `IndexOptions`) take the path verbatim, for a key written by something other than
`T`'s serializer, or an array element no property access can name. The path is validated **before** the
index name is derived from it. There is deliberately no string `DropIndexAsync<T>` — it would sit beside
`DropIndexAsync(string indexName)` and invite passing the wrong kind of string.

**The projecting DDL rejects the document root.** The split is by what the caller does with the
extracted value, not by API: the *reading* paths keep `$` (a predicate, an ordering, an `IndexFilter`
term each only compare or order by the whole serialized document), while `CreateIndexAsync`,
`CreateCompositeIndexAsync` and `AddVirtualColumnAsync` pass `allowRoot: false`. Both boundaries carry
the guard — three `DocumentOperations` call sites and three generators — and each is killed by exactly
one test; the `AddVirtualColumnAsync` call site is not redundant, because an existing column
short-circuits past the generator. The expression overloads need nothing, since `JsonPathResolver`
always appends at least one member. → rationale#index-ddl

**The generator's checks over the caller's own arguments are hoisted beside that root check**, for the
same reason: an existing column short-circuits past `GenerateAddVirtualColumnSql` entirely, so
`columnName`, the path root and `columnType` would otherwise be accepted or rejected by *database
state*. Measured, the identical call threw `ArgumentException` on a fresh database and was a silent
no-op on the second run — no injection surface (the bad value never reaches SQL on that branch), but a
non-idempotent contract.
`columnName` reaches it because `SchemaIntrospector.ColumnExistsAsync` compares against the table's
*real* columns, so one added by raw SQL under a name `ValidateIdentifier` rejects is reported present.
`SqlGenerator.ValidateColumnType` and `ValidateIdentifier` are `internal` for this; the rule keeps one
owner, and the generator's own checks stay. The hoist lives in `DocumentOperations` only — it is
validation intrinsic to SQL generation, not a plain argument guard, and the transaction boundary needs
it there.

**The generator's fourth check, `tableName`, is not hoisted — and no longer needs to be.** It is a
*derived* name (the caller passes a type, not a string), so hoisting it would raise an
`ArgumentException` against a parameter no caller passed. It is refused where it is produced instead:
`TableNameCollisionGuard` screens every convention-produced name through the identifier rule, so a
non-identifier table name never reaches this method on either branch, and the short-circuit can no
longer decide by database state what an argument decides. The generator's own check stays as the
generator's own contract. It was never an injection surface either: `SchemaIntrospector.GetColumnsAsync`
double-quotes the table name and doubles any embedded `"` itself before interpolating it into
`PRAGMA table_xinfo(...)`. The same screening is why `AddVirtualColumnAsync`'s inline
`idx_{tableName}_{columnName}` cannot derive a non-identifier index name: both halves are validated
before it is built. → rationale#index-ddl

### Blobs

Raw binary payloads live in a reserved store-owned table (`SqlGenerator.BlobTableName`,
`__store_blobs`), created via `CreateBlobTableAsync()`. `PutBlobAsync`/`GetBlobAsync`/`DeleteBlobAsync`/
`BlobExistsAsync`/`BlobLengthAsync` — no JSONB conversion, bytes stored verbatim. Blob operations exist
on `IDocumentOperations`, so calling them on an `IDocumentTransaction` commits a document and its blob
atomically. `BlobLimits.MaxBlobLength` (1,000,000,000, derived from SQLite's default
`SQLITE_MAX_LENGTH`) bounds every write with an `ArgumentOutOfRangeException`.

**The table is `(id, content_type, created_at, updated_at, version, data)` — payload last, and that is
load-bearing.** SQLite reads a row front to back, so metadata behind a multi-megabyte payload can only
be reached by walking overflow pages: **232 ms vs under 1 ms** per listing pass over twenty 20 MB rows.
There is deliberately **no length column** — `length(data)` is answered from the record header.

`GetBlobInfoAsync(id)` returns `BlobInfo(Id, Length, ContentType, CreatedAt, UpdatedAt, Version)`
without reading the payload, and `ListBlobsAsync(idPrefix, skip, take)` returns them in id order. **The
prefix is a half-open key range** (`id >= @Prefix AND id < @PrefixEnd`, upper bound from
`Blobs/BlobIdPrefix.cs`), not a pattern: only the range *searches* the primary-key index, and `LIKE` is
ASCII-case-insensitive so it matched the wrong ids. → rationale#blobs

Content type arrives through a `BlobWriteOptions` **overload** of both write paths, same
source-compatibility rule as `IndexOptions` — and with the same consequence, `PutBlobAsync(id, data,
default)` is now ambiguous. An overwrite replaces the content type and leaves `created_at` naming the
first write; both timestamps are stamped by SQLite itself
(`CAST(unixepoch('subsec') * 1000 AS INTEGER)`), so every writer against one file uses one clock.

Blobs are inside the concurrency model: `PutBlobWithVersionAsync` (bytes or stream, with or without
options) and `DeleteBlobWithVersionAsync` reuse `BuildConflictAsync` — which gained an `entity` noun so
a blob conflict does not report itself as a document. On the streamed path the guard is the **reserve**
statement, so a rejected write never replaces the payload with a zeroblob.

#### Streaming

`IDocumentStore.OpenBlobReadAsync(id)` returns a seekable read-only `Stream?` over SQLite's incremental
blob I/O (null when absent), and `PutBlobAsync(id, Stream source, long length)` writes one without
materializing it. **The asymmetry is the whole design: a read has to hand a `Stream` out, a write only
has to take one.** So the write consumes its source inside the call and owns nothing afterwards, and
`OpenBlobReadAsync` is the one member in the feature with a "dispose this" contract.

That stream owns a connection opened **outside the pool**, a deferred read transaction, and the
`SqliteBlob`, disposed in that order. Not a pooled lease — a forgotten one would starve the pool; on its
own connection the same mistake costs one handle. Open streams are still **bounded** by a second
`SemaphoreSlim` of `MaxPoolSize` slots (`BlobStreamSlot`, released idempotently on dispose, on the
not-found path and from the finalizer), a separate budget from the operation slots precisely so the two
cannot starve each other. The read transaction is what makes the rowid safe (SQLite reuses a deleted
row's rowid); it is deferred, so it takes no lock, but while a stream lives it pins the WAL against
truncation.

**`OpenBlobReadAsync` is deliberately absent from `IDocumentTransaction`** — a stream outliving its
transaction would read through a connection already back in the pool. Inside a transaction, blobs are
read with `GetBlobAsync`.

**`length` means "consume exactly this many bytes", not "the source holds exactly this many".** A
seekable source is measured from its current position, so a wrong length in either direction throws
`ArgumentException` with no I/O done. A non-seekable one cannot be measured, and the copy deliberately
**does not read past `length` to check** — that read would block indefinitely on a live pipe and would
swallow a byte belonging to whatever follows in a framed stream. There, only a premature end is an error
(`EndOfStreamException`). A plain `CopyToAsync` is wrong separately: it would run off the end of a blob
that cannot grow. → rationale#blobs

**Reserve and fill are two statements, so the pair is always atomic:** outside an ambient transaction it
takes its own, and **inside a caller's transaction it takes a `SAVEPOINT`** (`GenerateSavepointSql`/
`GenerateRollbackToSavepointSql`/`GenerateReleaseSavepointSql`, the name generated and
identifier-validated like any other). Without either, the reserve
has already replaced the payload with zero bytes by the time a copy fails.

**The opening `SAVEPOINT` and the write take the caller's token; both the failure cleanup and the
successful `RELEASE` run on `CancellationToken.None`.** The release is the non-obvious half: once every
declared byte is written it is the point of no cancellation, because the work is already in the caller's
transaction and only the marker remains. Measured, a cancellation in that window made the call throw
while the payload stayed committable — so a caller who caught it and committed persisted a write they
were told had failed. **No other cancellable finalizer in the library has this shape**, and the reason
is structural: only the savepoint leaves work that the caller's *already-intended* commit will persist.
→ rationale#blobs

#### Corrupt blob rows

**A blob row is corrupt when its `data` column is not a BLOB**, and the same sentence governs it as
documents: row presence decides not-found; the payload decides corrupt. All five reads that depend on
the payload throw `CorruptDataException` carrying the id, the table and the offending storage class —
`GetBlobAsync` and `OpenBlobReadAsync`, which read it, plus `BlobLengthAsync`, `GetBlobInfoAsync` and
`ListBlobsAsync`, which only *measure* it and are exactly why the check cannot live at the point of
reading. `BlobExistsAsync` still reports `true`, which is the point of the contract.

Two things make this bigger than SQL NULL: `length()` answers for a non-BLOB too (5 for `'hello'`), and
incremental blob I/O **opens a TEXT value** and reads its UTF-8 bytes. Both are wrong answers rather
than errors, which is why every blob read leads its projection with `typeof(data)` and funnels through
`DocumentOperations.EnsureBlobPayload`. **`QueryFirstBlobRowAsync` reads ordinal 1 only when the class
is `blob`, and that ordering is load-bearing** — `GetFieldValue<byte[]>` silently succeeds on TEXT,
INTEGER and REAL, so validating after reading would swap a detectable failure for wrong bytes.

The guard is on **reads only**: the deletes and all three write paths still work over a corrupt row,
because getting rid of one or overwriting it is the recovery. `ListBlobsAsync` fails the whole listing
rather than skipping the row, matching `GetAllAsync`. An empty blob (`x''` or `zeroblob(0)`) is a BLOB of
length 0 and stays legal everywhere. → rationale#blobs

#### Upgrading and rebuilding

`CreateBlobTableAsync` adds missing columns in place — `pragma_table_info` check, `ALTER TABLE ADD
COLUMN` under `BEGIN IMMEDIATE` with the check repeated inside the lock — rather than shipping an
`IMigration` a consumer must register: it is a reserved, store-owned table. `ALTER TABLE` only accepts a
constant default, so the timestamps are **nullable** and `version` defaults to 1; the fresh table
declares the same nullability so the two do not diverge.

What `ALTER TABLE` cannot do is reorder, so an upgraded table keeps `data` second — the slow layout —
which is why `IDocumentStore.RebuildBlobTableAsync()` exists: `CREATE`/`INSERT SELECT`/`DROP`/`RENAME`
in one transaction (200 MB in 823 ms), returns false when there is nothing to do, never run implicitly.
It adds the metadata columns first, because a table that predates them ends in `data` like a current one
and the layout check alone reported it current. A warning naming it is logged whenever the legacy order
is detected.

Deliberately out of scope: `RebuildBlobTableAsync` is the only conversion offered (no online/chunked
variant), and blob ids carry no secondary indexes, so listing is ordered by id and nothing else.

### Transactions

`BeginTransactionAsync()` returns an `IDocumentTransaction` (`Core/DocumentStoreTransaction.cs`) that
holds **one rented connection** for its lifetime; `CommitAsync`/`RollbackAsync` finish it, and disposing
without committing rolls back. `ExecuteInTransactionAsync(Func<IDocumentTransaction, Task>)` is the
ergonomic wrapper. Every operation goes through `ActiveTransaction()` first, so a call made after
commit/rollback/disposal throws `InvalidOperationException` — or `ObjectDisposedException` once
disposed — instead of running on a connection the pool has already re-leased.

**The transaction object is the unit of work**: operations must be invoked **on it**, because operations
invoked on the store rent their own connection and commit independently. That is the point of the design
— under the old shared-connection model a concurrent request's writes silently joined another's
transaction and were rolled back with it.

**`TransactionMode`.** `BeginTransactionAsync`/`ExecuteInTransactionAsync` default to `Deferred` and
take `TransactionMode.Immediate` through an **overload**, not an inserted parameter (both
`BeginTransactionAsync(default)` and `ExecuteInTransactionAsync(action, default)` are now ambiguous, so
such a call needs a cast). **`Deferred = 0` is load-bearing**: the tokenless overloads delegate with it,
so renumbering the enum would silently change what every existing caller gets.

The mode matters for exactly one shape, **read-then-write** — which is the shape the concurrency API
pushes. A deferred transaction pins a read snapshot at its first read; if another connection commits
before its first write, the upgrade fails with `SQLITE_BUSY_SNAPSHOT` (517), which `busy_timeout` cannot
retry and the provider retried anyway until *its* command timeout elapsed — a 30 s stall before an
unretryable error. `Immediate` takes the write lock at `BEGIN`, so that failure is unreachable;
contention becomes a plain `SQLITE_BUSY` wait which `busy_timeout` does retry. The cost is that the
write lock is held for the whole transaction including its reads, so concurrent writers serialize —
which is why it is opt-in. Past the wait the caller still sees `SQLITE_BUSY` and must retry. Tests need
a **file** database. → rationale#transactions

Deliberately **not** added: a general `SQLITE_BUSY` retry policy. If it ever returns, the right shape is
an opt-in retry on `ExecuteInTransactionAsync` alone with a documented idempotency requirement.

**Batch chunking.** A batch is split into chunks of `SqlGenerator.MaxBatchItemsPerStatement` (500) —
an upsert binds 2N parameters, so one unbounded statement blew past `SQLITE_MAX_VARIABLE_NUMBER` (32766)
at ~16383 items. `GenerateBulkUpsertSql`/`GenerateBulkDeleteSql` throw `ArgumentOutOfRangeException`
above the cap so the unbounded shape cannot be reintroduced. `DocumentOperations.RunBatchAsync` runs the
loop and sums affected rows; a multi-chunk batch is wrapped in a transaction so it stays all-or-nothing,
a single-chunk batch is left alone. `DocumentOperations` takes an `inAmbientTransaction` flag — explicit
rather than probed off `SqliteConnection`, which does not expose its pending transaction publicly.

Every item is validated **and serialized** before the first chunk runs, so a bad item anywhere throws
with nothing written. `UpsertManyAsync` rejects duplicate ids naming the id and both indices; SQLite
would otherwise fail the whole statement with the opaque `ON CONFLICT DO UPDATE command does not affect
row a second time`. `DeleteManyAsync` drops repeats silently — an `id IN (...)` list is unambiguous.

`GetManyAsync<T>` borrows that 500-item chunk size but runs its own loop rather than `RunBatchAsync`,
which sums affected-row counts a read never produces and opens a transaction a read does not need.
`SqlGenerator.GenerateBulkGetSql` reuses the `id IN (@Id0..@IdN)` shape and the caps of
`GenerateBulkDeleteSql`, and the batch writers bind explicit `@Id{i}`/`@Data{i}` parameters. The result
is an `IReadOnlyDictionary<string, T>` and not a list precisely so the caller can tell which ids were
missing: a missing id is an absent key, never a null value. An empty input short-circuits without a
round trip. Because a large read spans several statements, it is a point-in-time snapshot only when the
call is made on a transaction.

### Raw SQL escape hatch

The `Connection` property is gone (a pooled connection cannot be handed out to live indefinitely). Use
`ExecuteRawAsync(Func<SqliteConnection, CancellationToken, Task<T>>)`, available on both the store and a
transaction — on a transaction the callback gets that transaction's connection, so raw commands enlist
in it. The connection is valid only inside the callback.

**Create commands with `connection.CreateCommand()`**, which copies the connection's active transaction
onto the command; a directly constructed `new SqliteCommand(sql, connection)` leaves `Transaction` null
and the provider refuses to execute it while a transaction is pending.

A callback that leaves a transaction on the connection does not poison the pool — the dirty-session
guard closes it. **Connection-local state is the other half of that sentence, and none of it is
covered** (filed as **C47**, open). The guard has no notion of session state and the factory applies the
PRAGMAs once, at physical open, so what a callback changes persists on that connection until it is
discarded or the store is disposed. A session-scoped `PRAGMA`, an `ATTACH`ed database and a `TEMP` table
were all **measured** to leak; the `TEMP` schema generally, `DefaultTimeout`, registered
functions/collations and loaded extensions carry the same way by the same mechanism. **`ExecuteRawAsync`'s
docs require a caller to restore such state before the callback returns** — a documented requirement, not
a runtime guard. Re-applying a list of PRAGMAs cannot close it, since `ATTACH` and `TEMP` are not
PRAGMAs. → rationale#raw-sql

**Three synchronous members on `IDocumentOperations` make the hatch usable** without re-deriving what
the store already knows: `GetTableName<T>()` resolves the table through the store's *configured*
convention, `SerializeDocument<T>(value)` returns the same UTF-8 JSON bytes the store writes (for
binding to a raw `jsonb(@Data)`), and `DeserializeDocument<T>(json)` turns a raw `SELECT json(data)`
column back into `T`. They need no connection, take no token, and are present on both the store and a
transaction. `DeserializeDocument` returns `default` for null/empty JSON and throws
`DocumentSerializationException` on malformed input; `SerializeDocument` throws `ArgumentNullException`
on a null value.

The same reasoning keeps teardown on `IDocumentOperations` rather than behind the hatch:
`DeleteAllAsync<T>` (`DELETE FROM [t]`, returns rows deleted, table survives), `DropTableAsync<T>` and
the two `DropIndexAsync` overloads. Both drops are `IF EXISTS`, so they are idempotent.
`DropIndexAsync<T>(x => x.Email)` derives its name through the same `JsonPathResolver` +
`GenerateIndexName` pair `CreateIndexAsync` uses; an index created under an explicit name has to go
through the string overload.

Deliberately *not* added: any predicate/projection query API — richer filtering stays raw SQL.

### Migrations

`IMigration` implementations provide `UpAsync`/`DownAsync`; applied versions are tracked in
`__store_migrations (version, name, applied_at, checksum)`. They are reached through **`IDocumentStore`**
— `MigrateAsync(migrations[, MigrationOptions])`, `GetAppliedMigrationsAsync`,
`GetCurrentMigrationVersionAsync`, `RollbackToVersionAsync` — and **not** through `IDocumentOperations`,
so a migration can never run on a caller's `IDocumentTransaction`: it owns its own transaction.
`MigrationRunner` is `internal`; each store method rents one pooled connection and builds a runner over
it. There is no DI registration and no migrate-on-startup hosted service.

**The two documented reads do not write.** `GetAppliedMigrationsAsync` and
`GetCurrentMigrationVersionAsync` probe `sqlite_master` for the history table and answer `[]`/`0` when
it is absent, rather than calling `EnsureMigrationTableExistsAsync`; on a legacy three-column table the
applied-migrations read projects `NULL AS checksum` rather than triggering `AddChecksumColumnAsync`.
`ReadHistorySchemaAsync` (table **and** column) serves that read; `HistoryTableExistsAsync` (presence
alone) serves the version read. Only `MigrateAsync` and `RollbackToVersionAsync` create and upgrade it.
Both reads used to write twice on every call, at a measured 60x cost on a legacy table.
→ rationale#migrations

**"Already applied" is membership in the history table**, not `Version <= MAX(applied)` — the old
comparison silently skipped a back-filled migration and returned the same `false` as an already-applied
one. A version absent from history but below the current maximum throws `MigrationOutOfOrderException`
(carrying `Version`, `Name`, `CurrentVersion`) unless `MigrationOptions.AllowOutOfOrder` is set, which
applies it and logs a warning.

**Each apply and each rollback runs in its own explicit `BEGIN IMMEDIATE`, with the membership check
inside it.** The provider's default `BeginTransaction()` was already immediate, so the fix is the
*ordering*: the old code read the version before taking any lock, so two processes starting together
both ran `UpAsync` and the loser failed on the primary key. Transactions are per migration, not per run:
if 1 and 2 commit and 3 throws, 1 and 2 stay applied.

**What a migration author is told** — documented on `IMigration` itself, because that is where the
implementer is standing. The runner's transaction is already open when `UpAsync`/`DownAsync` receives
the connection, and the runner commits it, so the callback's SQL and the history row move as one unit.
An `UpAsync` that throws leaves nothing behind; a `DownAsync` that does leaves the migration **still
applied**.

- **Do not open, commit or roll back a transaction.** `connection.BeginTransaction()` throws, which is
  loud. A bare `COMMIT` is not — it ends the runner's transaction, making the work and its history row
  separately durable, and **no particular end state is guaranteed** afterwards.
- **Build commands with `connection.CreateCommand()`** — the same rule `ExecuteRawAsync` documents.
- **Do not call back into the store.** This is the run's only lease; at `MaxPoolSize = 1` it waits for a
  connection the run itself is holding. If it obtains another lease it runs on a *different* connection,
  so it is not enlisted in the migration's transaction and is not covered by its atomicity.
- **Override `Checksum` when you override `UpAsync` and change what runs.** The base value digests only
  the `upSql` handed to the constructor, so a subclass that adds or replaces work must `override` the
  (now `virtual`) property and return a value that changes with its own behaviour; an override that only
  delegates to `base.UpAsync` is already covered and needs nothing, and a `DownAsync` override needs
  nothing either. `public new string Checksum` is **silently ignored** — the runner reads through
  `IMigration`, so interface dispatch lands on the base. Nothing detects the omission; this is the
  subclass author's responsibility. Opting out is `=> null!` (the property is declared non-nullable),
  and it is one-way rather than a repair: a history row already holding a non-null checksum is simply
  never verified again. → rationale#migrations

`RollbackToVersionAsync` states the same shape its `MigrateAsync` sibling always did.

**Non-transactional statements stay unreachable from inside a migration, by design.** `VACUUM` fails
loudly; `PRAGMA foreign_keys = OFF` is **silently ignored**, so the 12-step table rebuild — the
canonical reason to want it — is not running under the assumption it was written against. Both belong
outside the runner, in `ExecuteRawAsync`, with the PRAGMA restored before that callback returns. An
opt-out flag that ran `UpAsync` outside the transaction was considered and **rejected**: it would
decouple the work from the history-row `INSERT`, and "half-applied" there is precise and unrecoverable.
`MigrationConnectionContractIntegrationTests` pins the whole contract. → rationale#migrations

**Checksums.** `IMigration.Checksum` is a default interface member returning null (so existing
implementations still compile); `Migration.Checksum` is **`virtual`** and returns the uppercase SHA-256
hex of its **up** SQL only —
the down SQL is not part of what was applied, and rollback never verifies checksums at all. The checksum
is stored with the history row and compared on later runs, throwing `MigrationChecksumMismatchException`
(`ExpectedChecksum` = stored, `ActualChecksum` = supplied) unless `MigrationOptions.VerifyChecksums` is
false. Either side null skips the check, which keeps pre-checksum history usable: a legacy three-column
table is `ALTER TABLE`-ed on first use, re-checking `pragma_table_info` under an immediate transaction
so two starting processes cannot both issue the ALTER.

**Input is validated before anything runs**: a null element or a duplicate version throws
`ArgumentException` naming the version and both indices, a negative rollback target throws
`ArgumentOutOfRangeException`, and `RollbackToVersionAsync` refuses the whole range when any migration in
it has no definition.

**`IMigration.Version` must be positive**, enforced at every entry point rather than only in
`Migration`'s constructor — one `RequirePositiveVersion` helper called from three sites (`Validate`'s
per-element loop, plus `ApplyMigrationAsync`/`RollbackMigrationAsync`, which do not go through
`Validate`). A hand-written `IMigration` at version 0 used to **apply** while
`GetCurrentMigrationVersionAsync` answered `0` (the "nothing applied" sentinel) and
`RollbackToVersionAsync(0)` would not roll it back: applied, unreportable and unrollbackable.
**Behaviour break** for such a consumer, and the only outcome that says so. → rationale#migrations

Source breaks accepted: `MigrationRunner` is no longer public, the five members on `IDocumentStore` break
an external implementation of that interface, and `MigrateAsync(migrations, default)` is ambiguous
because the `MigrationOptions` overload was added rather than a parameter inserted.

## AOT compatibility

The library is Native-AOT / trim compatible as a **single package**
(`<IsAotCompatible>true</IsAotCompatible>` turns on the trim + AOT analyzers). How each former blocker
was handled:

1. **Serialization** — `JsonHelper` goes through the AOT-safe `JsonTypeInfo<T>` overloads, resolving type
   metadata from `DocumentStoreOptions.SerializerOptions`. AOT consumers pass options backed by a
   source-generated `JsonSerializerContext`
   (`new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }`). When none is supplied,
   `JsonHelper.CreateDefaultReflectionOptions()` provides a reflection fallback — the single
   quarantined, `[UnconditionalSuppressMessage]`-annotated spot. That suppression is unconditional, so
   it deletes the publish-time warning rather than answering it; what makes it honest is
   `DocumentStoreOptions.ThrowIfSerializerOptionsUnusable`, which runs at **both** boundaries so the
   helper is genuinely unreachable under AOT instead of merely documented as such.
   **Scope limit:** `IsDynamicCodeSupported` is false under Native AOT *or* wherever .NET's
   `DynamicCodeSupport` feature switch is set false, but a self-contained `PublishTrimmed` publish
   without AOT reports it **True** — so a trimmed-but-jitted deployment must supply a source-generated
   context itself, or the fallback will round-trip documents whose accessors the trimmer removed.
2. **Dapper** — removed entirely, replaced by `Core/SqliteCommandExtensions.cs` (explicit parameter
   binding + ordinal reads).
3. **LINQ-predicate query + `SelectAsync` projections** — removed (needed
   reflection/`Expression.Compile`). `ExpressionToJsonPath` was deleted; the surviving expression APIs
   only read member names.
4. **`SchemaIntrospector`** — the `dynamic` PRAGMA read was rewritten to ordinal `DbDataReader` access.

`examples/AotVerification` is the only gate for the AOT-null branch, since the xUnit runner is JIT. Its
three shapes and what each now asserts (they changed with C46) are in → rationale#options-snapshot.

When adding features, keep them AOT-clean: no reflection-based serialization (route through `JsonHelper`
+ `JsonTypeInfo<T>`), no `Expression.Compile`, no `dynamic`. A `dotnet build` must stay free of
`IL2xxx`/`IL3xxx` warnings.

## Conventions

- File-scoped namespaces; `sealed` on non-inheritable classes; `readonly` fields; `_camelCase` private
  fields. Expression-bodied members for one-liners.
- Library code uses `.ConfigureAwait(false)` on awaits and `Async` suffix on async methods.
- Validate arguments up front and fail fast (`ArgumentException`/`ArgumentNullException.ThrowIfNull`).
  Rethrow inside `catch` on transaction rollback rather than wrapping.
- All public API needs XML doc comments (`GenerateDocumentationFile` is on; missing docs surface as
  warnings).
- Package versions are centralized in `Directory.Packages.props` (Central Package Management); csproj
  files carry a bare `<PackageReference Include="..." />` with no version.
- New features need both a unit and an integration test.
- **New API that takes options takes them through an overload, never an inserted parameter** — every
  caller passing a trailing `CancellationToken` positionally would break. `IndexOptions`,
  `BlobWriteOptions`, `MigrationOptions` and `TransactionMode` all arrived this way; the cost is that a
  bare `default` at the call site becomes ambiguous and needs a cast.
- Don't add AI-attribution trailers (`Co-Authored-By: Claude`, "Generated with Claude Code") to commits
  or PRs.
- Never auto-commit — stage and commit only when the user explicitly asks.

## Where to look first

- `src/LiteDocumentStore/Core/DocumentStore.cs` — the public surface: rents a connection per operation
  and delegates to `DocumentOperations`.
- `src/LiteDocumentStore/Core/DocumentOperations.cs` — where every document operation is actually
  implemented (shared by the store and by transactions).
- `src/LiteDocumentStore/Core/SqliteConnectionPool.cs` — connection lifetime, PRAGMA-once configuration,
  the `:memory:` guard, both budgets.
- `src/LiteDocumentStore/Core/DocumentStoreOptions.cs` — the options, `Validate()` and `Clone()`;
  `SqliteConnectionStringGuard.cs` + `SqlitePageSizeGuard.cs` are the two guards it and the pool run.
- `src/LiteDocumentStore/Core/SqlGenerator.cs` — the JSONB SQL contract; change SQL here, nowhere else.
- `src/LiteDocumentStore/Conventions/TableNamingConventions.cs` + `TableNameCollisionGuard.cs` — how a
  type becomes a table name, the deliberately non-injective fold, and the guard that makes a residual
  collision loud.
- `src/LiteDocumentStore/Query/DocumentQuery.cs` — the composable filter builder; validation and
  bound-value normalization live here.
- `src/LiteDocumentStore/Query/DocumentPatch.cs` — the field-level update builder; the JSON-text
  carve-outs for `bool`/`decimal`/wide `ulong` live here.
- `src/LiteDocumentStore/Indexing/IndexOptions.cs` + `IndexFilter.cs` — the index DDL options and the
  value-free partial-index filter.
- `src/LiteDocumentStore/Migrations/MigrationRunner.cs` — the history table, the membership check under
  `BEGIN IMMEDIATE`, and the checksum comparison.
- `src/LiteDocumentStore/Core/SqliteCommandExtensions.cs` — the raw ADO.NET helpers that replaced Dapper.
- `src/LiteDocumentStore/Serialization/JsonHelper.cs` — the AOT-safe serialization funnel
  (`JsonTypeInfo<T>` + reflection fallback).
- `src/LiteDocumentStore/Serialization/JsonPathResolver.cs` — expression-to-JSON-path derivation; where
  an index learns the name the serializer actually writes.
- `src/LiteDocumentStore/Extensions/ServiceCollectionExtensions.cs` — how consumers wire it up.
- `examples/AotVerification/` — end-to-end AOT smoke test with a source-generated context; CI publishes
  it Native-AOT with warnings as errors and runs the binary.
- `examples/Examples/Program.cs` — the sample dispatcher; add a new sample to the array there.
- `docs/DESIGN-RATIONALE.md` — why any of the above is the way it is.
