# Migrations

## Contents
- [Signatures](#signatures)
- [SqlMigration on startup](#sqlmigration-on-startup)
- [Custom IMigration](#custom-imigration)
- [Rules for UpAsync / DownAsync](#rules-for-upasync--downasync)
- [Ordering and checksums](#ordering-and-checksums)
- [Rollback](#rollback)
- [Pitfalls](#pitfalls)

Migrations are versioned schema changes tracked in `__store_migrations`. They live on
`IDocumentStore` only, never on a transaction, and each one runs in its own `BEGIN IMMEDIATE`
transaction. There is no auto-migrate and no DI hook: call `MigrateAsync` yourself at startup.
It is idempotent and safe across concurrent processes.

For plain document tables and indexes, `CreateTableAsync<T>()` and `CreateIndexAsync` at
startup are enough. Use migrations for changes that must happen once and in order: data
backfills, raw tables, partial indexes with values, renames.

## Signatures

```csharp
// IDocumentStore
Task<int> MigrateAsync(IEnumerable<IMigration> migrations, CancellationToken ct = default);
Task<int> MigrateAsync(IEnumerable<IMigration> migrations, MigrationOptions options, CancellationToken ct = default);
Task<IReadOnlyList<MigrationHistoryRecord>> GetAppliedMigrationsAsync(CancellationToken ct = default);
Task<long> GetCurrentMigrationVersionAsync(CancellationToken ct = default);   // 0 when none
Task<int> RollbackToVersionAsync(long targetVersion, IEnumerable<IMigration> migrations, CancellationToken ct = default);

public interface IMigration
{
    long Version { get; }              // > 0
    string Name { get; }
    string? Checksum => null;          // default interface member; null = no drift detection
    Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
    Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}

public SqlMigration(long version, string name, string upSql, string downSql);   // multi-statement SQL allowed
public virtual string Checksum { get; }   // CRC-32C of upSql, 8 uppercase hex chars

public sealed class MigrationOptions
{
    public static MigrationOptions Default { get; }
    public bool AllowOutOfOrder { get; init; }        // default false
    public bool VerifyChecksums { get; init; } = true;
}

public sealed class MigrationHistoryRecord { long Version; string Name; DateTimeOffset AppliedAt; string? Checksum; } // init-only
```

## SqlMigration on startup

```csharp
var customerTable = store.GetTableName<Customer>();   // never hardcode: names are namespace-qualified

IMigration[] migrations =
[
    new SqlMigration(
        version: 20260822001,
        name: "CreateCustomerTable",
        // a hand-written document table MUST match this schema exactly, version column included
        upSql: $"CREATE TABLE IF NOT EXISTS [{customerTable}] (id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1);",
        downSql: $"DROP TABLE IF EXISTS [{customerTable}];"),
    new SqlMigration(
        version: 20260822002,
        name: "ActiveCustomerEmailIndex",
        upSql: $"CREATE INDEX IF NOT EXISTS idx_customer_active_email ON [{customerTable}](json_extract(data, '$.Email')) WHERE json_extract(data, '$.Status') = 'active';",
        downSql: "DROP INDEX IF EXISTS idx_customer_active_email;"),
];

int applied = await store.MigrateAsync(migrations);      // 2 the first time, 0 on re-run
long current = await store.GetCurrentMigrationVersionAsync();
foreach (var r in await store.GetAppliedMigrationsAsync())
    Console.WriteLine($"{r.Version} {r.Name} {r.AppliedAt:u}");
```

Use sortable, positive versions, such as `yyyyMMddNNN`. Keep the full list in code forever,
because rollback needs it.

## Custom IMigration

```csharp
internal sealed class BackfillStatus : IMigration
{
    public long Version => 20260901001;
    public string Name => "BackfillCustomerStatus";
    public string? Checksum => "backfill-status-v1";       // change it when UpAsync's behaviour changes

    public async Task UpAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();   // enlists in the runner's transaction
        cmd.CommandText = """
            UPDATE [MyApp_Customer]
            SET data = jsonb_set(data, '$.Status', 'active'), version = version + 1
            WHERE json_extract(data, '$.Status') IS NULL
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DownAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await Task.CompletedTask;   // irreversible data backfill
    }
}
```

The table name is inlined above for brevity. In real code, pass `store.GetTableName<Customer>()`
in through the constructor.

To seed documents, bind `store.SerializeDocument(value)` (a `byte[]`) to `jsonb(@Data)` on the
given connection.

A `SqlMigration` subclass that changes what `UpAsync` runs must **`override`** `Checksum`.
`public new string Checksum` is silently ignored, because the runner reads it through
`IMigration`. `=> null!` opts out of verification.

## Rules for UpAsync / DownAsync

The connection you receive is already inside the runner's transaction. The runner commits it
together with the history row.
- **Never begin, commit or roll back.** `connection.BeginTransaction()` throws. A raw `COMMIT`
  is silently accepted and leaves schema and history in an unpredictable state.
- **Build commands with `connection.CreateCommand()`.** `new SqliteCommand(sql, connection)` has
  no transaction attached and fails.
- **Do not call back into the store.** The run holds its lease. At `MaxPoolSize = 1` the call
  hangs, then throws `TimeoutException`. Otherwise it runs outside the migration's transaction
  and can hit `SQLITE_BUSY`.
- **`VACUUM` and `PRAGMA foreign_keys = OFF` cannot work here.** `VACUUM` throws, and the PRAGMA
  is silently ignored, so FKs stay enforced during a table rebuild. Run them through
  `store.ExecuteRawAsync` before or after `MigrateAsync`.
- An `UpAsync` that throws leaves nothing behind for that migration. Earlier migrations in the
  same run stay applied, and a re-run resumes from the failed one.

## Ordering and checksums

- "Applied" means the version is present in the history table. A never-applied version **below**
  the current maximum (for example a branch merge) throws `MigrationOutOfOrderException`
  (`Version`, `Name`, `CurrentVersion`). Pass `new MigrationOptions { AllowOutOfOrder = true }`
  only when that back-fill is safe.
- Checksums cover **only** the up SQL. Editing an applied migration's `upSql` makes the next
  `MigrateAsync` throw `MigrationChecksumMismatchException` (`ExpectedChecksum` = stored,
  `ActualChecksum` = supplied). Editing `downSql` is fine.
- **To change schema, add a new migration. Never edit an applied one.**
- `VerifyChecksums = false` is an escape hatch. A null checksum on either side skips the check.
- A version <= 0, a null element or duplicate versions throw `ArgumentException` before anything
  runs.

```csharp
using LiteDocumentStore.Exceptions;
try { await store.MigrateAsync(migrations); }
catch (MigrationOutOfOrderException ex) { /* ex.Version < ex.CurrentVersion, never applied */ throw; }
catch (MigrationChecksumMismatchException ex) { /* an applied migration's up SQL changed */ throw; }
```

## Rollback

```csharp
int rolledBack = await store.RollbackToVersionAsync(20260822001, migrations);   // undo everything above it
await store.RollbackToVersionAsync(0, migrations);                              // undo all
```

- Rollback runs `DownAsync` newest-first, each migration in its own transaction. Checksums are
  never verified.
- Every applied migration above the target must be in the list you pass. If one is missing,
  `LiteDocumentStoreException` is thrown and nothing is rolled back.
- A negative target throws `ArgumentOutOfRangeException`.
- A `DownAsync` that throws leaves that migration applied. Migrations already rolled back stay
  rolled back.

## Pitfalls

- A hand-written document table without the `version` column makes every upsert fail. Prefer
  `CreateTableAsync<T>()`.
- `MigrateAsync(m, default)` binds to the `CancellationToken` overload, not `MigrationOptions`.
  Pass the options explicitly when you mean them.
- A `SqlMigration` whose `upSql` interpolates `GetTableName<T>()` has the table name in its
  checksum. Renaming or moving `T` (or changing the naming convention) then makes the next
  `MigrateAsync` throw `MigrationChecksumMismatchException`. Once a migration has shipped, freeze
  its SQL as a string literal holding the name it was written with.
- `GetAppliedMigrationsAsync` and `GetCurrentMigrationVersionAsync` never create the history
  table. They return `[]` and `0` on a fresh database.
