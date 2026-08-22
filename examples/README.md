# LiteDocumentStore examples

Two projects, both in `LiteDocumentStore.slnx`, so CI compiles and runs them — a sample
cannot silently rot against an API change any more.

| Project | What it is |
|---|---|
| `Examples/` | One console app holding every sample, selected by name |
| `AotVerification/` | The Native AOT gate: published with warnings as errors, then run, in CI |

## Running

```powershell
# from the repository root
dotnet run --project examples/Examples                      # list the samples
dotnet run --project examples/Examples -- quickstart        # run one
dotnet run --project examples/Examples -- all               # run every sample in order
```

Every sample uses an in-memory database and cleans up after itself, so they are safe to run
in any order and leave nothing on disk.

## The samples

| Name | Teaches |
|---|---|
| `quickstart` | Create a table, upsert, get, update, query by JSON path, delete, one transaction |
| `hybrid` | The point of the library: document CRUD and raw SQL (joins, aggregates, views, plain relational tables) over the same data |
| `indexes` | Expression indexes on a property, a nested property and a composite, plus before/after timings |
| `virtual-columns` | Generated columns, which is how you get indexed **range** queries — `QueryAsync` is equality-only |
| `transactions` | Batched vs individual writes, `UpsertManyAsync`, rollback on throw, raw SQL enlisted in a transaction |
| `migrations` | `MigrationRunner` with up/down SQL, applied history, rollback to a version, schema introspection |
| `multi-database` | Several independent stores through `IDocumentStoreFactory` |
| `multi-database-keyed` | The same, through keyed DI and typed services |

## AOT verification

```powershell
dotnet run --project examples/AotVerification                                  # JIT
dotnet publish examples/AotVerification -r win-x64 -warnaserror                # what CI does
```

It backs the store with a source-generated `JsonSerializerContext`, which is the AOT-safe
serialization path, and exercises the surviving document surface end to end. `-warnaserror`
turns any `IL2xxx`/`IL3xxx` trim or AOT warning into a build failure.

## Writing a new sample

1. Add `<Name>Example.cs` to `Examples/` — `internal static class`, one
   `public static async Task RunAsync()`, model records nested inside the class (they must not
   collide with the other samples' models).
2. Register it in the `examples` array in `Examples/Program.cs`.
3. Keep it in-memory, keep it under a couple of seconds (CI runs `-- all` on every push), and
   let it fail loudly rather than swallowing an exception.

## Notes that bite

- Raw SQL goes through `ExecuteRawAsync`; there is no `Connection` property to grab. Inside a
  transaction, call `tx.ExecuteRawAsync` so the commands enlist in it, and create commands with
  `connection.CreateCommand()` — a `new SqliteCommand(...)` has no transaction attached and will
  be refused.
- Reading a document in raw SQL is `SELECT json(data)`. A bare `SELECT data` hands back JSONB
  binary, which is not JSON text.
- The table for `T` is named after the type's simple name (`Customer` → `[Customer]`) unless you
  supply an `ITableNamingConvention`.
- Operations invoked on the *store* inside a transaction callback take their own connection and
  commit separately — always write through the transaction object.
