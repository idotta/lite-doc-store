# Documents and optimistic concurrency

## Contents
- [Signatures](#signatures)
- [CRUD and batches](#crud-and-batches)
- [Ids](#ids)
- [Versions and compare-and-swap](#versions-and-compare-and-swap)
- [CAS retry loop](#cas-retry-loop)
- [Not found vs corrupt vs incompatible](#not-found-vs-corrupt-vs-incompatible)
- [Pitfalls](#pitfalls)

Every member below is on `IDocumentOperations`, so it exists on both `IDocumentStore` and
`IDocumentTransaction`. Every async member takes `CancellationToken cancellationToken = default`
last.

## Signatures

```csharp
Task CreateTableAsync<T>(CancellationToken ct = default);          // CREATE TABLE IF NOT EXISTS
Task<int> UpsertAsync<T>(string id, T data, CancellationToken ct = default);
Task<int> UpsertManyAsync<T>(IEnumerable<(string id, T data)> items, CancellationToken ct = default);
Task<T?> GetAsync<T>(string id, CancellationToken ct = default);
Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken ct = default);
Task<IReadOnlyDictionary<string, T>> GetManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default);
Task<bool> ExistsAsync<T>(string id, CancellationToken ct = default);
Task<long> CountAsync<T>(CancellationToken ct = default);
Task<bool> DeleteAsync<T>(string id, CancellationToken ct = default);
Task<int> DeleteManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default);
Task<int> DeleteAllAsync<T>(CancellationToken ct = default);        // rows removed; table and indexes stay
Task DropTableAsync<T>(CancellationToken ct = default);             // IF EXISTS

Task<VersionedDocument<T>?> GetWithVersionAsync<T>(string id, CancellationToken ct = default);
Task<long> UpsertWithVersionAsync<T>(string id, T data, long expectedVersion, CancellationToken ct = default);
Task DeleteWithVersionAsync<T>(string id, long expectedVersion, CancellationToken ct = default);

public sealed record VersionedDocument<T>(T Data, long Version);
```

## CRUD and batches

```csharp
// Example types used in this file
public sealed record Customer(string Id, string Name, string City, int Age, string? Email, string[] Tags);
public sealed record Person(string Name);
public sealed record Account(string Owner, decimal Balance);

await store.CreateTableAsync<Customer>();                    // required first; idempotent

await store.UpsertAsync("1", new Customer("1", "Alice", "Seattle", 34, "alice@x.io", []));
await store.UpsertManyAsync(
[
    ("2", new Customer("2", "Bob", "Portland", 41, "bob@x.io", [])),
    ("3", new Customer("3", "Carol", "Seattle", 29, "carol@x.io", [])),
]);

Customer? alice = await store.GetAsync<Customer>("1");       // null when absent
if (alice is not null)
    await store.UpsertAsync("1", alice with { City = "Tacoma" });   // whole-document replace

var some = await store.GetManyAsync<Customer>(["1", "2", "nope"]);
bool hasNope = some.ContainsKey("nope");                      // false: a missing id is an absent key, never null

long count = await store.CountAsync<Customer>();
bool exists = await store.ExistsAsync<Customer>("2");         // does not read the payload
IEnumerable<Customer> all = await store.GetAllAsync<Customer>();   // UNORDERED, fully materialized

bool deleted = await store.DeleteAsync<Customer>("3");        // false if absent
int removed = await store.DeleteManyAsync<Customer>(["1", "2"]);
```

Batch behaviour:
- `UpsertManyAsync` validates and serializes every item before writing anything, so one bad
  item writes nothing.
- Batches run in chunks of 500 rows per statement. A multi-chunk batch on the store is wrapped
  in its own transaction, so the batch is all-or-nothing.
- Empty input returns 0 (or an empty dictionary) without a round trip.
- `UpsertManyAsync` rejects duplicate ids with an `ArgumentException` naming both indices.
  `DeleteManyAsync` and `GetManyAsync` silently de-duplicate.
- `GetManyAsync` over more than 500 ids runs several statements, so the result is a
  point-in-time snapshot only when called on a transaction.
- For ordered or filtered reads, use `QueryAsync(DocumentQuery<T>)` (see
  querying-and-patching.md), not `GetAllAsync` plus LINQ.

## Ids

- The id is the key argument. The document's own `Id` property is just payload, and nothing
  keeps the two in sync. Pass the same value consistently.
- Null, empty or whitespace ids throw `ArgumentException`. For a batch, the message names the
  index.
- Ids are compared ordinally and case-sensitively, and are not trimmed: `"A"`, `"a"` and `" a"`
  are three documents.

## Versions and compare-and-swap

Every row has a `version`. It starts at 1 and **every** write bumps it, including plain
`UpsertAsync`, `UpsertManyAsync` and patches. Mixing plain and CAS writes therefore stays
coherent: a holder of an older version conflicts.

| Call | `expectedVersion = 0` | `expectedVersion = n > 0` | Missing id |
|---|---|---|---|
| `UpsertWithVersionAsync` | Insert; the id must not exist (else `AlreadyExists`). A row stored at version 0 (raw SQL) is updated and lifted to 1 instead | Update only if stored == n (else `VersionMismatch`) | 0: inserts. n: `DocumentNotFound`. |
| `DeleteWithVersionAsync` | Matches only a row stored at version 0 (no insert meaning) | Delete only if stored == n | Always `DocumentNotFound`. It does **not** return false. |
| `PatchWithVersionAsync` | Matches only a row stored at version 0 (a patch never inserts) | Patch only if stored == n | `DocumentNotFound` |

A negative `expectedVersion` throws `ArgumentOutOfRangeException`.
`UpsertWithVersionAsync` returns the version SQLite stored. Use that value, rather than
computing `expected + 1`.

```csharp
using LiteDocumentStore.Exceptions;

long v1 = await store.UpsertWithVersionAsync("p1", new Person("Ada"), expectedVersion: 0);     // 1
long v2 = await store.UpsertWithVersionAsync("p1", new Person("Ada Lovelace"), expectedVersion: v1); // 2

try
{
    await store.UpsertWithVersionAsync("p1", new Person("Imposter"), expectedVersion: v1);    // stale
}
catch (ConcurrencyException ex) when (ex.Kind == ConcurrencyConflictKind.VersionMismatch)
{
    // ex.DocumentId == "p1", ex.TableName, ex.ExpectedVersion == 1, ex.ActualVersion == 2; row untouched
}

await store.DeleteWithVersionAsync<Person>("p1", v2);
```

`ConcurrencyException` exposes `DocumentId`, `TableName`, `ExpectedVersion`, `ActualVersion`
(null when the row is absent) and `Kind` (`AlreadyExists`, `VersionMismatch`, `DocumentNotFound`).
`ActualVersion` and `Kind` come from a second read after the rejected write. Outside a
transaction the row may change in between, so treat them as a hint for the retry strategy.
Inside a transaction they are exact.

To learn a row's version, use `GetWithVersionAsync`. Never probe with a guessed-version CAS
delete: if the guess matches, the row is deleted.

## CAS retry loop

```csharp
static async Task<long> DepositAsync(IDocumentStore store, string id, decimal amount, CancellationToken ct = default)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        var current = await store.GetWithVersionAsync<Account>(id, ct)
            ?? throw new InvalidOperationException($"Account {id} not found");
        try
        {
            return await store.UpsertWithVersionAsync(
                id, current.Data with { Balance = current.Data.Balance + amount }, current.Version, ct);
        }
        catch (ConcurrencyException ex) when (ex.Kind == ConcurrencyConflictKind.VersionMismatch)
        {
            // another writer got in first: re-read and retry
        }
    }
    throw new TimeoutException("Too much contention");
}
```

For a change to a few scalar fields, `PatchWithVersionAsync` avoids re-serializing the whole
document. When the read and the write must happen inside a transaction, use
`TransactionMode.Immediate` (see transactions-and-raw-sql.md).

## Not found vs corrupt vs incompatible

| Situation | Result |
|---|---|
| Id absent | `GetAsync` returns `default`, `GetWithVersionAsync` returns `null`, `GetManyAsync` omits the key |
| Row exists but `data` is SQL NULL or JSON `null` (only possible via raw SQL) | `CorruptDataException` (`Id`, `TableName`, `TargetType`). `GetAllAsync`, `GetManyAsync` and `QueryAsync` fail the whole read rather than skip the row. `DeleteAsync` still removes it. |
| Well-formed JSON that does not fit `T` | `DocumentSerializationException` (`TargetType`, inner `JsonException`) |
| Write-side serialization failure (e.g. a cycle) | `DocumentSerializationException` |

For a value-type `T`, `GetAsync` returns `default(T)` for an absent id, which you cannot tell
apart from a stored default. Use `ExistsAsync` or `GetWithVersionAsync` instead.

## Pitfalls

- An operation on a table that was never created throws a raw `SqliteException` ("no such table").
- `UpsertAsync` is last-writer-wins on the whole document. A read-modify-upsert of a stale copy
  reverts other writers' fields. Use `PatchAsync`, or the `*WithVersionAsync` methods.
- `UpsertAsync(id, null)` throws `ArgumentNullException`. A null item inside `UpsertManyAsync`
  throws `ArgumentException`.
- `ExistsAsync<T>(null!)` and `DeleteAsync<T>(null!)` are ambiguous between the string and
  `DocumentQuery<T>` overloads and do not compile.
- The document type is the static `T`: upserting a derived object through a base-typed or
  interface variable uses the base table and drops the derived members. See SKILL.md rule 13.
- Each store call commits on its own. For atomic multi-document work, use a transaction and call
  operations on it.
- A user-added `UNIQUE` index violation surfaces as an untranslated `SqliteException`.
