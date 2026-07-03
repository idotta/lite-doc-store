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
    Core/            DocumentStore, IDocumentStore, SqlGenerator, SqliteCommandExtensions
                     (raw ADO helpers), DocumentStoreOptions(+Builder)
    Conventions/     ITableNamingConvention — maps type -> table name
    Factories/       IDocumentStoreFactory, IConnectionFactory (+ Default impls)
    Extensions/      ServiceCollectionExtensions (AddLiteDocumentStore, keyed variant)
    Migrations/      MigrationRunner, IMigration/Migration, SchemaIntrospector
    Serialization/   JsonHelper (STJ, via JsonTypeInfo<T>)
    Exceptions/      LiteDocumentStoreException + Concurrency/Serialization/TableNotFound
  tests/
    LiteDocumentStore.UnitTests/             xUnit, mocked/isolated
    LiteDocumentStore.IntegrationTests/      xUnit, real SQLite (mostly :memory:)
    LiteDocumentStore.Benchmarks/            BenchmarkDotNet
```

`DocumentStore` and most internals are `internal sealed`; the test/benchmark projects see them via `InternalsVisibleTo` in the csproj. Consumers only touch the public surface: `IDocumentStore`, `DocumentStoreOptions`, the factories, and the DI extension.

> Note: the `tests/` projects were updated for the Dapper removal (tests that only covered the dropped `QueryAsync(predicate)` / `SelectAsync` APIs were removed; the rest use the raw-ADO helpers in `Core/SqliteCommandExtensions.cs`). The `benchmarks/` project intentionally keeps a `Dapper` package reference as a *comparison baseline* only — it is not a library dependency. All three projects compile and `dotnet test` passes.

## Documentation is stale — trust the code

`README.md` and `.github/instructions/*.md` (Copilot rules) describe an older `Repository` class with a `new Repository("app.db")` constructor and a `SqliteJsonbTypeHandler`. **That API no longer exists.** The real entry point is `IDocumentStore`, obtained through DI (`services.AddLiteDocumentStore(...)`) or `IDocumentStoreFactory.Create/CreateAsync(DocumentStoreOptions)`. When those docs conflict with the source, the source wins. The *conceptual* guidance in those files (JSONB read/write pattern, WAL config, hybrid philosophy, SQLite error codes) is still accurate.

## Architecture

**All SQL is centralized in `SqlGenerator`** (static, one method per statement shape). Nothing else hand-writes SQL against document tables. Table schema is uniform: `id TEXT PRIMARY KEY, data BLOB NOT NULL`. The JSONB contract, enforced there, is load-bearing:
- **Write:** `jsonb(@Data)` — `@Data` is UTF-8 JSON *bytes* from `JsonHelper.SerializeToUtf8Bytes`, not a string.
- **Read:** `SELECT json(data)` — converts JSONB binary back to JSON text for deserialization. JSONB is binary; a raw `SELECT data` is not deserializable.
- Table names are always bracketed (`[{tableName}]`) and derived from the type name via `ITableNamingConvention` — never from user input, so there's no injection surface there. All *values* are parameterized.

**Connection model.** A `DocumentStore` wraps one `SqliteConnection` and an `ownsConnection` flag. Via the factory it owns and manages the connection (opened + PRAGMAs applied by `DefaultConnectionFactory` from `DocumentStoreOptions`); when disposed it runs `PRAGMA wal_checkpoint(TRUNCATE)` before closing. The DI registration defaults to **Singleton** — one long-lived connection. Every method guards with `ObjectDisposedException.ThrowIf` + `EnsureConnectionOpen`.

**Querying.** Documents are queried by JSON path + value via `QueryAsync<T, TValue>(jsonPath, value)`, which builds `WHERE json_extract(data, '$.Path') = @Value`. The LINQ-predicate `QueryAsync<T>(Expression<Func<T,bool>>)` and the `SelectAsync` projections were **removed** (they required runtime reflection / IL generation that AOT can't support). `CreateIndexAsync`, `CreateCompositeIndexAsync`, and `AddVirtualColumnAsync` still accept LINQ expressions, but only walk **member names** (`DocumentStore.ExtractJsonPath`) to build `$.Path` — no compilation or closure evaluation, so they stay AOT-safe. Property names map **as-is (PascalCase)** to match default STJ serialization. For richer filtering (ranges, virtual-column index seeks, joins), drop to raw SQL via the `Connection` escape hatch.

**Transactions.** `ExecuteInTransactionAsync` wraps `BeginTransaction` with commit/rollback. Commands are created with `SqliteConnection.CreateCommand`, which auto-enlists the connection's active transaction, so document operations invoked inside the action participate automatically. One transaction per connection (no nesting) — batch writes go through `UpsertManyAsync`/`DeleteManyAsync`, which build a single multi-row statement with explicit `@Id{i}`/`@Data{i}` parameters.

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

- `src/LiteDocumentStore/Core/DocumentStore.cs` — the whole implementation; every public operation lives here.
- `src/LiteDocumentStore/Core/SqlGenerator.cs` — the JSONB SQL contract; change SQL here, nowhere else.
- `src/LiteDocumentStore/Core/SqliteCommandExtensions.cs` — the raw ADO.NET helpers that replaced Dapper.
- `src/LiteDocumentStore/Serialization/JsonHelper.cs` — the AOT-safe serialization funnel (`JsonTypeInfo<T>` + reflection fallback).
- `src/LiteDocumentStore/Extensions/ServiceCollectionExtensions.cs` — how consumers wire it up (DI + lifetimes).
- `examples/AotVerification.cs` — end-to-end AOT smoke test with a source-generated context.
</content>
