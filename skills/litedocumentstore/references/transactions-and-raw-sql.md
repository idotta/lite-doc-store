# Transactions and raw SQL

## Contents
- [Signatures](#signatures)
- [Using a transaction](#using-a-transaction)
- [TransactionMode](#transactionmode)
- [Busy, locked and timeout errors](#busy-locked-and-timeout-errors)
- [ExecuteRawAsync](#executerawasync)
- [Raw SQL and the JSONB contract](#raw-sql-and-the-jsonb-contract)
- [Cancellation](#cancellation)
- [Pitfalls](#pitfalls)

## Signatures

```csharp
// IDocumentStore
Task<IDocumentTransaction> BeginTransactionAsync(CancellationToken ct = default);                       // Deferred
Task<IDocumentTransaction> BeginTransactionAsync(TransactionMode mode, CancellationToken ct = default);
Task ExecuteInTransactionAsync(Func<IDocumentTransaction, Task> action, CancellationToken ct = default);
Task ExecuteInTransactionAsync(Func<IDocumentTransaction, Task> action, TransactionMode mode, CancellationToken ct = default);

public enum TransactionMode { Deferred = 0, Immediate = 1 }

// IDocumentTransaction : IDocumentOperations, IAsyncDisposable, IDisposable
bool IsCommitted { get; }
Task CommitAsync(CancellationToken ct = default);
Task RollbackAsync(CancellationToken ct = default);

// IDocumentOperations (store and transaction)
Task<TResult> ExecuteRawAsync<TResult>(Func<SqliteConnection, CancellationToken, Task<TResult>> operation, CancellationToken ct = default);
Task ExecuteRawAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken ct = default);
string GetTableName<T>();
byte[] SerializeDocument<T>(T value);        // the exact bytes the store writes
T? DeserializeDocument<T>(string? json);     // for a raw SELECT json(data) column
```

A transaction exposes the whole `IDocumentOperations` surface: CRUD, queries, patches, DDL,
blob writes and `GetBlobAsync`, and `ExecuteRawAsync`. It does **not** expose
`OpenBlobReadAsync`, migrations or `RebuildBlobTableAsync`, which are store-only.

## Using a transaction

A transaction holds **one** pooled connection until it is committed, rolled back or disposed.

```csharp
// Wrapper: commits when the callback returns; rolls back and rethrows the original exception if it throws
await store.ExecuteInTransactionAsync(async tx =>
{
    await tx.UpsertAsync(order.Id, order);          // ON tx, not store
    await tx.PutBlobAsync(order.Id, invoicePdf);
});

// Explicit: ALWAYS await using
await using var tx = await store.BeginTransactionAsync();
await tx.UpsertAsync(order.Id, order);
await tx.CommitAsync();       // without this, the dispose at end of scope rolls back
```

- **Call operations on `tx`.** A store call inside the block uses another connection, commits
  on its own and is not rolled back with `tx`.
  - A store **write** after `tx` has written waits on `tx`'s write lock until `BusyTimeoutMs`,
    then throws `SqliteException` `SQLITE_BUSY`.
  - At `MaxPoolSize = 1`, even a store read waits for the slot `tx` holds, then throws
    `TimeoutException`.
- For a pure bulk write, `store.UpsertManyAsync(...)` is a single call and already
  all-or-nothing.
- After `CommitAsync` or `RollbackAsync`, every member throws `InvalidOperationException`. This
  includes `GetTableName`, `SerializeDocument` and `DeserializeDocument`; call those on the store
  instead. After disposal, members throw `ObjectDisposedException`. A second commit or rollback
  throws `InvalidOperationException`.
- A failed `CommitAsync` leaves the transaction open. Disposal (guaranteed by `await using`)
  rolls it back.
- A leaked, never-disposed transaction holds a pool slot until the GC finalizes it. Meanwhile
  other operations queue and fail with `TimeoutException` after `PoolWaitTimeoutMs`.
- The `ExecuteInTransactionAsync` callback receives no token. Capture yours in the closure and
  pass it to operations inside.

## TransactionMode

| Mode | Lock | Use when |
|---|---|---|
| `Deferred` (default) | none until the first write | write-only units of work, or read-only snapshots |
| `Immediate` | write lock at `BEGIN` | **read-then-write**: get then upsert, check a version then write, check existence then insert |

Why it matters: in a Deferred transaction that reads first, if another connection commits
before your first write, that write fails with `SqliteException` extended code **517
(`SQLITE_BUSY_SNAPSHOT`)**. Waiting cannot fix it, so the whole transaction must be redone. It
does not fail fast: the provider retries until its command timeout (derived from `BusyTimeoutMs`)
runs out, so it first looks like a hang.
`Immediate` makes this impossible. The cost is that concurrent writers serialize for the whole
transaction, so use it only when needed.

```csharp
await store.ExecuteInTransactionAsync(async tx =>
{
    var customer = await tx.GetAsync<Customer>("c1");
    if (customer is not null)
        await tx.UpsertAsync("c1", customer with { City = "Austin" });
}, TransactionMode.Immediate);
```

A bare `default` in `BeginTransactionAsync(default)` or `ExecuteInTransactionAsync(action, default)`
binds to the `CancellationToken` overload (Deferred), not to `TransactionMode`. Pass the mode explicitly.

## Busy, locked and timeout errors

The library has **no retry policy**. Retry the whole unit of work yourself, and make it
idempotent:

```csharp
using Microsoft.Data.Sqlite;

const int SqliteBusy = 5;
const int SqliteBusySnapshot = 517;
for (var attempt = 1; ; attempt++)
{
    try
    {
        await store.ExecuteInTransactionAsync(async tx =>
        {
            var acct = await tx.GetAsync<Account>("a1");
            await tx.UpsertAsync("a1", acct! with { Balance = acct.Balance - 10 });
        }, TransactionMode.Immediate);
        break;
    }
    catch (SqliteException ex) when (attempt < 3 &&
        (ex.SqliteErrorCode == SqliteBusy || ex.SqliteExtendedErrorCode == SqliteBusySnapshot))
    {
        // rolled back; loop redoes the whole unit
    }
}
```

| Error | Cause |
|---|---|
| `SqliteException` code 5 `SQLITE_BUSY` | The write lock was held past the busy wait: another `Immediate` BEGIN, a store write during a transaction, a long writer |
| `SqliteException` extended 517 | A Deferred read-then-write lost a race. Redo the transaction or use `Immediate`. |
| `SqliteException` `SQLITE_LOCKED` | Overlapping writes on a shared-cache **in-memory** DB. Not retried by busy_timeout. Use a file DB for concurrent writers. |
| `TimeoutException` | No pooled connection within `PoolWaitTimeoutMs`, usually leaked transactions or streams |

## ExecuteRawAsync

The escape hatch for joins, aggregates, OR logic, projections, views and your own tables.

- **On the store**, it rents a connection, passes it and the token to your callback, then
  **retires** the connection. Each call opens a fresh physical connection, costing about 335 µs
  on a WAL file DB versus about 8 µs for a normal operation. Put several statements in one
  callback, and avoid it in hot loops where a document API exists. The benefit is that any
  PRAGMA, `ATTACH` or TEMP table you set never leaks to later operations, so nothing needs
  restoring.
- **On a transaction**, the callback gets the transaction's connection, so commands enlist in it.
  That connection is retired when the transaction completes: one physical open per transaction,
  however many callbacks it ran.
- The connection is valid only inside the callback. Never store it.
- **Build commands with `connection.CreateCommand()`.** `new SqliteCommand(sql, conn)` has no
  transaction attached and fails when one is pending, which is always the case inside
  `tx.ExecuteRawAsync`.
- Inside `tx.ExecuteRawAsync`, never issue `BEGIN`/`COMMIT`/`ROLLBACK` or call
  `connection.BeginTransaction()`. A raw `COMMIT` ends the store's transaction behind its back.

```csharp
var orderTable = store.GetTableName<Order>();
var customerTable = store.GetTableName<Customer>();

var rows = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT o.id, json_extract(c.data, '$.Name')
        FROM [{orderTable}] o
        JOIN [{customerTable}] c ON json_extract(o.data, '$.CustomerId') = c.id
        WHERE json_extract(o.data, '$.Total') > @min
        """;
    cmd.Parameters.AddWithValue("@min", 100.0);   // never decimal: it binds as TEXT and matches nothing
    var result = new List<(string OrderId, string CustomerName)>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) result.Add((r.GetString(0), r.GetString(1)));
    return result;
});
```

## Raw SQL and the JSONB contract

The document table schema is `id TEXT PRIMARY KEY, data BLOB NOT NULL, version INTEGER NOT NULL DEFAULT 1`,
and `data` holds **JSONB binary**.

| Do | Don't |
|---|---|
| Read whole documents with `SELECT json(data)`, then `store.DeserializeDocument<T>(text)` | `SELECT data`: binary, not deserializable |
| Filter and project with `json_extract(data, '$.Path')` | |
| Write with `jsonb(@Data)`, binding `@Data` to the `byte[]` from `SerializeDocument<T>` | Bind a JSON string, or write text into `data` |
| Update fields with `jsonb_set` / `jsonb_remove`, or better `PatchAsync` | `json_set` / `json_remove` / `json(...)` on `data`: they return TEXT and silently de-binary the column |
| Add `version = version + 1` in a raw UPDATE | Leave the version alone, so CAS callers miss the change |
| Use `[{store.GetTableName<T>()}]` | Hardcode the table name |
| Bind all values as parameters | Interpolate values |

```csharp
// Raw write readable by the document API
var table = store.GetTableName<Person>();
await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"INSERT INTO [{table}] (id, data, version) VALUES (@Id, jsonb(@Data), 1)";
    cmd.Parameters.AddWithValue("@Id", "p2");
    cmd.Parameters.AddWithValue("@Data", store.SerializeDocument(person));   // byte[]
    await cmd.ExecuteNonQueryAsync(ct);
});

// Raw field update inside a transaction
await store.ExecuteInTransactionAsync(async tx =>
{
    await tx.UpsertAsync("c4", new Customer("c4", "David", "Denver", 52, "david@x.io", []));
    await tx.ExecuteRawAsync(async (conn, ct) =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE [{tx.GetTableName<Customer>()}] " +
                          "SET data = jsonb_set(data, '$.Name', @name), version = version + 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@name", "David Miller");
        cmd.Parameters.AddWithValue("@id", "c4");
        await cmd.ExecuteNonQueryAsync(ct);
    });
});
```

`DeserializeDocument<T>` returns `default` for a null or empty string and throws
`DocumentSerializationException` on malformed JSON. For the literal `null` it returns `default`
for reference and `Nullable<T>` types, but throws for other value types.
`SerializeDocument<T>(null)` throws `ArgumentNullException`.

## Cancellation

- Every async member takes `CancellationToken cancellationToken = default`.
- On the store, the token cancels the wait for a pooled connection and is passed to the command.
  A statement that is already running is **not** interrupted.
- On a transaction, there is no connection wait, so only the command observes the token.
- Catch `OperationCanceledException`, which also covers `TaskCanceledException`. If a write was
  cancelled inside a transaction, let the transaction roll back.

## Pitfalls

- Forgetting `CommitAsync` loses the work silently on dispose.
- Never `Task.WhenAll` operations on one transaction. It is one connection; await them
  sequentially.
- Migrations cannot join an `IDocumentTransaction`. They run their own transactions.
