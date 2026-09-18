# Design rationale

Why the rules in `CLAUDE.md` are what they are: the measured evidence behind each one, the
alternatives that were rejected, and what a regression would look like.

`CLAUDE.md` states the rule and links here. **The rule is binding; this file is the evidence.**
Where the two conflict, the source wins, then `CLAUDE.md`, then this file.

Measurements were taken against SQLite 3.53.3 / Microsoft.Data.Sqlite 10.0.x on .NET 10 unless
stated otherwise. "Measured" means it was executed, not reasoned about; anything reasoned rather
than executed says so.

---

## sql-paths

### Why the path is interpolated rather than bound

SQLite matches a query against an expression index only when the indexed expression appears
**literally**. Binding the JSON path would silently disable every index `CreateIndexAsync`
creates — trading a performance cliff for an injection fix that the validator already provides.
`tests/LiteDocumentStore.IntegrationTests/SqlInjectionIntegrationTests.cs` pins both halves: the
injection is rejected, and the index is still used.

The same reasoning rejects supporting an apostrophe by doubling it: rewriting the emitted text
would stop it matching the index.

### Why the member rule mirrors SQLite instead of an identifier

A member used to have to be identifier-shaped (`[A-Za-z_][A-Za-z0-9_]*`), far narrower than
SQLite, which made a mundane configuration unusable. `JsonNamingPolicy.KebabCaseLower.ConvertName("FullName")`
is `full-name`, so a store on the BCL's own kebab-case policy — or any single
`[JsonPropertyName("full-name")]` — was refused by **every** typed path API at once:
`QueryAsync<Person, string>("$.full-name", …)`, `DocumentQuery<Person>.Where`, `DocumentPatch.Set`
and `CreateIndexAsync<Person>("$.full-name")` all threw from `ValidateJsonPath`, and the expression
overloads threw from `JsonPathResolver`. (`SnakeCaseLower` gives `full_name` and was never
affected, which is why it went unnoticed.)

Measured against 3.53.3 in `json_extract`, `jsonb_set`, `jsonb_remove` and `json_each`, all
unquoted: `$.full-name`, `$.a b`, an accented key, an emoji key, `$.2024`, `$.a$b`, `$.a]b`, and
keys carrying a newline or a tab all resolve — and an expression index over such a path is **still
used** (`EXPLAIN QUERY PLAN` → `SEARCH t USING INDEX ix (<expr>=?)`), which is the assertion that
matters: a path that validates but silently defeats the index would be worse than the refusal it
replaced.

A newline and a tab stay **accepted**, and a positive control pins that, so a later tightening
cannot quietly take them out alongside the NUL.

### U+0000

Widening the member rule to SQLite's label admitted a NUL, which the old identifier-shaped rule had
rejected — a regression the widening introduced, not a pre-existing gap.

`sqlite3_prepare` reads a NUL-terminated string, so a NUL in an interpolated path **truncates the
whole SQL statement at that byte**. The path always sits immediately after an opening apostrophe,
so the truncated prefix always ends inside an unterminated literal and SQLite always answers
`SQLITE_ERROR: unrecognized token` — confirmed across **every** generator that interpolates a path
(query predicate, `IN`, `json_each`, `IS NULL`, ordering, count, exists, patch set, patch remove,
create index, create composite index, index-filter term, query-by-path, add virtual column) by
preparing each truncated prefix against real SQLite. None is itself valid SQL, so there is no
statement-truncation primitive here.

The failure was therefore loud, but of the **leaked-provider-error** class: six typed APIs
(`QueryAsync`, `DocumentQuery.Where`, `DocumentPatch.Set`, `DocumentPatch.Remove`,
`CreateIndexAsync`, `AddVirtualColumnAsync`) handed the caller a raw `SqliteException` carrying a
truncated, misleading message for an argument the validator should refuse up front. Auto-named
index creation already refused it by accident, because `IsValidIdentifier` rejects the derived name
— the wrong diagnosis; an explicit index name reached the SQL error instead.

**Bound** as a parameter — which this library never does, deliberately — the quirk is worse and
silent: `json_extract(doc, @p)` with `$.a\0b` reads key `a`, and `jsonb_set`/`jsonb_remove` write
and remove it. That is what makes a NUL dangerous to anyone binding a path through `ExecuteRawAsync`.

**It is not a Tier 2 shape.** Quoting does not rescue it — measured both ways: interpolated
`$."a\0b"` fails with `unrecognized token`, bound `$."a\0b"` fails with `bad JSON path`. Unquoted,
`$."quoted"` and `$['bracket-quoted']` all fail, so such a key is unaddressable by every form;
`json_each` does list it, so one can exist in a stored document and simply cannot be reached.

### Tier 2 (quoted segments) — why it is deferred

Three shapes remain unreachable *and* reachable by quoting: a key containing `.`, a key containing
`[`, and the empty key. The dotted one is why Tier 2 is worth doing rather than documenting —
measured, `json_extract` returns NULL for it while `jsonb_set` and `jsonb_remove` **silently
no-op**, returning the document unchanged.

Tier 2 is a tokenizer emitting a canonical rendering (`$."a.b"`, `"` escaped JSON-style as `\"`,
**not** by SQL doubling — measured, `'$."a""b"'` silently resolves the wrong key), plus the same
rendering in `JsonPathResolver`. It stays deferred until someone actually needs one of those three
keys. `U+0000` is a fourth shape needing permanent rejection and is not part of it.

### Derived index names and the widened grammar

`GenerateIndexName` is `idx_{table}_{path}` with separators flattened and everything else kept, so
`$.full-name` derives `idx_T_full-name` — which `ValidateIdentifier` rejects against an `indexName`
no caller passed, the exact mis-attribution `RequireDerivableName` exists to prevent (it screened
only `[`).

It now screens the derived name through `SqlGenerator.IsValidIdentifier`, the non-throwing form of
the identifier rule, so that rule keeps one owner instead of being re-implemented in
`DocumentOperations`. The single-path derivation is the probe even for a composite index, since
both derivations apply the same transform to each path.

Three sites were already calling it (`CreateIndexAsync`, `CreateCompositeIndexAsync`, the composite
loop). The fourth, `DropIndexAsync<T>(Expression)`, had **no screen at all** and was measured
reporting `ParamName = "indexName"` for `DropIndexAsync<Person>(x => x.FullName)`; it now screens
too and reports against `expression`, its only parameter. Nothing becomes undroppable: the
expression overload could not have created such an index either, and `DropIndexAsync(string)` names
one directly.

Rewriting the brackets was rejected as a fix for the array-indexed case: the scheme already maps
distinct paths onto one name, so widening it adds collisions rather than removing any.

---

## table-naming

### What the old `type.Name` default cost

Silent cross-type data loss, not merely a shared table. Measured on two `Customer` records in
different namespaces: both mapped to `Customer`, the second `CreateTableAsync` no-opped through
`IF NOT EXISTS`, an upsert of one type overwrote the other's row at the same id, `GetAsync`
returned a **fabricated** document with `null` in a non-nullable member rather than failing, and
`GetAllAsync<T>` returned the other type's documents. No exception anywhere.

Generic types were separately unusable: `Box<int>`'s `Name` is `` Box`1 ``, so `ValidateIdentifier`
rejected it with `ParamName = tableName` — a parameter no caller passed, the same mis-attribution
class C41 and C06 fixed.

### Why the fold is not injective

Two collision families are knowingly accepted.

A segment's underscore is indistinguishable from a separator — namespace `A_` with type `B` and
namespace `A` with type `_B` both fold to `A__B` — and no escaping fixes it while the escape
character *is* the separator: a run of `n` underscores splits as `2a + s + 2b` with `a`/`b`
undetermined for any separator length `s`.

An argument's extent is not recoverable, since the arity says how many arguments follow, not where
each ends. Worth knowing: this second family is **not constructible from ordinary C#** — it needs
one dotted name to be both a namespace and a type, which is CS0101 inside an assembly and CS0434
across two referenced ones (both reproduced); reaching it takes `extern alias` or a reflectively
obtained type.

Closing either needs a self-delimiting encoding — length-prefixed segments or reserved markers,
`t_5MyApp5Sales5Order` — which is not a name anyone types into a SQL client, and readable tables are
the point of a store that stays open to raw SQL. The arity is kept in the generic rendering even
though it does not delimit: it costs two characters and separates two same-named generics of
different arity, which *is* constructible.

`TableNameCollisionGuard` makes the residual loud instead.

### Why the guard's dictionary is `OrdinalIgnoreCase`

SQLite folds ASCII case in identifiers, so `[Order]` and `[order]` are one table. Measured under an
ordinal comparer, two types whose names differed only in case shared a table and the first type read
back a fabricated document with `null` in a non-nullable member — the exact C07 shape the guard
exists to refuse. C# namespaces and type names are case-sensitive, so the default fold reaches it
too (`NsA.Customer` beside `nsA.Customer`), not only a custom convention.

It is per store instance and in process, so two processes opening one file with colliding types are
not covered — which needs both processes to use both types before any damage is possible. Cost is
one dictionary hit per operation, on a path whose cheapest operation is ~4.5 µs.

### Why nested-in-generic is refused

A scope decision, not an impossibility: `GetGenericArguments` returns the chain's arguments
flattened outermost-first, so each declaring level's arity would partition them. Document types of
that shape are rare enough that refusing beats freezing an on-disk encoding for them.

### Test consequence of the break

57 integration tests and one unit test hardcoded a table or derived index name; all now resolve it
through `store.GetTableName<T>()` (or `DefaultTableNamingConvention.Instance` where no store is
live), which is what `examples/` already did throughout.

Three of the four benchmark files were **not** touched on purpose — `ComparisonBenchmark`,
`SimplifiedComparisonBenchmark` and `ConnectionModelBenchmark` create their own `[TestDocument]`
baseline table on a hand-managed connection, so those literals are correct; only
`VirtualColumnBenchmark` queries a store-created table.

---

## connection-model

### Measured cost of pooling

From `benchmarks/LiteDocumentStore.Benchmarks/ConnectionModelBenchmark.cs` — `ConnectionModelBenchmark`
for the primitives, `StorePathBenchmark` for the store's own path. **Re-run both before changing
this design.**

| | vs the old single held connection |
|---|---|
| File DB read | **+2.1%** |
| Shared-cache in-memory read | +8.6% (the op is only ~9 µs, so fixed cost weighs more) |
| Allocations | +10% (~160 B: the lease plus the closure in `DocumentStore.RunAsync`) |
| 64 concurrent reads | **3x faster** than a `SemaphoreSlim(1)`-gated single connection |

In-memory users pay a further ~8% for shared-cache locking, which pooling requires. If the per-op
allocation ever matters, the closure in `RunAsync` can be removed by inlining rent/dispose into each
of the 20 operations — measured as the smaller half of the +2.1%, so it was not judged worth ~80
lines.

### Why the provider's own pool is opted out of

`Pooling=False` is forced in `SqliteConnectionPool.Normalize`. The provider's pool gives no "this
handle is new" hook, so every rent would have to re-apply the session PRAGMAs — measured at +3 µs,
or **+68% on a 4.5 µs read**. Owning the pool makes renting a semaphore wait plus a bag pop.

### The reserved keeper

A shared-cache in-memory database is destroyed when its **last** connection closes, so for those
stores — and only those — `Initialize` keeps the connection it opens as a *keeper*: never leased,
never discarded, never counted in `ConnectionCount`, closed last at disposal.

That is one permanent extra handle per in-memory store and one extra open per store lifetime (the
first lease no longer inherits `Initialize`'s connection); **no steady-state rent, return or discard
overhead** — the return and discard paths are byte-identical, and the rent path changed only on the
branch that *opens* a connection, where `FreshOrThrowIfDisposed` costs one volatile read per
physical open. A rent served from the idle bag, the steady state, pays nothing. A file database gets
no keeper and no extra handle; it pays that same one read per open (as does a blob read stream's
unpooled connection, through the twin check) and nothing else.

An idle keeper **adds no measurable cost, measured**: three further idle handles moved a
shared-cache in-memory read by −18%, i.e. noise in the wrong direction for a cost, which is what an
idle connection taking no table-level lock predicts. The +8.6% figure above is unchanged and was not
re-derived.

### What the missing keeper cost — the defect this section used to hide

The dirty-session guard did exactly its job and destroyed the database. At `MaxPoolSize = 1` on
`ForInMemory()`, a caller leaving a raw `BEGIN` in an `ExecuteRawAsync` callback made the guard
discard the only connection, and every row went with it — silently, with ordinary store operations
then succeeding against a fresh empty database, so nothing surfaced the loss. `MaxPoolSize > 1` only
delayed it: the cap is not a count, and the database dies whenever live connections reach zero.

The fix **repairs existing intent rather than adding a mechanism** — `Initialize`'s own doc already
promised that the first connection keeps an in-memory database alive for the pool's lifetime, and
the code banked it in the idle bag like any other, so the guarantee was written but never
implemented. Reserving it means no discard site needed a last-connection test, which is what keeps
the fix race-free: there is no check to race. `InMemoryKeeperIntegrationTests` pins every path that
closes an established connection.

### Dirty-session guard — the four shapes

Two probes are needed because neither sees the other's shapes (`Core/SqliteSessionState.cs`):

| what the callback left behind | `sqlite3_get_autocommit == 0` | provider transaction attached | next renter without the guard |
|---|---|---|---|
| raw `BEGIN` / `SAVEPOINT` | **yes** | no | writes silently enlist; `BeginTransaction` → `cannot start a transaction within a transaction` |
| an undisposed `conn.BeginTransaction()` | **yes** | yes | writes silently enlist; `BeginTransaction` → `does not support nested transactions` |
| raw `COMMIT` under a live transaction object | no | **yes** | writes are no longer transactional; `BeginTransaction` and `Close` both throw |
| raw `ROLLBACK` under a live transaction object | no | **yes** | every command throws `This SqliteTransaction has completed` |

`HasPendingTransaction` is SQLite's own answer, via `raw.sqlite3_get_autocommit` on
`SqliteConnection.Handle` (hence the direct `SQLitePCLRaw.core` reference — already
Microsoft.Data.Sqlite's own dependency). Measured at **42 ns and no allocation**, so `Return` runs it
on every one of the ~20 operations — ~1% of a 4.5 µs read.

`HasManagedTransaction` asks the provider instead, through `CreateCommand().Transaction`, which is
non-null exactly when a `SqliteTransaction` is still attached. That costs **223 ns and 192 B**,
roughly doubling per-operation allocation, so only `ReturnAfterExternalAccess` pays it — the two
`ExecuteRawAsync` overloads and `RunMigrationAsync`.

Note the asymmetry the guard cannot fix: closing a connection rolls back a *pending* transaction, so
a raw `BEGIN`'s writes vanish, but a raw `COMMIT`'s writes are already durable.

`RawSqlSessionStateIntegrationTests` pins all of it with `MaxPoolSize = 1`, asserting through a store
transaction rather than a plain write: three of the four shapes let a plain write through, so "the
next operation succeeded" would pass on a poisoned connection.

### Why the whole return path is `try`/`finally`

The slot is released in the `finally` because a waiter parked in `RentAsync` only ever wakes on a
free slot, and `SqliteConnection.Dispose()` is not exception-free — the raw-`COMMIT` shape fails with
`cannot rollback - no transaction is active`, which used to skip `ReleaseSlot()` on the disposed
branch and hang that waiter forever.

Three rules follow, since nothing on the path may throw while the caller's own exception may already
be propagating:

1. Only the **probes** sit inside a `catch`, and a probe that throws is logged and answered as
   *dirty* rather than cleaned up separately — the connection is then disposed of exactly **once**
   below, where a second `DiscardBrokenConnection` would double-decrement `ConnectionCount` and
   leave the connection open.
2. Every cleanup log goes through `QuietLog`.
3. `CloseQuietly` retries the close **once**, because that measured failure clears itself — the
   first `Dispose` throws while rolling back a transaction object with nothing to roll back, but
   detaches it on the way out, so the second attempt closes the connection instead of leaving the
   handle and its file lock alive until finalization.

`Dispose`/`DisposeAsync` drain through the same helper, so a throwing logger can no longer abandon
the rest of the idle bag with its connections open.

### Why the pool never disposes its `SemaphoreSlim`

`SemaphoreSlim.Dispose()` is safe only once every other operation on it has finished; with a waiter
parked in `WaitAsync` it clears the waiter list without completing it, so an operation queued for a
connection while the store was disposed would hang forever. A `SemaphoreSlim` that never exposes
`AvailableWaitHandle` holds no unmanaged resource, so there is nothing to release.

`Return`/`Discard` release the slot **even on the disposed path** — that is what wakes a parked
waiter, which then throws `ObjectDisposedException` from `ThrowIfDisposed` instead of hanging.

### Why every banking site re-checks `_disposed` after `_idle.Add`

`Dispose` flips the flag and drains the bag exactly **once**, so an add that lands after that drain
leaves the connection — with its file lock, or a shared-cache in-memory database — open until
finalization. Not a rare interleaving: measured at **313 of 400** barrier-synchronized attempts (one
thread disposing the lease, one the pool, `MaxPoolSize = 1`), and 330 of 400 on a later re-run.

Re-reading after the add fixes it because `ConcurrentBag.TryTake` hands the connection to exactly one
of the two racers, so it is closed exactly once; the happy path pays one volatile read rather than a
lock on the per-operation return path.

Three sites bank a connection and all three re-check — `ReturnCore`'s clean branch plus
`Initialize`/`InitializeAsync`, which pass `ThrowIfDisposed` on the way in and can still be inside the
connection factory when disposal runs. `Discard` needs none of it: it closes rather than banking.
`SqliteConnectionPoolTests` pins the return path by stress (nothing between the read and the add is
injectable) and the initialization path deterministically, through a factory that parks inside the
open until disposal has finished; moving the re-check back before the add fails the first pair.

### Why the SQLite version guard lives in the pool

`IConnectionFactory` is public — a consumer-supplied factory would otherwise open unguarded
connections. Same reasoning puts `SqlitePageSizeGuard` there.

The result is deliberately **not cached**: it is process-wide constant, but a pool opens at most
`MaxPoolSize` connections in its lifetime, so caching would trade process-wide mutable state for a
handful of sub-microsecond queries. Because SQLite lets a user-defined function override a built-in,
the too-old path is actually testable — `SqliteVersionGuardIntegrationTests` spoofs
`sqlite_version()` to `3.44.2`.

### The `DefaultConnectionFactory` seam

Because `SqliteConnection` is unsealed and its `CreateCommand()` is virtual, a factory can hand the
store a connection subclass that observes every statement a path issues, while keeping correct PRAGMA
behaviour by forwarding configuration to the default factory. `BlobSavepointCancellationIntegrationTests`
is built on exactly that, and it is what turns a microsecond-wide cancellation race into a
deterministic test; `IndexCreationRaceIntegrationTests` reuses the seam.

Publishing the default factory did not make that technique possible **in this repository** — the test
projects have `InternalsVisibleTo` and could always have constructed the internal one — it made it
available to **consumers**, who previously could supply a factory but had to re-derive its whole body.

---

## quiet-log

The rule the pool discovered is not the pool's: **a log must never be able to lose the resource
around it** — neither the hand-back on a release path, nor the connection just opened on a rent path.

Every such path is guarded by a one-shot flag (`_released`, `_disposed`, `_disposing`), so a throw
does not merely lose the message — it skips the release, and no retry can ever reach it again. A
caller-supplied `ILogger` is caller code, and one that starts failing mid-process (a file or network
sink that dies) is enough. `MaxPoolSize` occurrences later, the store deadlocks with nothing logged
anywhere: the C05 terminal symptom, reached without leaking anything.

`QuietLog` forwards its template and arguments verbatim under a scoped `CA2254` suppression, so
structured logging survives and the analyzer (an *error* here, `TreatWarningsAsErrors` +
`latest-recommended`) does not fire at the call sites. Fourteen sites use it: five in
`DocumentStoreTransaction` (`Release`, and the Debug/Warning pair in each of `DisposeAsync` and
`RollbackIfPending`), six in `DocumentStore`'s two WAL-checkpoint copies, and three in
`BlobReadStream`'s disposal helpers — where the slot was already safe in a nested `finally`, but a
throwing log still escaped the teardown and replaced the failure the caller needed to see,
contradicting that code's own comment.

The wrapper is half of each guard; the other half is that the hand-back moved into a `finally` —
`Release` around its `_lease.Discard()`/`Dispose()`, `Dispose`/`DisposeAsync` around their
`Release()`, and `DocumentStore.Dispose`/`DisposeAsync` around `_pool.Dispose()`. Either half alone
fixes the shipped bug, which is deliberate: the `finally` also covers whatever is added to those
methods later, and the quiet log also stops a *successful* rollback whose Debug line threw from being
recorded as a failed one, which set `_connectionCompromised` and discarded a healthy connection.

### The two deliberately loud paths

`CommitAsync` and `RollbackAsync` moved their `Release()` into a `finally` but keep the log **loud**,
because a caller is waiting on those methods and should learn that their logger is broken. The pool's
rent path is the same call.

Loud does not mean unguarded. `SqliteConnectionPool.Announce` is the rent path's version of the rule:
the connection it has just opened is still only a local, so a throw between
`Interlocked.Increment(ref _created)` and the `return` abandoned an open handle (with its file lock,
or a shared-cache in-memory database) until finalization and left `ConnectionCount` counting a
connection the pool no longer had. The slot was recovered by `TakeOrCreateAsync`'s own `catch` either
way, so nothing surfaced the loss and it repeated on every retry. The announcement now runs in a `try`
whose `catch` routes the connection through `DiscardBrokenConnection` — uncount, quiet warning, close
— before rethrowing. Both copies (`CreateConnection` and `CreateConnectionAsync`) go through it. The
provider `await` sits *outside* that `try`, so a commit that fails still leaves the transaction alive
for the caller to roll back or retry.

`LoggerFaultLeaseIntegrationTests` pins all of it with `MaxPoolSize = 1` and a logger armed only after
the store is built (one that throws from the start cannot get a store constructed) — including both
loud paths, and the recycled-not-discarded distinction.

Two guards are **defence for a state no public path reaches**: `Release`'s `catch` needs
`SqliteTransaction.Dispose()` to throw, which measurably only happens when `Rollback()` was never
attempted (a failed `Rollback` marks the transaction completed, so the later `Dispose` is a no-op) —
and every path into `Release` rolls back first. They are kept because the rule is cheaper to hold
everywhere than to re-derive per site.

---

## pool-budget

### What the unbounded rent cost

A slot is held for one operation, or — for a transaction — until it is committed, rolled back or
disposed, and `DocumentStoreTransaction.Release` was the *only* path that released it. So a caller who
dropped an `IDocumentTransaction` without disposing it (a missed `await using`) lost that slot for the
lifetime of the process, and `RentAsync(CancellationToken)` waited on `_slots.WaitAsync(cancellationToken)`
with no bound: `MaxPoolSize` such leaks hung every later operation **forever, with no exception, no
log and no metric** — measured, both this and a nested store call inside `ExecuteInTransactionAsync` at
`MaxPoolSize = 1` were still pending after 3 s.

The sibling budget in the same file had already made the opposite call (`RentBlobStreamSlotAsync`
bounded at 30 s, `~BlobReadStream` logging and releasing), so this is the operation budget catching up.

### The finalizer's shape

`~DocumentStoreTransaction` logs the leak at Error and calls `PooledConnection.Abandon()` →
`SqliteConnectionPool.AbandonLease()`, which uncounts and releases the slot **without touching the
connection** — a finalizer must not touch provider objects that have finalizers of their own. The log
sits in a `try` whose `finally` does the release (a throwing `ILogger` must not cost the slot the
finalizer exists to recover) inside a `catch`-all (an escape from a finalizer kills the process).

`GC.SuppressFinalize` goes in `Release()`, not in `Dispose`: commit and rollback release without
disposing, so the choke point is the one place every completion path passes. Three `CA1816`
suppressions carry that reasoning, since the analyzer only recognises the `Dispose` idiom.

C27's twin defect in `~BlobReadStream` — the same log-then-release in one `try`, which stranded a
blob-stream slot on a throwing logger — was fixed alongside it.

### Two consequences

The abandoned connection keeps its open transaction and its database lock until the provider finalizes
it, so the slot comes back before the lock does — a *write* right after a recovered leak can still fail
`SQLITE_BUSY`, which is why the pinning test probes with a read.

`AbandonLease` uncounts before releasing, so `ConnectionCount` under-reports live handles for that
window (it counts what the pool owns, like `DiscardBrokenConnection`) while the replacement the woken
waiter opens means live handles can transiently exceed `MaxPoolSize`.

### `PoolWaitTimeoutMs` must be cloned

It is where the pool reads it from: `Normalize` clones, so an omission in `Clone()` silently restores
the default and nothing else notices.

---

## options-snapshot

### Why a supplied `SerializerOptions` must carry a `TypeInfoResolver`

The asymmetry is the whole defect: `JsonSerializer.Serialize(value, options)` populates a missing
resolver, while `JsonSerializerOptions.GetTypeInfo(Type)` — which is what the AOT-safe
`JsonTypeInfo<T>` path has to use — does not.

Measured on .NET 10: `new JsonSerializerOptions { PropertyNamingPolicy = CamelCase }` serializes fine
on its own, and through the store it opened successfully, created tables, counted and deleted, while
every write threw and **every read leaked a raw `NotSupportedException`** (`JsonHelper.Deserialize`
caught only `JsonException`).

Leaving `SerializerOptions` null is unaffected outside AOT — that is the reflection fallback, which is
a resolver. Under Native AOT it is not a resolver the runtime can build converters for, so the sibling
check refuses null when `RuntimeFeature.IsDynamicCodeSupported` is false. ILC substitutes that property
as a constant, so the branch is decided at publish time rather than costing the JIT path a check that
can never fire.

`Validate()` runs before `CreateStore` constructs the store, so no connection is opened for options the
store will not honour (pinned by a fail-fast `IConnectionFactory` asserting zero open attempts).

### The mutation window, and why snapshotting beat re-checking

`Validate()` alone is bypassable: `CreateStore` validated, then called
`_loggerFactory.CreateLogger<DocumentStore>()`, then constructed the store — and a caller-supplied
logger factory is arbitrary caller code inside that window, on the same mutable options object both DI
registrations capture. An ordinary concurrent setter reaches the same window with no custom logger at
all.

Measured before the fix:

- A logger factory nulling `SerializerOptions` there was published as a real AOT binary and **silently
  serialized a reflection-only model as `{}`**.
- A logger factory swapping one valid in-memory connection string for another valid one (nothing
  bypassed, merely retargeted) made the store open a **different database than the one that was
  validated**. `PageSize`, `AdditionalPragmas`, `MaxPoolSize` and everything else `Validate()` covers
  were swappable the same way.

`internal` was no boundary either: the assembly is unsigned and `ExposeInternalsToTests` defaults to
true, so a project-reference consumer naming itself `LiteDocumentStore.UnitTests` calls the constructor
and skips `Validate()` outright.

`DocumentStoreFactory.CreateStore` now clones the caller's options and validates the *clone*, then reads
nothing from the caller's object again — the naming convention and the store both come from the
snapshot. `DocumentStore`'s constructor clones and validates once more, so the same guarantee holds for
the internal constructor the test projects use and for any path that skips the factory. Every read in
that constructor comes from the snapshot; re-reading `options` one line further down reopens the window
there. `SqliteConnectionPool.Normalize` still clones a third time, because the pool is constructed
directly in tests and should defend itself.

**C46 supersedes the factory-boundary half of the old paired check.** A mutation in that window is now
*ignored, not refused*. The constructor check is not dead: it still fires for **direct construction**,
the one remaining way a caller hands the store options it must refuse; and it is no longer a standalone
call, since the constructor's `snapshot.Validate()` ends in `ThrowIfSerializerOptionsUnusable`.

That helper holds **both** serializer rejections, resolver-less first then AOT-null, and both run at
both boundaries: a verification pass once found the constructor re-running only the AOT-null half, so
resolver-less options swapped in through the same window constructed successfully and failed at the
first serialization instead. Honouring half of a deliberately paired check is the asymmetry the pairing
exists to prevent.

`ParamName` is `nameof(SerializerOptions)` at both, naming the option to fix rather than the `options`
bag it arrived in, which costs a scoped `CA2208` suppression.

### What the snapshot cannot detach

`SerializerOptions` stays the caller's instance **deliberately** — the source-generated resolver and its
metadata cache must be shared for AOT correctness, and STJ freezes the instance after first use.

`TableNamingConvention` stays the caller's instance **necessarily**: it is a behaviour object like
`IConnectionFactory` and `ILogger`, neither of which the store has ever snapshotted, and
`TableNameCollisionGuard` invokes that same instance on every operation — so a caller holding a mutable
convention can change table names under a live store. That is why `ITableNamingConvention`'s docs
require an implementation to be deterministic (one type, one name, for a store's lifetime); it is a
documented requirement, not a runtime guard, since the claim dictionary and every derived index name
already depend on it.

`AdditionalPragmas` is genuinely detached: `[.. AdditionalPragmas]` is a new list and its elements are
immutable strings.

### What each half is pinned by

The factory's snapshot is the measured repro and is covered deterministically, in a unit and an
integration test: reverting `CreateStore` to hand the caller's object through fails both. The
constructor's `Validate()` is covered too — removing it fails two unit tests.

`Clone()` completeness is load-bearing for **every store construction**, not only the pool's
normalization: a settable property left out silently reverts to its default on every store.
`OptionsSnapshotTests.Clone_CopiesEverySettablePublicProperty` reflects over `DocumentStoreOptions`,
sets every settable property to a non-default value and compares the clone property by property.

The constructor reading from the *snapshot* rather than from `options` has no deterministic public
seam: nothing inside the constructor body is injectable, so it only matters against a writer mutating
the object concurrently with that body.
`OptionsSnapshotTests.Constructor_WithOptionsMutatedConcurrently_NeverOpensConnectionsForUnvalidatedOptions`
stresses it the way `SqliteConnectionPoolTests.Return_RacingDispose` stresses its own interleaving: 400
barrier-synchronized attempts, one thread constructing and one flipping `PageSize` between 4096 and
777. `PageSize` is the probe because `Validate()` rejects a non-power-of-2 while the pool's `Normalize`
re-checks only the connection string, so a value that slips in after validation reaches the connection
factory instead of being caught again — measured, reverting the four constructor reads to `options`
fails it on every run.

### What `examples/AotVerification` still gates

The AOT-null branch is unreachable from xUnit (the runner is JIT), so that example is its only gate.
**C46 changed what two of its three shapes assert.**

- Shape 1 is unchanged and gates the AOT-null half: resolver-less options are refused by `Validate()`
  before a store exists.
- Shapes 2 and 3 still drive the two hostile `ILoggerFactory` implementations — one nulling
  `SerializerOptions` inside `CreateLogger`, one swapping in resolver-less ones — through the DI
  registration, but the expected outcome is now *ignored* rather than *refused*. Each asserts that the
  caller's object really was mutated, that construction succeeded, and then **round-trips a `Person`
  through the source-generated context**, which is the positive evidence that the validated context was
  used.

What a regression looks like there is worth stating, because it is *not* the `{}` C12 measured:
reverting only the factory snapshot leaves the constructor cloning the **mutated** options and rejecting
them in its own `Validate()`, so both shapes fail at resolution rather than round-tripping an empty
document. Reaching `{}` needs both guards gone, and only for the nulled half — resolver-less options
fail inside `JsonHelper`'s `GetTypeInfo` instead of falling back.

The tripwire `UnusedConnectionFactory` those two shapes used is deleted.

**No longer gated by this example:** the *constructor's* AOT-null check. `DocumentStore` is internal, so
the example cannot construct one directly, and no public path now delivers a null `SerializerOptions` to
it. The resolver-less half of the constructor check is JIT-reachable and is pinned by
`DocumentStoreTests.Constructor_WithSerializerOptionsCarryingNoResolver_ThrowsNamingSerializerOptions`;
the constructor's AOT-null half is covered by inspection only.

The example is also its own counterexample for how `IsDynamicCodeSupported` is decided: `PublishAot`
writes the `DynamicCodeSupport` switch into the project's runtimeconfig, so a plain `dotnet run` of it
already reports the property **false**, with no native publish involved.

---

## json-metadata

Coverage is per type, so options validation cannot see the other half: a resolver that *is* present but
does not cover `T` — a source-generated context missing a `[JsonSerializable]` — produces the same
`NotSupportedException` at the operation. Both `JsonHelper.Deserialize` overloads catch it beside
`JsonException`, so it arrives as `DocumentSerializationException` with a message naming the metadata
gap rather than the framework's source-generation boilerplate.

`GetTypeInfo`'s `InvalidOperationException` is the third shape of the same problem: metadata the
configured options cannot produce. It is raised when the type's own JSON contract is invalid (two
members mapping to one `[JsonPropertyName]`, an ambiguous `[JsonConstructor]`) or when a caller-supplied
`IJsonTypeInfoResolver` fails, and it is reachable with **default** options — an ordinary document type
with colliding property names leaked a raw `System.InvalidOperationException` out of `UpsertAsync`,
`SerializeDocument` and `DeserializeDocument` alike.

### Why it is caught around `GetTypeInfo` alone

The three methods each run two statements — resolve the metadata, then call `JsonSerializer`. The
`JsonException` and `NotSupportedException` clauses deliberately wrap **both**, because both legitimately
arrive from either side: the missing-`[JsonSerializable]` case answers `NotSupportedException` at the
resolve, and a converter can answer it at the serializer call.

The `InvalidOperationException` clause cannot, because **STJ propagates an exception other than
`JsonException`/`NotSupportedException` unchanged out of a custom `JsonConverter`** — so a clause around
both statements relabels a converter's own `InvalidOperationException` as a metadata failure, which is
worse than the raw exception it replaces: an unhelpful error becomes a confidently wrong one pointing at
the wrong file. Measured before the narrowing: a converter whose `Write` threw
`InvalidOperationException("converter write failed")` surfaced as `DocumentSerializationException` saying
the type metadata was invalid.

So the resolve lives in `JsonHelper.ResolveTypeInfo<T>`, which catches `InvalidOperationException` around
that one statement and nothing else; the `DocumentSerializationException` it throws derives from neither
of the outer clauses' types, so it passes through them untouched, and `GetTypeInfo`'s own
`NotSupportedException` still reaches the outer clause because the helper does not catch it.

The sibling `ArgumentException` `GetTypeInfo` documents (`typeof(void)`, a pointer, a ref struct, an open
generic) is **unreachable and deliberately unguarded**: the only argument is `typeof(T)`, and the CLR
refuses such a type as a generic argument before the call is made — measured,
`MakeGenericMethod(typeof(List<>))` throws `InvalidOperationException` and `MakeGenericMethod(typeof(void))`
throws `ArgumentException`, both from reflection rather than from the store.

---

## pragmas

Four settings were applied and silently ignored, each measured against real SQLite before being fixed.

### `PRAGMA page_size` must precede `journal_mode`

SQLite refuses to change the page size of a database in WAL mode, so the old order (WAL first) made
`PageSize` a no-op *even on a brand-new file* — an 8192 request read back as 4096.

On an *existing* database the PRAGMA is ignored regardless of order, so `SqlitePageSizeGuard` reads the
value back on every physical connection. `PageSize = 0` is the escape hatch — no statement, no check,
keep whatever the database has — which is what a store opening databases created elsewhere wants.
Converting an existing database needs a `VACUUM`, and that only works outside WAL mode (measured: in
WAL, VACUUM runs and leaves the page size unchanged).

### `EnableForeignKeys = false` did nothing

Microsoft.Data.Sqlite opens connections with `foreign_keys` already ON, so skipping the statement left
them on. Both states are now stated: `PRAGMA foreign_keys = ON|OFF`.

### WAL on an in-memory database

`PRAGMA journal_mode = WAL` answers `memory` there — not an error, not honoured — and it also armed the
dispose-time WAL checkpoint against a database with no WAL. The `ForInMemory`/`ForSharedInMemory` presets
already set `EnableWalMode = false`, so only a hand-written connection string hits this.

### `BusyTimeoutMs` was a floor, not a bound

`PRAGMA busy_timeout` bounds SQLite's busy handler *within one attempt*; Microsoft.Data.Sqlite then
re-runs the whole attempt while its command timeout has not elapsed, so the effective wait on a contended
statement is max(`busy_timeout`, command timeout) — 30 s with the provider's default, whatever
`BusyTimeoutMs` said.

Measured on a blocked `BEGIN IMMEDIATE`: 250 ms busy timeout returned after ~2 s under `Default Timeout=2`
and ~4 s under `Default Timeout=4`, while a 3000 ms busy timeout under `Default Timeout=1` took ~3 s.

`DefaultConnectionFactory` now also sets `connection.DefaultTimeout` from `BusyTimeoutMs` (seconds,
rounded up, **floored at 1**), unless the connection string states `Default Timeout`/`Command Timeout`,
which then wins.

The floor is not cosmetic: `DefaultTimeout = 0` means *retry forever* to the provider, not "fail now" —
measured, a blocked `BEGIN IMMEDIATE` never returned with it at 0, whatever `busy_timeout` said — so
`BusyTimeoutMs = 0` would have turned "do not wait for locks" into an unbounded hang. The loop is also
second-granular (`busy_timeout = 0` with `DefaultTimeout = 1` surfaced `SQLITE_BUSY` after 1142 ms), so
~1 s is the shortest bound the provider can express and "fail immediately" is not reachable at all.

`SqliteConnectionStringGuard.SpecifiesCommandTimeout` answers off the base `DbConnectionStringBuilder`,
because `SqliteConnectionStringBuilder` reports every keyword as present and returns 30 for the absent
ones. It is applied **before** the PRAGMA block, not after: those statements run under the command timeout
too, so applying it last left a contended `PRAGMA journal_mode = WAL` waiting the provider's 30 s at
connection open. It reads the presence check off `connection.ConnectionString` rather than the options,
since `ConfigureConnection` is public and takes a caller-supplied connection.

This sits in the factory rather than the pool: it applies an option, it is not a correctness guard.

### PRAGMAs deliberately *not* given typed options

`mmap_size`, `temp_store`, `journal_size_limit`, `wal_autocheckpoint`, `auto_vacuum`:
`AdditionalPragmas` already applies any of them to every physical connection, so typed properties would
add surface and no capability — and the "unbounded WAL growth" premise was wrong, since
`wal_autocheckpoint` defaults to 1000 pages and is already on.

`PRAGMA optimize` on close was rejected separately (it writes, takes a write lock, and would have to run
per connection at pool disposal with every failure swallowed), and `auto_vacuum` would need a second
readback guard like `SqlitePageSizeGuard`. A general `SQLITE_BUSY` retry policy stays rejected.

### What a custom `IConnectionFactory` owes

A delegating decorator gets every behaviour above for free. Publishing `DefaultConnectionFactory` is the
whole fix — no configure helper, no new type — because a helper covering only `ConfigureConnection` would
still leave a decorator reproducing `CreateConnection` and both of its dispose-on-throw paths.

A factory that does **not** delegate owes every option it claims to honour. What differs is not whether
they must be applied but **how loudly the omission surfaces**:

| option | omission surfaces as |
|---|---|
| `EnableForeignKeys` | **silent** — measured through a naive factory: `PRAGMA foreign_keys` came back **1** where the default gives **0** |
| `DefaultTimeout` derivation | **silent** — stayed at **30** with `BusyTimeoutMs = 250` where the default gives **1** |
| `PageSize` (and its ordering) | **detected** — `SqlitePageSizeGuard` refused the naive factory's WAL-before-`page_size` ordering with `IncompatiblePageSizeException` naming 8192 requested against 4096 actual |
| in-memory + WAL | **costs a factory nothing** — `SqliteConnectionStringGuard` rejects it during options validation, before any connection is opened |

`ApplyCommandTimeout` stays `private`, and `SqliteConnectionStringGuard`/`SqliteCommandExtensions` stay
`internal`: a delegating factory never needs them, and a non-delegating one can re-derive both from
public BCL surface (`DbConnectionStringBuilder`, `CreateCommand`).

`DefaultConnectionFactoryTests.DefaultConnectionFactory_IsPubliclyConstructibleAndSealed` pins the
accessibility by **reflection** rather than by constructing the type, since `InternalsVisibleTo` makes a
plain `new DefaultConnectionFactory()` compile either way; `DelegatingConnectionFactoryIntegrationTests`
pins the two measured behaviours a decorator inherits.

### WAL checkpoint on disposal

The rent is **bounded** (`DocumentStore.WalCheckpointRentTimeout`, 5 s) — an unbounded wait would let one
leaked lease hang `Dispose` forever — and the checkpoint is skipped on timeout, which costs only the
`TRUNCATE`: SQLite checkpoints the WAL itself when the last connection closes. Same reason it is gated on
`EnableWalMode` up front, so a non-WAL store pays no rent + `PRAGMA journal_mode` round trip per dispose;
the trade-off is that an existing WAL database opened with `EnableWalMode = false` skips it.

---

## connection-string-guard

### Why classification is structural, not substring matching

Substring matching (`Contains("mode=memory")`, `Contains("cache=shared")`) let five measured shapes past
both rejections — the same failure each time, a pool multiplying one request into an empty database per
connection, with no exception and no log.

The guard now discards any `#` fragment, splits a `file:` data source at its first `?`, and parses the
query the way SQLite does: `&`-separated, keys *and* values percent-decoded, and the **last** occurrence
of a repeated parameter winning. The `Mode=`/`Cache=` keywords fill in only what the query omits —
measured both directions: `file:x?mode=memory&cache=private;Cache=Shared` opens private,
`file:x?mode=memory;Cache=Shared` opens shared.

Five shapes that used to be accepted now throw:

| shape | why |
|---|---|
| `file::memory:` | private per connection |
| `file:?mode=memory&cache=shared`, `Mode=Memory;Cache=Shared` with no `Data Source` | an **empty URI filename** is private however shared the cache claims to be |
| `…&cache=shared&cache=private` | last wins |
| `file:%00x?…` | SQLite truncates the name at the first NUL, which a raw-path emptiness test would call shared |
| `file:x?mode=memory#ignored&cache=shared` | SQLite discards the fragment, so it opens private |

The fragment is cut from the **raw** string, before any decoding: a `%23` is data, and measured,
`file:x%23y?mode=memory&cache=shared` opens shared in-memory under the filename `x#y`.

`Data Source=:memory:` stays private whatever `Cache=` says, because SQLite shares an in-memory database
only through a URI filename; `file::memory:?cache=shared` is genuinely shared and is accepted.

### Why classification is case-sensitive

Mirroring SQLite rather than over-rejecting. Measured: `FILE::MEMORY:` fails to open with SQLite Error 14,
`mode=MEMORY` is not a mode, and an uppercase `FILE:` prefix is not a URI at all (Windows opened a file
called `FILE`). None of them names an in-memory database, so the guard leaves them to SQLite — a
behavioral change over the old `OrdinalIgnoreCase` matching, which turned an already-broken uppercase
spelling into an `ArgumentException` instead of the `SqliteException` it actually earns.

`+` and a malformed escape are likewise literal to SQLite (`cache=shared+` → `no such cache mode: shared+`),
so `Uri.UnescapeDataString` is the right decoder and `WebUtility.UrlDecode` is not; tests pin that.

### `ForSharedInMemory(cacheName)` validation

It validates its own argument rather than letting the guard blame a `ConnectionString` the caller never
typed: blank, or containing `;`, `?`, `&` or `#`.

The `;` matters most — measured, `ForSharedInMemory("x;Data Source=evil.db")` appended a second
`Data Source` keyword, the last one won, and an **on-disk file was created** with WAL honoured while every
guard stayed silent; `#` turned the in-memory store into a file DB the same way.

`%` is deliberately *not* rejected: `file:a%20?mode=memory&cache=shared` shares fine, and delimiter
injection through an escape is impossible because SQLite splits on the raw `?` before decoding.
`DocumentStoreOptionsBuilder.UseSharedInMemory` delegates to the preset, so one validation covers both
entry points.

### The empty data source

`EmptyName` was computed on both `Classify` branches and then only ever consulted as
`shape.InMemory && (… || shape.EmptyName)`, so `Data Source=`, `Data Source=file:`,
`Data Source=file:?cache=shared` and a connection string carrying **no** `Data Source` keyword at all
passed `Validate()` with no exception and no log. SQLite opens a private temporary database for an empty
filename and deletes it when *the connection* closes, so the pool multiplied one configured database into
one per connection.

Measured at `MaxPoolSize = 4` with four concurrent transactions each re-issuing the idempotent DDL: four
documents written with no exception, `CountAsync` = 1, three of the four `GetAsync` calls null,
`IsHealthyAsync` still true — C08's exact shape on disk instead of in memory. It is silent only because
the DDL is re-issued per connection; with the DDL run once at startup, connection #2 fails loudly with
`no such table` on a store that just created it. The C48 keeper cannot rescue it either, since
`IsSharedInMemory` requires `!EmptyName`.

The check sits **after** the in-memory rejection so the in-memory spellings of an empty filename keep
reporting themselves as private in-memory, the more specific diagnosis. **The WAL rejection needed no
equivalent split**, because an empty data source is refused outright whatever `EnableWalMode` says, and
naming a journal mode would understate a configuration the store cannot pool at all (the bonus defect it
would otherwise report: `PRAGMA journal_mode = WAL` answers `delete` on an unnamed temp database,
silently ignored, with the dispose-time `wal_checkpoint(TRUNCATE)` then armed against a database with no
WAL).

No over-rejection: `Data Source=store.db`, `file:store.db?cache=shared`,
`file:a%20b?mode=memory&cache=shared` and `file::memory:?cache=shared` all carry `EmptyName = false`, and
`FILE:?mode=memory&cache=shared` stays accepted because an uppercase prefix is not a URI, so its whole
data source is the filename.

---

## argument-validation

### What validating after the rent cost

`RunAsync` rents first, so a guard that lived only in `DocumentOperations` ran *after* the wait for a free
connection. Measured: `GetAsync<Person>(null)` answers `ArgumentException` on an idle store,
`TaskCanceledException` under a cancelled token, and — with one leaked transaction at `MaxPoolSize = 1` —
`TimeoutException` after the full `PoolWaitTimeoutMs` (30 s by default) carrying a message that blames
undisposed transactions for a null id.

That was 43 of the 50 `RunAsync` sites, across 27 public methods; the other 7 take no validatable argument.

### Why the store path validates twice

Each guard is an `internal static Validate*` on `DocumentOperations`, called from **both** boundaries:
`DocumentStore` calls it inline ahead of `RunAsync`, and the `DocumentOperations` method still calls it
because `IDocumentTransaction` holds its own connection and never rents — hoisting a guard *out* of the
operations would silently lose it on the transaction path.

Double validation is free: every hoisted guard is a pure `O(1)` null/blank/range check, and because both
sites call one helper the message and `ParamName` cannot drift (pinned by a store-vs-transaction parity
assertion on the exception type, `ParamName` **and** `Message`).

An `Action validate` parameter on `RunAsync` was rejected: it would allocate a second closure per operation
on a path whose per-op allocation is budgeted, and it would run at the top of `RunAsync` anyway — strictly
more cost and less visibility than an inline call.

### Why argument validation precedes `ThrowIfDisposed()`

A bad argument is a caller bug that is true whatever state the store is in, and the alternative makes the
same buggy call report two different things depending on a race with disposal.

This was already what `OpenBlobReadAsync`, `ExecuteRawAsync`, `SerializeDocument`, `MigrateAsync` and
`RollbackToVersionAsync` did — `MigrateAsync`'s source comment states the reason — so the change made the
rest match rather than inventing a convention. `BeginTransactionAsync(TransactionMode)` was the one member
with the opposite order.

### The four deliberate limits

1. The `*Many` collection guards hoist only the `ArgumentNullException.ThrowIfNull(items|ids)`: the
   per-element loop runs after `.ToList()`, and repeating it ahead of the rent would enumerate the
   sequence twice and silently consume a one-shot enumerable.
2. Validation intrinsic to SQL generation (`ValidateJsonPath`/`ValidateIdentifier`/`ValidateColumnType`,
   `RequireDerivableName`) stays in `SqlGenerator` and still runs after the rent — hoisting it would
   duplicate SQL-shape knowledge outside the one boundary that owns it.
3. `AddVirtualColumnAsync`/`CreateIndexAsync`'s *expression* overloads hoist only the `ThrowIfNull` on the
   expression: `columnName` is screened by the string overload they delegate to, i.e. after the path is
   resolved, so hoisting it would reorder which exception a call that gets both wrong receives.
4. The two composite-index validators scan the whole array for a null or blank element **before** any
   element is parsed, where each element used to be checked and parsed before the loop advanced. For an
   array carrying *two different* faults the cheap structural check now reports first:
   `CreateCompositeIndexAsync<T>(["not-a-path", null])` answers `ArgumentNullException` naming `jsonPaths`
   rather than the unparseable path at index 0. Single-fault behaviour is unchanged.

Limit 4 is pinned by two `[Fact]`s in `ArgumentValidationTests`, because nothing else fails if the
interleaved order comes back.

### Cancellation

The token cancels the wait for a free pooled connection (`RunAsync` passes it to `_pool.RentAsync`, which is
why that private helper takes it *and* the caller's lambda captures it), and it reaches the ADO command. It
cannot interrupt a statement already running — Microsoft.Data.Sqlite does SQLite I/O synchronously — so a
cancelled token is observed *before* the command starts, never part-way through. On a transaction there is
no rent, so the command is the only cancellation point;
`CancellationTests.TransactionOperation_WithAnAlreadyCancelledToken_Throws` is the test that actually pins
the token reaching ADO.

The helpers in `Core/SqliteCommandExtensions.cs` take the token **before** their trailing
`params (string, object?)[]`. C# allows one params parameter and it must come last; dropping `params` so
the token could trail would force an explicit array at every call site, including the many that bind
nothing. The two synchronous helpers (`Execute`, `QueryFirstString`) stay tokenless: every path that uses
them is itself synchronous and has no caller token.

---

## querying

### Why bound values are normalized

ADO otherwise binds a shape that matches nothing *silently*. Each was measured against real SQLite, not
assumed:

| type | what ADO binds | what STJ stored |
|---|---|---|
| `DateTime`/`DateTimeOffset` | `"2024-03-01 00:00:00"` | `"2024-03-01T00:00:00"` |
| `byte[]` | a blob | base64 text |
| `decimal` | TEXT | the REAL `json_extract` yields |
| `float` | widened | round-tripped |
| `ulong` above `long.MaxValue` | wrapped negative | the unsigned value |

`DocumentQuery<T>.NormalizeBoundValue` is shared with the older `QueryAsync<T, TValue>` overload. This
assumes default serialization — a custom converter for one of those types breaks the alignment.

`ValidateValue` also rejects a non-finite `float`/`double`: STJ refuses to write NaN and infinity, so no
stored document can hold one; ADO rejected NaN at bind time anyway, and infinity would have stored
SQLite's `9e999`. That guard is in the shared helper, so a `DocumentQuery` comparison against NaN fails at
the call site too instead of matching nothing.

### `ExistsAsync` and the index

`GenerateFilteredExistsSql` wraps a `LIMIT 1` subquery in `SELECT EXISTS(...)`, so it stops at the first
matching row instead of counting them all, and it reuses the same `AppendWhere` pass as the count — which
is what keeps the interpolated path matching a `CreateIndexAsync` expression index (pinned by an
`EXPLAIN QUERY PLAN` assertion in `DocumentQueryIntegrationTests`).

One API-shape consequence: `ExistsAsync<T>` is overloaded on `string id` and `DocumentQuery<T>`, so a bare
`ExistsAsync<T>(null!)` no longer compiles — the call needs a cast to say which overload it means.

### Why the removed generators must not come back

`GenerateQueryWithWhereSql` / `GenerateSelectFieldsSql` / `GenerateSelectFieldsWithWhereSql` were dead
leftovers of the removed projection APIs and took raw SQL fragments — the only three generators that did
not validate their identifiers and paths. `ExecuteRawAsync` is the escape hatch for that.

---

## json-path-resolver

### Why the expression names the serialized key

Reading `Member.Name` instead was silently wrong in one direction that matters: under a naming policy or a
`[JsonPropertyName]`, `CreateIndexAsync<Customer>(x => x.Email, null, new IndexOptions { Unique = true })`
created a perfectly valid index over `$.Email`, which no row has — and SQLite counts each NULL in a unique
index as distinct, so the declared constraint accepted every duplicate. Nothing failed and nothing was
logged.

The resolver goes through `SerializerOptions.GetTypeInfo(type).Properties`, matching the CLR member on
`JsonPropertyInfo.AttributeProvider` and emitting `JsonPropertyInfo.Name` — the same metadata `JsonHelper`
serializes through, populated under both the reflection resolver and a source-generated context, so an
index is by construction created over the path the documents carry. A default-configured store sees
byte-identical SQL, since the member name *is* the serialized name there.

### Why `[JsonExtensionData]` is rejected

It is the second shape of the vacuous index, and a getter check does not catch it: the member has a getter
*and* a metadata name, but its entries serialize into the containing object — `{"Name":"n","k":"v"}`, never
`{"Extra":{"k":"v"}}` — so the member's own name appears in no document. The keys are not unreachable, they
are simply paths in their own right: `CreateIndexAsync<T>("$.nickname")`.

### Why the chain must bottom out at the lambda's parameter

`x => captured.Email` walks into the compiler-generated closure class, and while the resolver would reject
it anyway (a display class has no serialized `Email`), the message named `<>c__DisplayClass5_0`. `Convert`
nodes are unwrapped at every hop rather than only at the top, so an explicit `x => ((Base)x).Name` still
resolves.

### Divergence consequence

On a store whose names diverge, the *derived index name* changes with the path
(`idx_Customer_Email` → `idx_Customer_email_address`). An existing database keeps its old, vacuous index
and `DropIndexAsync<T>` no longer names it — dropping it is a one-line `ExecuteRawAsync`.

---

## concurrency

### Why `BuildConflictAsync` takes insert intent explicitly

`AlreadyExists` is only reachable from the write's insert branch. Inferring intent from
`expectedVersion == 0` mislabelled the delete-a-legacy-row-at-0 case: a versioned *delete* rejected by a row
at version 1 must report `VersionMismatch`, as the enum's own contract says.

### Why `ActualVersion` and `Kind` are post-conflict observations

The stored-version read is a separate statement from the guarded mutation, so outside a transaction another
connection can update, delete or recreate the row in between; the values are exact only when the operation
runs through an existing transaction, which holds the SQLite locks across both statements. Their XML docs
say so.

Deliberately not fixed by wrapping every guarded write in a transaction: that taxes the happy path to make
failure metadata temporally exact, and opening a transaction *after* the 0-row result buys nothing — the
race is already over. Nor can one statement do it: `UPDATE … RETURNING` yields no row on failure, and
`;`-chained statements are not atomic.

`ActualVersion` costs one `SELECT version` on the conflict path only — the happy path pays nothing.

---

## patching

### Why patch exists at all

The concurrency window, not convenience: a read-modify-write reserializes the whole document, so it
silently reverts a concurrent writer's edits to fields the caller never touched.
`PatchIntegrationTests.PatchAsync_LeavesAConcurrentWritersEditsToOtherFieldsIntact` pins both halves — the
patch keeps them, the equivalent upsert clobbers them.

### Why a patch path must reach below the root

`$` is grammatically valid — `ValidateJsonPath` accepts it, and the *reading* paths still do — but a patch
is the one caller for which it is destructive rather than merely blunt. Measured against real SQLite:

- `jsonb_set(data, '$', 5)` replaces the whole document with the scalar `5`, **reports success and bumps
  the version**, after which every read of that row throws `DocumentSerializationException` — silent
  destruction through an API whose entire purpose is to *avoid* clobbering a document.
- `jsonb_remove(data, '$')` yields SQL NULL and fails with a raw `SQLite Error 19: NOT NULL constraint
  failed` — the store's own `data BLOB NOT NULL` catching what the library should have. The one shape in
  the patch API that leaked a provider error instead of validating up front, and one a consumer-created
  table without that constraint would not catch at all.

The guard is the *bare* `$` only — `$[0]` addresses an element and stays legal, pinned by its own test. It
sits at both boundaries: the builder throws at the call site with `ParamName` `jsonPath`, the generator
re-checks with `operations`, so a hand-built `PatchOperation` cannot slip past. The two generator checks
are separate `ValidateJsonPath` calls (one per function loop) and are mutation-tested separately, as are
the two builder sites; widening the test to `Length <= 1` is an equivalent mutant, since the leading-`$`
check already guarantees a non-empty string.

### Why the caps are 499 and 999

The cap that binds is **`SQLITE_MAX_FUNCTION_ARG` (1000), not `SQLITE_MAX_VARIABLE_NUMBER`**: a set spends
two function arguments while binding one parameter, and a remove binds none at all, so
`MaxBoundParameters` (900) never sees a remove-only patch and 500 sets sail past it only to fail with
SQLite's `too many arguments on function jsonb_set`.

The two are independent — the nested `jsonb_set` is one argument to `jsonb_remove`. Integration tests
execute a patch at each cap against real SQLite, so the numbers are pinned to what it actually accepts
rather than to arithmetic.

### The three JSON-text carve-outs (`AsJson` on `PatchOperation`)

- `bool` — SQLite has no boolean and a bound `true` stores the number `1`.
- `decimal` and a `ulong` above `long.MaxValue` — both would round through a REAL and lose digits
  (`10.05m` → `10.050000000000001`).

### Path ordering

Within each function SQLite applies paths left to right, each seeing the document the previous ones left.
Only an exactly repeated path is rejected, so *related* paths compose in call order — removing
`$.Items[0]` before `$.Items[1]` shifts the array under the second path, and setting `$.A` before `$.A.B`
writes into the value the first set installed.

---

## corrupt-rows

### Why a null document is an error rather than a skipped row

A row that reads back as nothing used to be skipped, so `GetAllAsync`/`QueryAsync` returned fewer documents
than the table held, and `GetWithVersionAsync`/`GetAsync` returned null, indistinguishable from not-found.
That is why `GenerateGetAllSql`, `GenerateQueryByJsonPathSql` and `GenerateQuerySql` select `id, json(data)`
and read through `QueryStringPairsAsync`: the id has to travel with the document to be named.

### The two shapes, and why they are not interchangeable

`jsonb('null')` yields the 4-character text `null`; a **SQL NULL `data` column** yields SQL NULL.

`ExecuteScalar` maps *both* "no row" and "row whose column is NULL" to null, so testing the projected text
for emptiness reported an existing corrupt row as not-found while `ExistsAsync`/`CountAsync` and all three
multi-row readers reported it as present. Hence `GetAsync` reads through
`SqliteCommandExtensions.QueryFirstStringRowAsync` rather than `QueryFirstStringAsync`.
`GetWithVersionAsync` already had the information (`QueryFirstStringInt64Async` returns a tuple with
`Text == null`) and threw it away in a `Text: { Length: > 0 }` pattern.

Both now check emptiness *before* deserializing, because `JsonHelper` maps empty JSON to `default(T)` —
which for a value-type `T` is not null, so the `document is null` guard alone would hand back a fabricated
`default(T)` row. `QueryFirstStringAsync` is unchanged and still used for the schema and PRAGMA reads,
where no-row correctly means absent.

### Why `EnsureDocumentPayload` runs before deserializing

Two shapes were type-dependent without it:

- `JsonHelper` maps a null or empty projection to `default(T)`, which for a **value-type** `T` is a real
  value the `is not { }` guard downstream happily matches — so `GetAllAsync`/`QueryAsync`/`GetManyAsync`
  added a fabricated zero row instead of reporting the corrupt one. (The single-row reads already checked
  emptiness first; the collection readers did not.)
- The JSON literal `null` failed from the other side: a reference type deserialized it to null and was
  reported as corrupt, while a value type made STJ refuse the conversion — so one row surfaced as
  `DocumentSerializationException` for a struct and `CorruptDataException` for a class.

Neither shape is reachable through a store write — `SerializeDocument` rejects a null document — so only
raw SQL produces one. `DeserializeDocument`, the raw-SQL helper, deliberately keeps its own contract
(`default` for null/empty/`null` JSON, `DocumentSerializationException` on malformed): the guard belongs to
the read paths, not to `JsonHelper`.

### Why the exception hierarchy is split

A corrupt *row* carries `Id`, `TableName`, `TargetType` and `StoredTypeName`, so the row is identifiable
without parsing the message, while `DocumentSerializationException` keeps its original job — a JSON
serialization, deserialization or type-metadata failure, thrown only from `JsonHelper`. Well-formed JSON
merely incompatible with `T` is therefore still a `DocumentSerializationException`; nothing about that row
is corrupt.

`CorruptDataException` does **not** derive from `DocumentSerializationException` — catching one must not
catch the other — a pre-1.0 break taken on purpose. It also does not cover a `data` column holding bytes
that are not JSONB at all: SQLite fails inside the `json(data)` projection and the `SqliteException`
surfaces untranslated, since classifying provider error text is something this library does not do (pinned
in `ExceptionIntegrationTests`).

### Why the rename

`LiteDocumentStore.Exceptions.SerializationException` was ambiguous by simple name with
`System.Runtime.Serialization.SerializationException`, so any consumer importing both namespaces got CS0104
on a bare catch — reproduced against the real library, not assumed. `JsonSerializationException` was
rejected as the new name because it collides with Newtonsoft's, a far more common import. No `[Obsolete]`
shim was kept, so this is an intentional pre-1.0 source *and* binary break.
`ExceptionNameCollisionTests` uses both bare names in one file, so the collision fails the build if it ever
comes back.

---

## index-ddl

### Why `IndexFilter` is value-free

SQLite forbids bound parameters in a partial index, so a value-comparing filter would have to inline a SQL
literal — a new injection surface with its own escaping rules. `IS [NOT] NULL` already covers what partial
indexes are wanted for (unique-among-the-rows-that-have-one, unique-among-not-deleted). Richer filters stay
an `ExecuteRawAsync` job.

### Why the name pre-check compares definitions instead of skipping

This is `TableNameCollisionGuard`'s precedent applied to index names, and it exists because the derivation
is not injective and nothing else made a collision visible:

- `GenerateIndexName` folds `$.A.B` and `$.A_B` onto one name.
- `GenerateCompositeIndexName` folds `["$.A","$.B"]` and `["$.A.B"]` onto one.
- `AddVirtualColumnAsync`'s `idx_{table}_{columnName}` collides with `CreateIndexAsync`'s derived name for
  the same member.

That third one is the measured cost, because **an index on the generated column does not serve a query on
the raw expression**: with `idx_T_Email` over `[Email]`, `WHERE json_extract(data, '$.Email') = ?` plans
`SCAN T`, while the expression index that call was silently skipped would have given
`SEARCH T USING INDEX … (<expr>=?)`. So the pre-check turned a name collision into a permanent table scan,
reported by one Debug line.

### Why the textual comparison is exact

One generator produces both sides. SQLite does not store the executed statement byte-verbatim: it
reconstructs the `CREATE [UNIQUE] INDEX <name>` header canonically and **drops `IF NOT EXISTS`**, keeping
everything from `ON` onward exactly as written (measured, 3.53.3). So the three index generators take an
`ifNotExists` flag and the comparison form is generated with it false — no string surgery on the executed
text.

Nothing in that string is caller-shaped whitespace or casing: `ValidateIdentifier` restricts the table,
index, column and collation names, `ValidateJsonPath` restricts the paths, and every separator is a
literal, so two runs for the same arguments are byte-identical. An integration test round-trips the
generated text through real SQLite's `sqlite_master` so that assumption cannot rot silently.

### Why the comparison is made twice

The executed statement keeps `IF NOT EXISTS`, which is right for two callers racing to create the *same*
index — absorbing that is the point of the token — and wrong for a conflicting one, where it turns the
create into a silent no-op and hands the caller a success for someone else's definition. So the stored
definition is re-read *after* the create and compared again.

Dropping `IF NOT EXISTS` instead was rejected: it makes the identical race fail, which is the wrong trade,
and it reports the provider's `index … already exists` rather than the guard's message naming both
definitions. A name absent on the second read was created and dropped again in between; nothing claims it,
so there is nothing to refuse.

Both races are pinned deterministically in `IndexCreationRaceIntegrationTests`, through a custom
`IConnectionFactory` delegating to `DefaultConnectionFactory` that hands the store a `SqliteConnection`
subclass whose `CreateCommand` runs the interfering DDL on a second connection as the `CREATE` is built;
the intercepted ordinal is asserted rather than assumed.

### `AddVirtualColumnAsync`'s preflight ordering

The `ALTER TABLE ADD COLUMN` commits immediately outside an ambient transaction, so refusing the index
afterwards left the call half applied — the generated column added, the call failed, and nothing the caller
could undo by catching it.

The preflight sits ahead of the column-exists check rather than between it and the `ALTER`, so "refuse
before any work" holds by construction instead of by the short-circuit happening to skip the `ALTER`: the
call fails identically whether or not the column is already there, and in both cases with nothing written.
The post-create check still runs, because the preflight only closes the *pre-existing* state — another
connection can still claim the name in between, and closing that residual would mean wrapping the whole
operation in a transaction.

### What the pre-check does not close

`DropIndexAsync<T>(x => x.A.B)` still derives `idx_T_A_B` and drops whatever holds that name, so a
collision can still drop the wrong index. No check at a creation site can catch that; closing it needs an
injective scheme (a hash suffix, `idx_T_A_B_7f3c1a`) and is its own job.

### Why the projecting DDL rejects `$`

The split is by what the caller does with the extracted value, not by API: the *reading* paths keep `$` (a
`DocumentQuery` predicate, an ordering, an `IndexFilter` term each only compare or order by the whole
serialized document — blunt but legitimate), while `CreateIndexAsync`, `CreateCompositeIndexAsync` and
`AddVirtualColumnAsync` pass `allowRoot: false`.

Measured before the guard: `AddVirtualColumnAsync<T>("$", "vc")` created
`[vc] TEXT GENERATED ALWAYS AS (json_extract(data, '$'))` — a generated column duplicating every document
on read — and `createIndex: true` indexed it, while `CreateIndexAsync<T>("$", "name")` and the composite
form keyed an index on the document's serialized text.

The auto-named forms *did* throw, but for the wrong reason and with the wrong `ParamName`:
`RequireDerivableName` screens only `[`, so `$` reached `GenerateIndexName`, produced `idx_<table>_$`, and
`ValidateIdentifier` rejected **that** — reported against an `indexName` the caller never supplied.

`RequireDerivableName` is deliberately left alone: root validity belongs to path validation, and
bracket-vs-name injectivity is its own problem. Both boundaries carry the guard — the three
`DocumentOperations` call sites and the three generators — and each is killed by exactly one test; the
`AddVirtualColumnAsync` call site is not redundant, because an **existing** column short-circuits past the
generator entirely, so without it the same call is a silent no-op rather than a rejection. The expression
overloads need nothing: `JsonPathResolver` always appends at least one member, so a bare `$` is unreachable
there.

### Why `columnType` and `columnName` are hoisted with it

The same short-circuit hides the rest of `GenerateAddVirtualColumnSql`'s validation *over the caller's own
arguments*, not just the root check — `columnName` and `columnType` as well. Its fourth check, over
`tableName`, is deliberately left where it is; see below. Measured on current `main` against a real file
database (`DocumentStoreOptions.ForFile`), one store,
one table, verbatim:

| call | before | after |
| --- | --- | --- |
| `("$.Category", "cat1", false, "NOT_A_TYPE")`, column absent | `ArgumentException` `ParamName=columnType` — *"Unsupported column type 'NOT_A_TYPE'. Supported types are TEXT, INTEGER, REAL, BLOB and NUMERIC."* | unchanged |
| same call after a successful `(..., "cat2", false, "TEXT")` | **accepted, no throw** | `ArgumentException` `ParamName=columnType` |
| `("$.Category", "weird name", false, "TEXT")`, column absent | `ArgumentException` `ParamName=columnName` — *"Invalid SQL identifier 'weird name': only ASCII letters, digits and underscores are supported."* | unchanged |
| same, after `ALTER TABLE [t] ADD COLUMN "weird name" TEXT` | **accepted, no throw** | `ArgumentException` `ParamName=columnName` |
| the `"bad]name"` shape, both branches | identical to `weird name` | identical |
| `("$.Category", "weird name", true, ...)`, column present | `ArgumentException` `ParamName=`**`indexName`** — blaming the *derived* `idx_<table>_weird name` | `ParamName=columnName` |
| `("$.Category", "cat2", true, "NOT_A_TYPE")`, column present | **accepted** — the index preflight validates the name, never the type | `ArgumentException` `ParamName=columnType` |
| all of the above on `IDocumentTransaction` | identical | identical |

So the filed finding was real on `columnType` and the entry's claim that `columnName` is "effectively
covered" was **wrong on the branch that matters**: `SchemaIntrospector.ColumnExistsAsync` compares against
`pragma_table_info`, i.e. the table's real columns, so a column created by raw SQL under a name the
identifier rule rejects is reported present, and with `createIndex: false` nothing else looks at the name.
With `createIndex: true` the index preflight did catch it — but reported against `indexName`, a name the
caller never passed, and it catches nothing about `columnType`.

No injection surface either way: on the short-circuit branch neither value reaches SQL at all. What it cost
was idempotence — the same arguments threw on a fresh database and were a silent no-op on the next run,
which is exactly the property the root hoist was added to restore.

The fix is two lines beside the existing path hoist, in the generator's own order (`columnName`, then
`jsonPath`, then `columnType`) so the first fault reported is the same on both branches.
`SqlGenerator.ValidateColumnType` went `private` → `internal` and `ValidateIdentifier` the same, rather than
forking either rule: `IdentifierError` stays the one owner, and `IsValidIdentifier` (the non-throwing form,
for *derived* names) is unchanged. The generator keeps its own checks — both boundaries, as with `jsonPath`.
The hoist is **not** duplicated into `DocumentStore`, matching the path precedent: it is validation
intrinsic to SQL generation rather than a plain argument guard, and `DocumentStoreTransaction` calls
`DocumentOperations` directly, so putting it there covers both surfaces.

Reverting either line, rebuilt: dropping the `columnName` line fails
`AddVirtualColumnAsync_WithAnInvalidName_ThrowsWhetherOrNotTheColumnExists` (both `InlineData` shapes) and
its unit counterpart with *"Assert.Throws() Failure: No exception was thrown"*; dropping the `columnType`
line fails `AddVirtualColumnAsync_WithAnUnsupportedType_ThrowsWhetherOrNotTheColumnExists` and
`..._OnATransactionOverAnExistingColumn_StillThrows` the same way.

### Why `tableName` is *not* hoisted with them

`GenerateAddVirtualColumnSql` validates four things, in this order:

```csharp
ValidateIdentifier(tableName, nameof(tableName));
ValidateIdentifier(columnName, nameof(columnName));
ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false);
var validatedType = ValidateColumnType(columnType);
```

The hoist covers the last three. The first stays in the generator, so on the short-circuit branch a
non-identifier table name *is* still accepted or rejected by database state. That is deliberate, for three
measured reasons:

1. **It is a derived name, not an argument.** `AddVirtualColumnAsync<T>` takes a type; the string comes from
   `_tableNamingConvention.GetTableName<T>()`. Hoisting the check would raise an `ArgumentException` with
   `ParamName=tableName` against a parameter the caller never supplied — the same mis-attribution the
   `createIndex: true` row above shows for `indexName`, and the class **C29** addresses directly. Fixing it
   here would mean picking a `ParamName` before that decision is made.
2. **The shape needs two deliberate steps to reach.** A custom `ITableNamingConvention` has to return a
   non-identifier name *and* the table has to have been created by raw SQL under it: every other path
   through `SqlGenerator` refuses the name, `GenerateCreateTableSql` (`SqlGenerator.cs:76`) included, so a
   store-created table can never carry one.
3. **There is no injection surface.** The only statement the table name reaches on that branch is
   `SchemaIntrospector.GetColumnsAsync` (`Migrations/SchemaIntrospector.cs:95`), which quotes it itself —
   `"\"" + tableName.Replace("\"", "\"\"") + "\""` — before interpolating it into `PRAGMA table_xinfo(...)`.
   A `]`, a quote or a `;` in the name cannot break out of that identifier.

So what is left open is a non-idempotence in one doubly-opted-into configuration, not a correctness or a
safety hole — and closing it belongs with the `ParamName` question, not here.

Out of scope, deliberately: the `tableName` check above, the index-definition preflight, the
derived-index-name scheme (still non-injective, still its own job), and any widening of what
`ValidateColumnType` accepts — the five SQLite storage classes stay as they are.

---

## blobs

### Why the read stream is unpooled and the write is not

The asymmetry is the whole design: **a read has to hand a `Stream` out, a write only has to take one.** So
the write consumes its source inside the call and owns nothing afterwards — which is why it sits on
`IDocumentOperations` and stays atomic with document writes — and `OpenBlobReadAsync` is the one member in
the feature with a "dispose this" contract.

A pooled lease would mean a caller who forgets holds one permanently, and after `MaxPoolSize` such leaks
every other operation queues behind them. On its own connection the same mistake costs one handle, starves
nothing, and is reclaimed by the provider's finalizers.

Open streams are still **bounded** — a second `SemaphoreSlim` of `MaxPoolSize` slots (`BlobStreamSlot`,
released idempotently on dispose, on the not-found path and from the finalizer), so exhaustion is a
`TimeoutException` naming the cap rather than unbounded handles. That is a separate budget from the
operation slots precisely so the two cannot starve each other, which is why `MaxPoolSize`'s doc says a
store may hold up to twice that many connections.

### Why the read transaction exists

Incremental blob I/O addresses rows by rowid and SQLite reuses a deleted row's, so a bare `SELECT rowid` →
`new SqliteBlob(...)` could open a different row. It is deferred, so it takes no lock.

Its cost is inherent, not a consequence of the unpooled connection: while a stream lives it pins the WAL
against truncation, and outside WAL its read lock blocks writers.

`OpenBlobReadAsync` is deliberately **absent from `IDocumentTransaction`**: a stream outliving its
transaction would read through a connection already back in the pool, and adding it would have meant
tracking open streams and throwing on commit. Inside a transaction, blobs are read with `GetBlobAsync`.

### `length` means "consume exactly this many bytes"

Not "the source holds exactly this many". A seekable source is measured from its current position before
anything is written, so a wrong length in either direction throws `ArgumentException` naming `length` with
no I/O done.

A non-seekable one cannot be measured, and the copy deliberately **does not read past `length` to check** —
on a live network stream or pipe that read blocks until the peer sends or closes (indefinitely, and even
for a zero-length blob), and it would swallow a byte belonging to whatever follows in a framed stream. So
there, only a premature end is an error (`EndOfStreamException`); trailing bytes are left unread.

A plain `CopyToAsync` is wrong for a different reason: it would run off the end of a blob that cannot grow
and surface the provider's resize error instead of the caller's mistake.

`GenerateReserveBlobSql` pre-sizes the row with `zeroblob(@Len)` and returns its rowid — `RETURNING rowid`
fires on the `DO UPDATE` branch too (measured against 3.53.3), so an overwrite needs no second lookup. A
`zeroblob(0)` row opens as a `SqliteBlob` of length 0, so the empty blob needs no special case.

### The savepoint, and the cancellation window

Reserve and fill are two statements, so the pair is always atomic: outside an ambient transaction it takes
its own, and **inside a caller's transaction it takes a `SAVEPOINT`**. Without either, the reserve statement
has already replaced the payload with zero bytes by the time a copy fails, so a caller who catches the
exception and commits would persist a corrupt blob — rolling their whole transaction back is not the
store's call, and the savepoint is the only construct that undoes just this write.

The opening `SAVEPOINT` and the write take the caller's token. **Both the failure cleanup and the
successful `RELEASE` run on `CancellationToken.None`.**

The release is the non-obvious half: once every declared byte is written it is the point of no
cancellation, because the work is already in the caller's transaction and only the marker remains.
Measured before the fix, a cancellation landing in that window (**15 of 600** adversarially-timed attempts)
made the call throw while the payload stayed committable, and a caller who caught it and committed their
other work persisted a write they were told had failed. Narrow, but **deterministic once inside it**: all
15 of the 15 attempts that landed there both threw and persisted.

The window is microseconds wide: `CopyExactlyAsync` follows every `ReadAsync` with a `WriteAsync` on the
same token, so a cancellation **already observable when the read returns** is caught by that write and the
`catch` undoes the write correctly. Reaching the window needs a cancellation that **arrives after the final
write** — a timeout, a request-abort token, or a custom source stream that hands over its last bytes and
then cancels asynchronously.

**No other cancellable finalizer in the library has this shape**, and the reason is structural rather than
luck. The distinction is whether a cancelled finalizer can leave work that persists *without a further
deliberate act by the caller*:

- The internally owned transactions cannot: `RunBatchAsync`'s multi-chunk commit, the non-ambient blob
  write's own commit and `ExecuteInTransactionAsync` all dispose and roll back, so the failure they report
  is true.
- The blob-table upgrade, the rebuild and all six `MigrationRunner` commits are synchronous
  `transaction.Commit()` and take no token at all.
- `DocumentStoreTransaction.CommitAsync` is the interesting near-miss — it deliberately keeps the provider
  await outside the release path, so a cancelled commit leaves the transaction **alive** rather than
  discarding it, and a caller can retry `CommitAsync(CancellationToken.None)` and persist it. Still not
  this shape: nothing persists unless the caller commits again on purpose, so the reported failure was true
  when it was reported.

Only the savepoint leaves work that the caller's **already-intended** commit will persist while they have
been told it failed.

Removing the store-level transaction fails three integration tests; replacing the `ROLLBACK TO` fails a
fourth.

### Why `data` is the last column

SQLite reads a row front to back, so a column sitting behind a multi-megabyte payload can only be reached
by walking its overflow pages. Measured over twenty 20 MB rows, listing the metadata took **232 ms** per
pass with `data` second and **under a millisecond** with it last (14 ms vs 0.02 ms for one row).

There is deliberately **no length column**: `length(data)` is answered from the record header (`OP_Column`
carries `OPFLAG_LENGTHARG`; 50 reads over a 200 MB blob took 2 ms), so a stored one would only add state
that a consumer's raw-SQL insert could desynchronize.

### Why the id prefix is a key range

Measured: only the range *searches* the primary-key index while `LIKE`, `GLOB` and `substr` all scan it;
`LIKE` is ASCII-case-insensitive so prefix `u/1` matched a stored `U/1UPPER`; and a range needs no wildcard
escaping.

SQLite compares TEXT with `memcmp` over UTF-8, i.e. code-point order (`é` sorts after `z`), which is what
makes the incremented bound exact. A prefix of only `U+10FFFF` has no upper bound and the lower one alone
is used.

### The corrupt blob row

The store's own DDL declares `data BLOB NOT NULL`, so this is only reachable through raw SQL or on a table
a consumer created themselves — but the six read paths used to disagree completely, measured:

| path | before |
|---|---|
| `BlobExistsAsync` | `true` |
| `GetBlobAsync`, `BlobLengthAsync` | null — indistinguishable from absent |
| `GetBlobInfoAsync`, `ListBlobsAsync` | leaked `InvalidOperationException` from the reader |
| `OpenBlobReadAsync` | leaked `SqliteException` |

**One corrupt row made blob listing impossible** — the C02 shape.

Two things make this bigger than SQL NULL:

- `length()` **answers for a non-BLOB too** — 5 for `'hello'`, 2 for `42`, 3 for `1.5`, counting characters
  and digits — so `BlobLengthAsync`, `GetBlobInfoAsync` and `ListBlobsAsync` silently reported a plausible
  byte count that was not one.
- SQLite's incremental blob I/O **opens a TEXT value** and reads its UTF-8 bytes (only INTEGER and REAL make
  `SqliteBlob` refuse), so `OpenBlobReadAsync` returned a stream over the wrong thing.

Both are wrong answers rather than errors, which is why every blob read leads its projection with
`typeof(data)` and funnels through `EnsureBlobPayload` — including the two that never touch the payload.

`QueryFirstBlobRowAsync` reads ordinal 1 **only** when the class is `blob`, and that ordering is
load-bearing, not defensive: `GetFieldValue<byte[]>` silently succeeds on TEXT, INTEGER and REAL, so
validating after reading would swap a detectable failure for wrong bytes. (`ExecuteScalarAsync<byte[]>`
cannot serve the read at all — `ConvertScalar` reaches `Convert.ChangeType`, which has no `byte[]` target,
which was C26's raw `InvalidCastException`.)

An empty blob — `x''` or `zeroblob(0)` — is a BLOB of length 0 and stays legal on every path.

`ListBlobsAsync` fails the whole listing rather than skipping the row, matching `GetAllAsync` — fewer rows
than the table holds is undetectable data loss — so the workaround for a damaged row is an id prefix that
excludes it, or deleting it.

In `BlobReadStream.OpenAsync` the check sits before the `SqliteBlob` is constructed and throws through the
existing `catch`, so the read transaction, the unpooled connection and the blob-stream slot are all
released (pinned with `MaxPoolSize = 1`, where a leaked slot is a `TimeoutException` on the very next open).

### Upgrading an existing blob table

`CreateBlobTableAsync` adds the missing columns in place rather than shipping an `IMigration` a consumer
must register: it is a reserved, store-owned table, and the call that already creates it is where startup
code touches it. The mechanism is the `MigrationRunner` precedent — `pragma_table_info` check,
`ALTER TABLE ADD COLUMN` under `BEGIN IMMEDIATE` with the check repeated inside the lock.

`ALTER TABLE` only accepts a constant default, so the timestamps are **nullable** (an existing row has no
true creation time and back-filling `now()` would invent one) and `version` defaults to 1 so an existing
row is CAS-able immediately; the fresh table declares the same nullability so the two do not diverge.

What `ALTER TABLE` *cannot* do is reorder, so an upgraded table keeps `data` second — the slow layout —
which is why `RebuildBlobTableAsync` exists: `CREATE`/`INSERT SELECT`/`DROP`/`RENAME` in one transaction,
**200 MB in 823 ms**. It adds the metadata columns first, because a table that predates them ends in `data`
like a current one — the layout check alone reported it current, returned false, and left every metadata
read failing with `no such column: content_type`.

The concurrent-upgrade test pins that parallel upgrades converge, **not** the re-check under the lock: that
interleaving cannot be scheduled through the public API, and removing the re-check leaves the test passing
— it is kept for the cross-process case it does protect.

### Streamed CAS

On the streamed path the guard is the **reserve** statement, so a rejected write never replaces the payload
with a zeroblob (pinned by mutation: swapping the guarded reserve for the plain one fails exactly that
test).

---

## transactions

### Why the transaction object is the unit of work

Under the old shared-connection model, commands auto-enlisted in whatever transaction happened to be open
on that connection, so a concurrent request's writes silently joined another's transaction and were rolled
back with it. Two transactions now run on two connections and cannot see or roll back each other.

Every operation on the transaction object goes through `ActiveTransaction()` first, so a call made after
commit/rollback/disposal throws instead of running on a connection the pool has already handed to another
renter.

### `TransactionMode` — the one shape that matters

Read-then-write, which is the shape the concurrency API pushes, since `GetWithVersionAsync` +
`UpsertWithVersionAsync` are only temporally exact when run on a transaction.

A deferred transaction pins a read snapshot at its first read; if another connection commits before its
first write, the write-lock upgrade fails with `SQLITE_BUSY_SNAPSHOT` (extended code 517). `busy_timeout`
cannot retry that — the snapshot is already stale, so waiting can never help; the only recovery is roll
back and redo.

Measured against real SQLite (`TransactionModeIntegrationTests`): Microsoft.Data.Sqlite retries it anyway
at the ADO layer until its own command timeout elapses, so with the provider's 30 s default the caller
**stalled for 30 seconds** and then got the unretryable error. That is why `DefaultConnectionFactory` sets
the connection's command timeout from `BusyTimeoutMs`.

`Immediate` takes the write lock at `BEGIN`, before any snapshot exists, so that failure is unreachable;
contention becomes a plain `SQLITE_BUSY` wait at `BEGIN` which `busy_timeout` does retry. The cost is that
the write lock is held for the whole transaction including its reads, so concurrent writers serialize —
which is why it is opt-in and not the default. Past the wait the caller still sees `SQLITE_BUSY` and must
retry; the mode removes an unretryable failure, not the need for retry.

`Deferred = 0` is load-bearing: the tokenless overloads delegate with it, so renumbering the enum would
silently change what every existing caller gets. `MigrationRunner` already ran `BEGIN IMMEDIATE`
internally (`deferred: false`); the mode only exposes the same choice.

Tests need a **file** database — shared-cache in-memory takes table-level locks and fails overlapping write
transactions with `SQLITE_LOCKED` whatever the mode.

### Why no general `SQLITE_BUSY` retry policy

It would touch all ~20 operation paths, and re-running a caller's `ExecuteInTransactionAsync` lambda has
ugly semantics (partial effects, non-idempotent work). `busy_timeout` plus `Immediate` covers the realistic
cases; if it ever returns, the right shape is an opt-in retry on `ExecuteInTransactionAsync` alone with a
documented idempotency requirement.

### Batch chunking

An upsert binds 2N parameters, so one unbounded statement blew past `SQLITE_MAX_VARIABLE_NUMBER` (32766) at
~16383 items and approached `SQLITE_MAX_SQL_LENGTH`. `GenerateBulkUpsertSql`/`GenerateBulkDeleteSql` throw
above the cap, so a future caller cannot reintroduce the unbounded shape.

A multi-chunk batch is wrapped in a transaction so it stays all-or-nothing; a single-chunk batch is left
alone (one statement is already atomic). The `inAmbientTransaction` flag is explicit rather than probed off
`SqliteConnection`, which does not expose its pending transaction publicly.

`UpsertManyAsync` rejects duplicate ids because SQLite would otherwise fail the whole statement with the
opaque `ON CONFLICT DO UPDATE command does not affect row a second time`. `DeleteManyAsync` instead drops
repeats silently: an `id IN (...)` list is unambiguous and the deleted-row count is unaffected.

`GetManyAsync<T>` borrows the 500-item chunk size but runs its own loop rather than `RunBatchAsync`, which
sums affected-row counts a read never produces and opens a transaction a read does not need. The result is
an `IReadOnlyDictionary<string, T>` and not a list precisely so the caller can tell which ids were missing:
a missing id is an absent key, never a null value.

---

## raw-sql

### Why `connection.CreateCommand()` is required

It copies the connection's active transaction onto the command. A directly constructed
`new SqliteCommand(sql, connection)` leaves `Transaction` null and Microsoft.Data.Sqlite refuses to execute
it while a transaction is pending (pinned by
`Transaction_ExecuteRawAsync_EnlistsOnlyCommandsCreatedFromTheConnection`).

### Connection-local state leaks — C47, open

The dirty-session guard probes for a pending transaction and has no notion of session state, while the
factory applies the PRAGMAs once, when the connection is physically opened — so what a callback changes
persists on that connection until it is discarded or the store is disposed.

Three kinds were **measured** to leak: a session-scoped `PRAGMA`, an `ATTACH`ed database (still in
`pragma_database_list` on a later operation) and a `TEMP` table (still in `temp.sqlite_master`). They are
examples of the category, not its extent — the rest of the `TEMP` schema, `SqliteConnection.DefaultTimeout`,
functions/aggregates/collations registered on the connection (including overrides of built-ins) and loaded
extensions all live on the connection rather than in the file, so the same mechanism carries them; that
half is reasoned, not measured. The point of the last two is that the surface is connection-local state
generally rather than PRAGMAs specifically, which is why no re-apply-a-list fix can close it.

Measured at `MaxPoolSize = 1` with `EnableForeignKeys = true`: after a callback issued
`PRAGMA foreign_keys = OFF`, the next `ExecuteRawAsync`, an ordinary store write and a store transaction all
read `0`, an FK-violating insert on a later operation succeeded, and `DocumentStoreOptions.EnableForeignKeys`
still reported `true` — one physical connection, never re-configured. A larger pool does not remove the
leak, only makes it less deterministic: it reaches fewer operations, but which ones depends on who draws
that connection.

Re-applying or probing a list of PRAGMAs cannot close it, since `ATTACH` and `TEMP` are not PRAGMAs;
discarding the connection after external access would, at the per-operation cost this pool design exists to
avoid.

### Why the three synchronous helpers exist

Without them a consumer had to hardcode the table name beside the type and reimplement STJ with options the
store never exposed — `JsonHelper` and `SqliteCommandExtensions` stay `internal`. `GetTableName<T>()`
resolves through the store's *configured* convention, not a copy of the default rule, so a custom
convention is honoured.

The same reasoning keeps teardown (`DeleteAllAsync`, `DropTableAsync`, both `DropIndexAsync` overloads) on
`IDocumentOperations`: a test fixture or admin path should never hand-write a `DROP`.

---

## migrations

### Why the two documented reads must not write

Both used to write twice on **every call**: `CREATE TABLE IF NOT EXISTS` unconditionally, plus the
`ALTER TABLE … ADD COLUMN checksum` under `BEGIN IMMEDIATE` on a legacy table. The per-runner
`_schemaEnsured` flag never suppressed it, because `DocumentStore.RunMigrationAsync` builds a new
`MigrationRunner` per call.

Measured: two reads on a store that never migrates left `__store_migrations` created, and eight concurrent
`GetCurrentMigrationVersionAsync` calls against a legacy table took **53.85 ms** per burst against
**0.89 ms** on a current one — a 60x cost from a DDL write lock the caller never asked for. Now **0.75 ms
vs 0.81 ms** (indistinguishable). The probe costs one `SELECT COUNT(*) FROM sqlite_master` on both reads
and one `pragma_table_info` on the applied-migrations read, replacing two writes.

`ReadHistorySchemaAsync` (table **and** column) serves the applied-migrations read;
`HistoryTableExistsAsync` (presence alone) serves the version read, which projects no checksum at all.

A knock-on the library does **not** turn into a supported configuration: a `Mode=ReadOnly` store with no
history table used to fail both reads with `SqliteException` 8 (`attempt to write a readonly database`) and
now answers `0`/`[]`. That is a side effect of the reads becoming reads, not a statement that read-only
stores are supported — the dispose-time `PRAGMA wal_checkpoint(TRUNCATE)` and other paths are still
unguarded writes, and `Mode=ReadOnly` remains undocumented and unhandled in `src/`.

### Why "already applied" is membership, not `Version <= MAX(applied)`

The old comparison silently skipped a back-filled migration (branch merge, two devs picking versions) and
returned the same `false` as an already-applied one, so the caller could not tell.

### Why the membership check is inside `BEGIN IMMEDIATE`

Microsoft.Data.Sqlite's default `BeginTransaction()` was already immediate (verified against 10.0.11: a
second connection's write hits `SQLITE_BUSY` while the transaction is open), so the fix is the **ordering**,
not the mode — the old code read the version *before* taking any lock, so two processes starting together
both ran `UpAsync` and the loser failed on the primary key. Now the loser blocks on the write lock (bounded
by `BusyTimeoutMs`), re-reads history and reports `false`.

### What a migration author is told, and why

The runner's transaction is already open when `UpAsync`/`DownAsync` receives the connection, and the runner
commits it, so the callback's SQL and the history row move as one unit.

The four restrictions are shared by both methods; their *consequences* are not, because the up path is
atomic with an `INSERT` of that row and the down path with a `DELETE` of it. An `UpAsync` that throws while
the runner's transaction is intact leaves nothing behind; a `DownAsync` that does leaves the migration
**still applied**.

- **Do not open, commit or roll back a transaction.** `connection.BeginTransaction()` throws
  `SqliteConnection does not support nested transactions`, which is loud. A bare `COMMIT` is not — it ends
  the runner's transaction, which makes the migration's work and its history row separately durable rather
  than one unit, and what happens after that is no longer the runner's to control. Measured on one such
  run: the call threw `cannot rollback - no transaction is active` while version 1 was already recorded and
  the table present, so a retry returned 0. **No particular end state is guaranteed**, which is exactly why
  it is the nastiest shape here — the exception the caller is handed does not tell them which one they got.
- **Build commands with `connection.CreateCommand()`** — a directly constructed `SqliteCommand` fails with
  `Execute requires the command to have a transaction object…`.
- **Do not call back into the store.** This is the run's only lease. At `MaxPoolSize = 1` such a call waits
  for a connection the run itself is holding — it fails once `PoolWaitTimeoutMs` elapses with a
  `TimeoutException` whose message blames undisposed transactions and so misdiagnoses this case, or hangs
  when that option is `Timeout.Infinite`. If it obtains another lease, it runs on a *different* pooled
  connection, so it is not enlisted in the migration's transaction and is not covered by its atomicity. It
  may also block or fail on SQLite's locks — a write contends with the write lock the transaction holds and
  can surface `SQLITE_BUSY` (`database is locked`), measured at ~3.6 s with `BusyTimeoutMs = 1500` at the
  default pool size. What the outcome is beyond that depends on journal mode, cache mode and the locks held
  at the time, which is SQLite's business and not a contract this library states.

### Non-transactional statements, and why no opt-out was added

`VACUUM` and `PRAGMA foreign_keys` are the two that matter, and the asymmetry is why this is documented
rather than left to SQLite: `VACUUM` fails loudly (`cannot VACUUM from within a transaction`), while
`PRAGMA foreign_keys = OFF` is **silently ignored** — the setting keeps whatever value the connection
already had, so on the default `EnableForeignKeys = true` it reads back `1` inside the same migration and
foreign keys stay enforced (a store configured `false` reads back `0` and is equally unaffected). So the
12-step table rebuild, the canonical reason to want the PRAGMA, is not running under the assumption it was
written against.

An opt-out — a defaulted `IMigration` member or a `MigrationOptions` flag that runs `UpAsync` outside the
transaction — was considered and **rejected**: it would decouple the migration's own work from the
history-row `INSERT`, and "half-applied" there is precise and unrecoverable. The schema change would be
durable but **unrecorded** if the process died or the insert failed between them, so the next run sees the
version absent from history and runs `UpAsync` again against a database that already has the change —
`CREATE TABLE` without `IF NOT EXISTS`, an `ALTER TABLE ADD COLUMN`, any data backfill, all fail or
double-apply; and a multi-statement `UpAsync` that failed part-way would have nothing rolling it back at
all. The concurrent-start guarantee weakens too, since the window between the membership check and the
history write would then hold arbitrary caller SQL.

`MigrationConnectionContractIntegrationTests` pins the contract the docs promise — the nested-transaction
throw, the silently-ignored PRAGMA, the bare-`COMMIT` outcome, and that the lease is held for the whole run
rather than per migration.

### Why the checksum covers the up SQL only

The down SQL is not part of what was applied, so editing it must not fail a startup migration; rollback
never verifies checksums at all. Either side null skips the check, which is what keeps pre-checksum history
usable.

### Why `Version` must be positive

`Migration` has refused `version <= 0` since it was written; a hand-written `IMigration` skipped that.
Measured, version 0 **applied** (`MigrateAsync` returned 1) while `GetCurrentMigrationVersionAsync` then
answered `0` — the documented "nothing applied" sentinel — and `RollbackToVersionAsync(0)` returned 0
without rolling it back, with `-1` refused by the target's own `ThrowIfNegative`. So it was applied,
unreportable and unrollbackable through the public API. It was not *re-*applied, because the check is
membership rather than `Version <= MAX(applied)`, so the damage was confined to reporting and rollback.

A negative version was worse in a different way: on an **empty** database `MigrateAsync` threw
`MigrationOutOfOrderException` comparing against that same 0 sentinel, and under `AllowOutOfOrder` it
applied and made `GetCurrentMigrationVersionAsync` answer `-1`.

The floor is one `RequirePositiveVersion` helper called from three sites — `Validate`'s per-element loop,
which covers `MigrateAsync` and `RollbackToVersionAsync`, plus the two internal single-migration entry
points `ApplyMigrationAsync`/`RollbackMigrationAsync`, which do not go through `Validate` — reporting
`ArgumentException` against `migrations` or `migration` respectively.

**Behaviour break:** a consumer whose hand-written `IMigration` sits at 0 or below had it applied and now
gets an `ArgumentException`; the version is unreportable and unrollbackable either way, so the throw is the
only outcome that says so.

### Why migrations are not on `IDocumentOperations`

So a migration can never run on a caller's `IDocumentTransaction`: it owns its own transaction. There is no
DI registration and no migrate-on-startup hosted service — the consumer calls `store.MigrateAsync(...)`,
which needs no new package reference.

Two source breaks were accepted: `MigrationRunner` is no longer public, and the five members on
`IDocumentStore` break an external implementation of that interface.
