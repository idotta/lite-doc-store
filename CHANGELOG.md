# Changelog

All notable changes to LiteDocumentStore are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) — pre-1.0, so a minor version may break.

**This file starts at 0.5.0.** Releases 0.1.0 through 0.4.0 shipped before it existed; that history
is not lost, it is in git — `git log v0.3.0..v0.4.0` for one release, `git tag` for the list.

## [0.5.0] - 2026-09-22

0.5.0 is a large pre-1.0 release: 73 commits since 0.4.0, and the breaking half is wider than the
commit log's `!` markers suggest. Read **Breaking changes** before upgrading — several of the
breaks are silent, changing what SQL the library emits rather than failing a build.

### Breaking changes

#### Names an existing database already holds

- **Table names are now namespace-qualified.** `DefaultTableNamingConvention` folds the type's
  namespace parts, then its declaring-type chain, then its simple name, joined with `_`, and a
  constructed generic appends its arity then each argument by the same rule — so `MyApp.Sales.Order`
  is now `MyApp_Sales_Order` where it used to be `Order`, and a global-namespace type alone is
  unchanged. *Remedy:* rename the tables in your database, or keep the old names by plugging in the
  five-line `type.Name` convention the README's "Table names" bullet gives, through
  `DocumentStoreOptions.TableNamingConvention`. Never hardcode a name — ask `store.GetTableName<T>()`.
  Raw SQL inside your own `IMigration` implementations is affected too, as are auto-derived index
  names, which embed the table name. (#74)
- **A generic document type now gets a table instead of failing.** `Box<int>` resolves to
  `MyApp_Box_1_System_Int32`; before, the convention handed back `Box` + backtick + `1` and the
  identifier validator refused it. Types the fold still cannot name — open generic definitions,
  generic parameters, arrays, pointers, by-ref types, types nested in a *generic* type, and
  non-ASCII names — now throw `NotSupportedException` naming the type, instead of an
  `ArgumentException` blaming a `tableName` parameter no caller passed. (#74)
- **Every auto-derived index name changes**, because it now carries a six-hex-character digest:
  `idx_Customer_Email` is now `idx_Customer_Email_3cf60a`. Both consequences are silent — a
  `DropIndexAsync<T>(x => x.Email)` that drops nothing, and a `CreateIndexAsync<T>(x => x.Email)`
  that leaves an upgraded database carrying two indexes over one path. *Remedy:* list the old names
  once and drop each explicitly before re-creating anything; the README's "Index names" bullet has
  the `sqlite_master` query and the full note.
- **`$."Name"` is now the *quoted member* `Name`, and renders canonically as `$.Name`.** Quoted path
  segments ship in this release, so a `"` straight after a `.` opens a quoted label instead of being
  an ordinary member character. The key is the same; the **SQL text is not**, and SQLite matches an
  expression index only when the indexed expression appears *literally*. An expression index created
  through the old `$."Name"` spelling therefore **silently stops being matched** — no error
  anywhere, every query over that path falling back to a table scan. *Remedy:* if you ever passed a
  path containing a `"`, drop and re-create those indexes; paths written the ordinary way (`$.Name`)
  are byte-for-byte unchanged and need nothing. (#114)
- **Two path spellings are now refused.** A path whose quotes do not close (`$."lead`) throws
  `ArgumentException`, as does anything after the closing quote (`$."a""b"`, which SQLite resolved
  as the wrong key anyway). And a `"` inside a member that *needs* the quotes is refused on **every**
  engine: SQLite 3.45.x, the floor the version guard enforces, does not unescape a quoted label at
  all, so such an index would be a valid index over a path no row has — vacuous and silent. *Remedy:*
  reach that key through `ExecuteRawAsync` with a bound path. (#114)
- **A path with no unquoted spelling has no derivable index name.** `$."a.b"`, like the already-refused
  `$.full-name` and `$.Tags[0]`, needs an explicit `indexName`; the digest separates names, it does
  not make the readable half an identifier. (#114)

#### API that is gone or renamed

- **`IDocumentStore.Connection` is removed.** A pooled connection cannot be handed out to live
  indefinitely. *Remedy:* `ExecuteRawAsync(Func<SqliteConnection, CancellationToken, Task<T>>)`, on
  the store and on a transaction; build commands with `connection.CreateCommand()`. The connection is
  valid only inside the callback, and is retired afterwards, so a callback may leave session state
  changed. (#33, #46)
- **The transaction API is replaced.** `ExecuteInTransactionAsync(Func<IDbTransaction, Task>)` and
  `ExecuteInTransactionAsync(Func<Task>)` are gone; a transaction is now an `IDocumentTransaction`,
  obtained from `BeginTransactionAsync()` or handed to
  `ExecuteInTransactionAsync(Func<IDocumentTransaction, Task>)`, and it holds one rented connection
  for its lifetime. *Remedy:* invoke operations **on the transaction object** — an operation invoked
  on the store rents its own connection and commits independently, which under the old shared
  connection was how a concurrent request's writes silently joined another's transaction and were
  rolled back with it. The store's members also split across `IDocumentOperations` (shared by the
  store and a transaction) and `IDocumentStore`. (#33)
- **`ConfigureConnection` / `ConfigureConnectionAsync` are gone from `IConnectionFactory`.** The pool
  only ever calls `CreateConnection(Async)`, so an implementer who put PRAGMA work behind the
  `Configure*` declarations handed back silently unconfigured connections. *Remedy:* do the
  configuration before returning from `CreateConnection(Async)`, or hold a `DefaultConnectionFactory`
  and delegate to it — the two members are still public on that class, which is where a decorator
  calls them. (#106)
- **`SerializationException` is now `DocumentSerializationException`.** The old bare name was
  ambiguous with `System.Runtime.Serialization.SerializationException`, giving any consumer who
  imported both namespaces a `CS0104` on a bare `catch`. No `[Obsolete]` shim — keeping the old name
  keeps the collision. This is a binary break as well as a source break. *Remedy:* rename the type
  in every `catch` and `throw`; nothing else about it changed. (#66)
- **`ColumnInfo.IsPrimaryKey` is removed.** It was mapped from `PRAGMA table_xinfo`'s `pk` with
  `== 1`, but that column is the 1-based position within the primary key, so every composite key
  reported its later columns as not part of the key. *Remedy:* read `PRAGMA table_xinfo` through
  `ExecuteRawAsync`, which gives you the ordinal the boolean could never carry. (#76)
- **The DI registration is Singleton only** — the `ServiceLifetime` parameter is gone from
  `AddLiteDocumentStore`. A thread-safe store with its own pool has nothing for a scoped registration
  to isolate. *Remedy:* drop the argument; the registration you get is the one the parameter's
  `Singleton` value asked for. (#33)
- **`IDocumentStore` gained five migration declarations**, so an external implementation of the
  interface no longer compiles: `MigrateAsync` (two overloads), `GetAppliedMigrationsAsync`,
  `GetCurrentMigrationVersionAsync` and `RollbackToVersionAsync`. *Remedy:* implement the five, or
  hold a real `IDocumentStore` and forward to it — the interface is not designed to be reimplemented
  wholesale. (#54)
- **Every async member takes a trailing `CancellationToken cancellationToken = default`.** Source
  compatible for callers; a binary break for already-compiled ones, and a source break for anyone
  implementing `IDocumentOperations` or `IDocumentStore`. (#34)
- **`MigrationRunner` is now `internal`.** At 0.4.0 it was the *only* migration entry point —
  `IDocumentStore` carried none of the five declarations — so every consumer who ran a migration
  constructed one directly, and this is a source and binary break for all of them. *Remedy:* call
  the five members on `IDocumentStore` instead; each rents a pooled connection and builds the runner
  for you. (#54)
- **`Migration` is now `SqlMigration`.** The bare name collided with widely imported ones, the same
  reasoning that renamed `BlobInfo`. No `[Obsolete]` shim, and a binary break as well as a source
  break. *Remedy:* rename `new Migration(...)` to `new SqlMigration(...)` and any `: Migration` base
  to `: SqlMigration`. (#107)
- **`MigrationHistoryRecord`'s three properties are `init`-only**, where they used to have public
  setters a consumer could legally call. The records `GetAppliedMigrationsAsync` hands back are
  freshly built snapshots, so a write to one never persisted and never could. *Remedy:* stop writing
  to them; to change history, run a migration. (#105)

#### Calls that will not compile

New options types arrive as **overloads** rather than inserted parameters, which is what kept every
caller passing a trailing `CancellationToken` positionally working. The cost is that a bare `default`
at those call sites is ambiguous and needs a cast — worth knowing while you adopt them:

- `PutBlobAsync(id, data, default)` — `BlobWriteOptions` vs `CancellationToken`. Write
  `PutBlobAsync(id, data, default(CancellationToken))`.
- `BeginTransactionAsync(default)` and `ExecuteInTransactionAsync(action, default)` — `TransactionMode`
  vs `CancellationToken`.
- `MigrateAsync(migrations, default)` — `MigrationOptions` vs `CancellationToken`.
- `CreateIndexAsync<T>(x => x.Email, "name", default)` and the same call on
  `CreateCompositeIndexAsync` — `IndexOptions` vs `CancellationToken`.
- `ExistsAsync<T>(null!)` — the `string id` and `DocumentQuery<T>` overloads both accept it. Pass a
  typed null, or (better) a real id.

#### Behaviour that now refuses instead of going quiet

- **A transaction answers nothing once it has ended, including its connectionless members.**
  `GetTableName<T>()`, `SerializeDocument<T>` and `DeserializeDocument<T>` throw
  `InvalidOperationException` after commit or rollback and `ObjectDisposedException` after disposal,
  like every other member — one object, one lifetime, one answer. *Remedy:* call them on the
  **store**, which is unaffected, or move the call inside the `await using` block.
  `SerializeDocument<T>(null)` still reports `ArgumentNullException` first, since a null document is
  a caller bug whatever state the transaction is in. (#113)
- **`GetTableName<T>()` throws for a convention-produced name that is not a SQL identifier.** Every
  operation passes the collision guard, which now screens the configured convention's output and
  throws `InvalidOperationException` naming the convention type and the name it returned. Only a
  custom convention can reach this. *Remedy:* return `[A-Za-z_][A-Za-z0-9_]*` from your convention;
  bracket-quoting the result yourself no longer works. The same guard also refuses two types that
  resolve to one table name, ASCII case included. (#74)
- **Re-creating an index under a name that already holds a *different* definition throws
  `InvalidOperationException`** naming the index and both definitions, where it used to be a silent
  no-op — the worst case of which downgraded an expression index to a permanent table scan. An
  identical re-create is still idempotent. *Remedy:* `DropIndexAsync` first when you mean to change
  an index's `IndexOptions`. (#92)
- **`CreateIndexAsync<T>(x => x.FullName)` under a naming policy throws instead of silently creating
  a vacuous index.** The derivation used to read the CLR member, so under
  `JsonNamingPolicy.KebabCaseLower` it produced an index over `$.FullName` — a path no row has —
  under the perfectly valid name `idx_T_FullName`. The path now resolves to `$.full-name`, whose
  readable half is no SQL identifier, so the auto-derived name is refused against the path;
  `DropIndexAsync<T>(x => x.FullName)` is refused against the expression for the same reason.
  *Remedy:* pass an explicit `indexName`, and drop through the string `DropIndexAsync(indexName)`
  overload. (#63, #94)
- **`IMigration.Version` must be positive, enforced at every runner entry point** and not just in
  `SqlMigration`'s constructor, so a hand-written version-0 migration now throws `ArgumentException`
  where it used to apply — while `GetCurrentMigrationVersionAsync` answered `0`, the "nothing
  applied" sentinel, and `RollbackToVersionAsync(0)` refused to roll it back: applied, unreportable
  and unrollbackable. The only consumer this changes is one depending on that outcome. *Remedy:*
  number migrations from 1. (#54, #97)
- **Four PRAGMA options were applied and silently ignored, and now are honoured or refused.**
  (#55, and #56 for the `BusyTimeoutMs` derivation below)
  - `PageSize` is set **before** `journal_mode`, since SQLite refuses to change the page size of a
    database already in WAL mode — the old order made the option a no-op even on a brand-new file.
    On an *existing* database the PRAGMA is ignored whatever the order, so the value is read back on
    every physical connection and a mismatch throws `IncompatiblePageSizeException` carrying both
    sizes. *Remedy:* `PageSize = 0` keeps whatever the database has, with no statement and no check;
    converting an existing database needs a `VACUUM` outside WAL mode.
  - `EnableForeignKeys = false` did nothing, because the provider opens connections with
    `foreign_keys` already ON. Both states are now stated explicitly.
  - **WAL mode on an in-memory database is refused** with `ArgumentException` during options
    validation. `PRAGMA journal_mode = WAL` answers `memory` there — not an error, not honoured — and
    it armed the dispose-time checkpoint against a database with no WAL. *Remedy:* leave
    `EnableWalMode` false for in-memory stores.
  - `BusyTimeoutMs` was a floor, not a bound: `PRAGMA busy_timeout` bounds SQLite's handler within
    one attempt, and the provider then re-ran the whole attempt. `connection.DefaultTimeout` is now
    derived from it (seconds, rounded up, floored at 1) unless the connection string states
    `Default Timeout` or `Command Timeout`, which wins. It still is not a bound on the total — it
    decides only whether a *further* attempt may begin.
- **A private in-memory database is rejected** with `ArgumentException` during options validation:
  `Data Source=:memory:`, `Mode=Memory` without `Cache=Shared`, an empty URI filename, a
  `cache=private` that wins by being last, a `cache=shared` sitting behind a `#`. Each pooled
  connection would otherwise get its own empty database. An **empty data source** is rejected for the
  same reason. *Remedy:* `DocumentStoreOptions.ForInMemory()` or `ForSharedInMemory(name)`, which
  return a uniquely named shared-cache memory database. (#75, #91)
- **Serializer options the store cannot use are rejected**, at store construction and at the factory:
  a supplied `SerializerOptions` must carry a `TypeInfoResolver`, and a **null** `SerializerOptions`
  is refused when `RuntimeFeature.IsDynamicCodeSupported` is false. *Remedy:* under Native AOT, pass
  `new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }`. (#84)
- **A row that reads back as nothing is corrupt, not absent.** A `data` column holding SQL NULL or
  `jsonb('null')` now raises `CorruptDataException` (carrying the id, the table, the target type and
  the stored type name) from every document read, where it used to surface as not-found, as a
  fabricated zero value for a struct `T`, or as a `DocumentSerializationException` for a class `T`. A
  genuinely absent id is still absent. `CorruptDataException` deliberately does **not** derive from
  `DocumentSerializationException`, so catching one does not catch the other. *Remedy:* catch
  `CorruptDataException` where you used to catch the serialization one; delete or overwrite the row
  to recover, both of which still work. (#64)
- **A blob row whose `data` is not a BLOB is corrupt too**, on all five reads that depend on the
  payload — including the three that only *measure* it, since `length()` answers for TEXT and
  incremental blob I/O happily opens one. `BlobExistsAsync` still reports `true`. (#65)
- **The document root `$` is refused where it would destroy or project a whole row.** `DocumentPatch`
  and the patch generator refuse it (`jsonb_set(data, '$', 5)` replaced the document with a scalar
  and *reported success*), as do `CreateIndexAsync`, `CreateCompositeIndexAsync` and
  `AddVirtualColumnAsync`. Reading paths — predicates, orderings, index filters — still accept it,
  and `$[0]` and `$.""` are keys below the root and stay legal everywhere. (#71, #73)
- **`DocumentStoreOptionsBuilder.WithCacheSizeMb` rejects its argument outside `1..2_097_152`** with
  `ArgumentOutOfRangeException`, instead of storing an overflowed or sign-flipped product that
  `PRAGMA cache_size` reads as a page count. *Remedy:* `WithCacheSize(int)` stores what it is given.
  (#112)
- **A virtual column's generator checks run even when the column already exists.** The identical call
  used to throw `ArgumentException` on a fresh database and be a silent no-op on the second run.
  (#99)

### Added

- **Blobs grew up.** On top of the existing `CreateBlobTableAsync`/`PutBlobAsync`/`GetBlobAsync`/
  `DeleteBlobAsync`/`BlobExistsAsync`: `BlobLengthAsync`, a size bound
  (`BlobLimits.MaxBlobLength`, 1,000,000,000) on every write, and blob operations on
  `IDocumentOperations`, so a document and its blob commit atomically inside one transaction. (#57)
- **Blob metadata, listing and versioning.** `GetBlobMetadataAsync` returns
  `BlobMetadata(Id, Length, ContentType, CreatedAt, UpdatedAt, Version)` without reading the payload,
  and `ListBlobsAsync(idPrefix, skip, take)` returns them in id order over a half-open key range that
  searches the primary-key index. (The type is `BlobMetadata`, not `BlobInfo`, so its bare name does
  not collide with a widely imported one — #107.) Content type arrives through `BlobWriteOptions`;
  timestamps are stamped by SQLite itself, so every writer against one file uses one clock. The
  payload column is last, which is what makes metadata cheap to read — measured at 232 ms against
  under 1 ms for a listing pass over twenty 20 MB rows. `RebuildBlobTableAsync()` converts a table
  upgraded in place to that layout. (#58)
- **Blob streaming.** `IDocumentStore.OpenBlobReadAsync(id)` returns a seekable read-only `Stream?`
  over SQLite's incremental blob I/O, and `PutBlobAsync(id, Stream source, long length)` writes one
  without materializing it. Open streams are bounded by their own budget of `MaxPoolSize` slots,
  separate from the operation slots. (#57)
- **`DocumentQuery<T>`** — an immutable, AOT-safe filter builder consumed by `QueryAsync<T>`,
  `CountAsync<T>` and `ExistsAsync<T>`, with `Equal`, `NotEqual`, `GreaterThan(OrEqual)`,
  `LessThan(OrEqual)`, `Like`, `Glob`, `In`, `IsNull`, `IsNotNull` and `ArrayContains`, plus ordering
  and paging. Predicates combine with AND only. Bound values are normalized to what the serializer
  actually wrote, so a `DateTime`, `byte[]`, `decimal`, `float` or wide `ulong` matches instead of
  silently matching nothing. (#46, #51)
- **`DocumentPatch<T>` and `PatchAsync` / `PatchWithVersionAsync`** — change named fields in one
  statement, one round trip and one version bump, which is what closes the window a
  read-modify-write leaves open for a concurrent writer's edits to fields you never touched. (#52)
- **The optimistic-concurrency holes are closed.** `DeleteWithVersionAsync<T>` joins the existing
  `UpsertWithVersionAsync` (a plain `DeleteAsync` ignores the version, so a read-modify-delete could
  silently drop a concurrent update), `ConcurrencyException` now carries `DocumentId`, `TableName`,
  `ExpectedVersion`, `ActualVersion` and a `ConcurrencyConflictKind`, a `version = 0` write against a
  taken id lifts a legacy row instead of leaving it un-CAS-able forever, and both write paths end in
  `RETURNING version` so the value handed back is what SQLite stored. `PutBlobWithVersionAsync` and
  `DeleteBlobWithVersionAsync` bring blobs inside the same model. (#50, #58)
- **Index DDL options.** `IndexOptions` carries `Unique`, `Collation`, `Descending` and `Filter`;
  `IndexFilter` is the deliberately value-free partial-index `WHERE`. Default options emit exactly
  the statement earlier versions emitted. The DDL also gained string-path overloads, for a key
  written by something other than `T`'s serializer or an array element no property access can name.
  (#53)
- **Migrations that are reachable and race-safe.** `MigrateAsync`, `GetAppliedMigrationsAsync`,
  `GetCurrentMigrationVersionAsync` and `RollbackToVersionAsync` on `IDocumentStore`, with
  "already applied" meaning membership in the history table, each apply in its own `BEGIN IMMEDIATE`
  with the check inside the lock, `MigrationOutOfOrderException` for a back-filled version (unless
  `MigrationOptions.AllowOutOfOrder`), and SHA-256 checksums verified against history
  (`MigrationChecksumMismatchException`, `MigrationOptions.VerifyChecksums`).
  `SqlMigration.Checksum` is `virtual` so a subclass that changes what `UpAsync` runs can cover it.
  The three migration types that did change shape are under **Breaking changes**, as is the
  positive-`IMigration.Version` rule. (#54, #97, #104, #105, #107)
- **Batch and bulk operations.** `GetManyAsync<T>` (returning a dictionary, so a missing id is an
  absent key rather than a null value), `DeleteAllAsync<T>`, `DropTableAsync<T>` and both
  `DropIndexAsync` overloads; batches are chunked at 500 items per statement and wrapped in a
  transaction when they span more than one chunk, and `UpsertManyAsync` rejects duplicate ids naming
  the id and both indices. (#48, #49)
- **`TransactionMode.Immediate`**, through an overload of `BeginTransactionAsync` /
  `ExecuteInTransactionAsync`. A deferred transaction that reads then writes can fail with
  `SQLITE_BUSY_SNAPSHOT`, which `busy_timeout` cannot retry; `Immediate` takes the write lock at
  `BEGIN`, making that failure unreachable at the cost of serializing writers. (#56)
- **A usable raw-SQL escape hatch.** `ExecuteRawAsync` on the store and on a transaction, plus three
  synchronous members that save re-deriving what the store knows: `GetTableName<T>()`,
  `SerializeDocument<T>(value)` (the same UTF-8 JSON bytes the store writes) and
  `DeserializeDocument<T>(json)`. (#46)
- **Guards at open.** `UnsupportedSqliteVersionException` when the loaded SQLite predates 3.45, which
  is where `jsonb()` shipped — instead of the first write failing with `no such function: jsonb` —
  and `IncompatiblePageSizeException` when the database will not take the requested page size. Both
  run in the pool, not the factory, so a consumer-supplied `IConnectionFactory` cannot skip them.
  (#34, #55)
- **`DefaultConnectionFactory` is public and `sealed`**, so a custom `IConnectionFactory` can
  decorate it by delegation rather than re-deriving every PRAGMA. (#86)
  `DefaultTableNamingConvention` is public for the same reason.
- **The Native-AOT claim is now gated in CI.** `examples/AotVerification` is published Native-AOT
  with warnings as errors and the binary is run on every push, and `examples/Examples` holds every
  sample as a real project so samples cannot rot silently. (#35)
- **A connection pool with a bounded wait.** The store rents a connection per operation, so it is
  thread-safe and safe to share across requests; `MaxPoolSize` caps the leasable ones and
  `PoolWaitTimeoutMs` (default 30 s, `Timeout.Infinite` to wait forever) bounds the rent, throwing
  `TimeoutException` naming the option, its value and `MaxPoolSize` rather than queueing until
  something else gives out. (#33, #70)
- **`DocumentStoreOptions.Validate()`**, run from every construction path — the factory, the store's
  own constructor and `DocumentStoreOptionsBuilder.Build()` — so a builder cannot produce options the
  factory then refuses, and the DI path is covered too. (#55, #85)

### Fixed

- **JSON paths are validated at all, and an apostrophe in one is refused.** At 0.4.0 the caller's
  path went verbatim into a single-quoted SQL literal with nothing screening it, so an apostrophe
  closed the literal — a real injection surface, now closed. Every path passes one rule: a member
  may hold whatever SQLite lets it hold except an apostrophe and `U+0000`, with a `.` or `[`
  reachable only through a quoted segment (whose own `"` refusal is above). A store configured with
  `JsonNamingPolicy.KebabCaseLower`, or carrying a `[JsonPropertyName("full-name")]`, is accepted,
  and an expression index over such a path is still used. *Remedy:* reach a key holding an
  apostrophe through `ExecuteRawAsync` with a bound path — doubling it in the interpolated text
  would stop it matching the expression index. (#33, #94)
- **An index is created over the path the documents actually carry.** `JsonPathResolver` resolves
  every expression segment through the serializer's own metadata and emits `JsonPropertyInfo.Name`,
  where it used to read the CLR member name. With a `JsonNamingPolicy` or a `[JsonPropertyName]` in
  play, the old derivation produced valid indexes over paths no row has — and since SQLite counts
  each NULL in a unique index as distinct, a declared unique constraint accepted every duplicate,
  silently. (#63)
- **An index-name collision is detected instead of skipped.** The `sqlite_master` pre-check compares
  definitions rather than skipping on the name alone, and compares again after the create, so two
  callers racing to create the same index still both succeed while a conflicting one is refused.
  (#92)
- **Pooled connections survive their own edge cases.** A connection banked while the pool was being
  disposed is now closed rather than leaked (measured at 313 of 400 barrier-synchronized attempts);
  a leaked transaction is recovered and reported by its finalizer instead of starving the pool; a
  throwing `ILogger` can no longer lose the connection the log line was about; a connection that
  comes back carrying a pending transaction is closed instead of poisoning the next renter; and a
  shared-cache in-memory database keeps a reserved connection, so a discard cannot destroy it.
  (#67, #68, #69, #70, #90)
- **A connection a caller ran raw SQL on is retired rather than recycled.** A session `PRAGMA`, an
  `ATTACH`ed database and a `TEMP` table were each measured to be inherited by the next renter.
  Retiring costs one physical open per raw callback (~335 µs on a WAL file database against ~8 µs
  recycled), so prefer one callback doing several statements. The old requirement that a callback
  restore what it changed is gone. (#114)
- **Arguments are validated before a connection is rented and before the disposal check**, at both
  the store and the transaction boundary, so a bad argument reports the same exception, `ParamName`
  and message whichever one you call. (#93, #100)
- **The two documented migration reads no longer write.** `GetAppliedMigrationsAsync` and
  `GetCurrentMigrationVersionAsync` probe `sqlite_master` and answer `[]`/`0` when the history table
  is absent, instead of creating and upgrading it on every call — measured at 60x on a legacy table.
  (#95)
- **A blob savepoint is released on `CancellationToken.None`.** Once every declared byte is written,
  cancelling made the call throw while the payload stayed committable, so a caller who caught it and
  committed persisted a write they had been told had failed. (#88)
- **`GetTypeInfo`'s `InvalidOperationException` is translated around that one statement only**, so a
  custom `JsonConverter`'s own failure is no longer relabelled as a JSON metadata failure. (#96)
- **Options are snapshotted rather than re-read.** The store, the factory, the pool, the builder and
  the DI registration each clone; registering one options object under two keys with a mutation in
  between used to make both keys open the second database. (#85, #102)
- **A rejection names the parameter the caller can go and fix** — the patch operation caps blame
  `patch`, the duplicate-path check blames `jsonPath`, the two pool option setters name the option
  rather than `value`, and a derived index name that is no identifier is reported against the path
  that produced it. (#94, #100, #103)
- **The package cannot ship with `InternalsVisibleTo` in it.** The csproj drops the attribute on
  every pack path, a build target refuses the rest with `LDS0001`, and CI greps the assembly inside
  the `.nupkg` it just produced. (#110)
