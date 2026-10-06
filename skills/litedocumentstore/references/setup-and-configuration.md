# Setup and configuration

## Contents
- [Getting a store](#getting-a-store)
- [Presets](#presets)
- [DocumentStoreOptions](#documentstoreoptions)
- [The builder](#the-builder)
- [Dependency injection](#dependency-injection)
- [Several databases](#several-databases)
- [Custom connection factory](#custom-connection-factory)
- [Table naming](#table-naming)
- [Pool, timeouts, disposal and health](#pool-timeouts-disposal-and-health)
- [Pitfalls](#pitfalls)

## Getting a store

`DocumentStore` is internal. A store comes from `DocumentStoreFactory` or from DI. Code against
`IDocumentStore`. There is no `DefaultDocumentStoreFactory`.

```csharp
public interface IDocumentStoreFactory
{
    IDocumentStore Create(DocumentStoreOptions options);
    Task<IDocumentStore> CreateAsync(DocumentStoreOptions options, CancellationToken cancellationToken = default);
}

public DocumentStoreFactory();
public DocumentStoreFactory(IConnectionFactory connectionFactory);
public DocumentStoreFactory(IConnectionFactory connectionFactory, ITableNamingConvention? tableNamingConvention, ILoggerFactory? loggerFactory);
```

`Create`/`CreateAsync` validate the options and open the first connection. Option, SQLite
version and page-size errors therefore throw here (or at the first DI resolution), not later.

```csharp
await using IDocumentStore store = await new DocumentStoreFactory()
    .CreateAsync(DocumentStoreOptions.ForFile("data/app.db"));
await store.CreateTableAsync<Customer>();
```

## Presets

| Preset | Connection string | WAL | Synchronous | Use for |
|---|---|---|---|---|
| `DocumentStoreOptions.ForFile(path)` | `Data Source={path}` | on | Normal | Production file DB |
| `DocumentStoreOptions.ForInMemory()` | unique `file:lds-<guid>?mode=memory&cache=shared` | off | Off | Isolated tests |
| `DocumentStoreOptions.ForSharedInMemory(name = "shared")` | `file:{name}?mode=memory&cache=shared` | off | Off | Several stores in one process sharing data |

In-memory data lives only while the store is alive. Two stores using the same
`ForSharedInMemory` name share one database, which leaks state between parallel tests. Use
`ForInMemory()` for isolation. `ForSharedInMemory` rejects a blank name or one containing
`; ? & #`.

Shared-cache in-memory databases lock **per table**. Overlapping write transactions fail with
`SQLITE_LOCKED`, which `BusyTimeoutMs` does not retry. Test concurrent writers against a
temporary **file** database.

## DocumentStoreOptions

| Property | Default | Notes |
|---|---|---|
| `ConnectionString` | `""` | Keyword form only (`Data Source=...`). It is required. |
| `EnableWalMode` | `true` | Must be `false` for in-memory databases. |
| `SynchronousMode` | `Normal` | `Off` / `Normal` / `Full`. `Off` can corrupt data on power loss. |
| `PageSize` | `4096` | A power of 2 in [512, 65536], or `0` to keep whatever the file has. |
| `CacheSize` | `-2000` | Positive = pages, negative = KiB. The default is about 2 MiB. |
| `BusyTimeoutMs` | `5000` | Lock wait. It also sets the command timeout (seconds, rounded up, minimum 1). |
| `EnableForeignKeys` | `true` | Applied explicitly as ON or OFF. |
| `MaxPoolSize` | `clamp(ProcessorCount, 2, 16)` | Concurrent operations and open transactions. The setter throws below 1. |
| `PoolWaitTimeoutMs` | `30_000` | Wait for a free connection, then `TimeoutException`. `-1` = forever. |
| `TableNamingConvention` | `null` (default convention) | When set, it overrides the factory's and DI's convention. |
| `AdditionalPragmas` | `[]` | Run on every new connection, e.g. `"PRAGMA temp_store = MEMORY"`, `mmap_size`. |
| `SerializerOptions` | `null` (reflection) | Must have a `TypeInfoResolver`. It is required under AOT. See aot-and-serialization.md. |

`Validate()` runs automatically in `Build()`, in the factory and in the store. It throws
`ArgumentException` with `ParamName` set to the faulty option. `Clone()` copies everything
except `SerializerOptions` and `TableNamingConvention`, which are shared by reference.

Options are **snapshotted** when handed to the factory or to DI. Mutating them afterwards
does nothing.

## The builder

```csharp
var options = DocumentStoreOptions.Builder()            // or Builder(connectionString), or new DocumentStoreOptionsBuilder()
    .UseFile("data/app.db")                             // UseInMemory() / UseSharedInMemory(name) / WithConnectionString(cs)
    .WithSynchronousMode(SynchronousMode.Normal)
    .WithWalMode(false)                                 // required for a hand-written in-memory connection string
    .WithCacheSize(-8000)                               // raw PRAGMA cache_size: >0 pages, <0 KiB
    .WithTableNamingConvention(new SimpleTypeNameConvention())
    .WithBusyTimeout(2000)
    .WithMaxPoolSize(8)
    .WithPoolWaitTimeout(10_000)
    .WithCacheSizeMb(16)                                // stores -16*1024 KiB
    .WithPageSize(4096)                                 // 0 = keep the file's
    .WithForeignKeys(true)
    .AddPragma("PRAGMA temp_store = MEMORY")            // a blank pragma is silently ignored
    .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
    .Build();                                           // InvalidOperationException if no connection string; ArgumentException if invalid
```

Shortcuts:
- `OptimizeForPerformance()` sets WAL on, Synchronous Normal, PageSize 8192 and CacheSize -4000.
  - It turns WAL **on**, so calling it after `UseInMemory()` makes `Build()` throw.
  - Its 8192 page size makes an existing 4096-page file throw `IncompatiblePageSizeException`.
- `OptimizeForSafety()` sets WAL on, Synchronous Full and foreign keys on. It conflicts with in-memory in the same way.
- `OptimizeForTesting()` is the same as `UseInMemory()`.

The builder methods are applied in call order, so the last one wins.

## Dependency injection

```csharp
public static IServiceCollection AddLiteDocumentStore(this IServiceCollection services, Action<DocumentStoreOptions> configureOptions);
public static IServiceCollection AddLiteDocumentStore(this IServiceCollection services, DocumentStoreOptions options);
public static IServiceCollection AddKeyedLiteDocumentStore(this IServiceCollection services, object serviceKey, Action<DocumentStoreOptions> configureOptions);
public static IServiceCollection AddKeyedLiteDocumentStore(this IServiceCollection services, object serviceKey, DocumentStoreOptions options);
```

```csharp
services.AddLiteDocumentStore(o =>
{
    o.ConnectionString = "Data Source=app.db";
});
```

- `IDocumentStore` is always a **singleton**. There is no lifetime parameter.
- The delegate runs **immediately** at registration, not at resolution, and the result is
  snapshotted.
- Options are **not validated at registration**. Invalid options, page-size mismatches and version
  errors throw at the first `GetRequiredService<IDocumentStore>()`. Resolve the store once at
  startup to surface them early, and run `CreateTableAsync`/`MigrateAsync` there.
- A second `AddLiteDocumentStore` call is silently ignored (`TryAdd`). Use the keyed form for
  several databases.
- `IConnectionFactory`, `ITableNamingConvention` and `IDocumentStoreFactory` are registered with
  `TryAdd`. To replace one, register yours **before** `AddLiteDocumentStore`. An `ILoggerFactory`
  in the container is picked up automatically.

Typical host startup:

```csharp
var app = builder.Build();
var store = app.Services.GetRequiredService<IDocumentStore>();   // validates and opens
await store.CreateTableAsync<Customer>();
await store.CreateBlobTableAsync();
```

## Several databases

One store per database, each with its own key:

```csharp
services.AddKeyedLiteDocumentStore("Orders", DocumentStoreOptions.ForFile("orders.db"));
services.AddKeyedLiteDocumentStore("Audit", DocumentStoreOptions.ForFile("audit.db"));

sealed class OrderService([FromKeyedServices("Orders")] IDocumentStore store) { /* ... */ }
var audit = provider.GetRequiredKeyedService<IDocumentStore>("Audit");
```

Without DI, create a store from the factory for each database and dispose each one.

## Custom connection factory

```csharp
public interface IConnectionFactory
{
    SqliteConnection CreateConnection(DocumentStoreOptions options);
    Task<SqliteConnection> CreateConnectionAsync(DocumentStoreOptions options, CancellationToken cancellationToken = default);
}
```

`DefaultConnectionFactory` is `sealed`. Decorate it by holding one and delegating, which
inherits every PRAGMA and timeout:

```csharp
internal sealed class LoggingConnectionFactory : IConnectionFactory
{
    private readonly DefaultConnectionFactory _inner = new();

    public SqliteConnection CreateConnection(DocumentStoreOptions options)
    {
        Console.WriteLine("opening connection");
        return _inner.CreateConnection(options);
    }

    public Task<SqliteConnection> CreateConnectionAsync(DocumentStoreOptions options, CancellationToken cancellationToken = default)
        => _inner.CreateConnectionAsync(options, cancellationToken);
}

await using var store = new DocumentStoreFactory(new LoggingConnectionFactory()).Create(DocumentStoreOptions.ForInMemory());
// DI: services.AddSingleton<IConnectionFactory, LoggingConnectionFactory>(); BEFORE AddLiteDocumentStore
```

To use your own `SqliteConnection` subclass, open it and call the public
`DefaultConnectionFactory.ConfigureConnection(connection, options)` (or
`ConfigureConnectionAsync`) before returning it. The store calls **only** `CreateConnection(Async)`.
A factory that configures connections itself must apply every option before returning. A
missing `foreign_keys` PRAGMA or command timeout fails silently.

## Table naming

`DefaultTableNamingConvention` (shared `DefaultTableNamingConvention.Instance`) builds the
name from the namespace, the declaring types and the type name, joined with `_`:

| Type | Table |
|---|---|
| `MyApp.Sales.Order` | `MyApp_Sales_Order` |
| `MyApp.Outer+Inner` | `MyApp_Outer_Inner` |
| `MyApp.Box<int>` | `MyApp_Box_1_System_Int32` |
| global-namespace `Order` | `Order` |

- Always get the name from `store.GetTableName<T>()` for raw SQL and migrations.
- Moving or renaming a type changes its table. Existing rows stay in the old table.
- The default convention throws `NotSupportedException` for open generics, generic parameters,
  arrays, pointers, by-ref types, types nested in a generic type, and non-ASCII names.
- Two types whose names collide, compared case-insensitively, throw `InvalidOperationException`
  on the second type's first operation. A convention that returns a name outside
  `[A-Za-z_][A-Za-z0-9_]*` throws `InvalidOperationException` too.

A custom convention, for example to keep bare type names from older releases:

```csharp
public interface ITableNamingConvention { string GetTableName<T>(); string GetTableName(Type type); }

internal sealed class SimpleTypeNameConvention : ITableNamingConvention
{
    public string GetTableName<T>() => GetTableName(typeof(T));
    public string GetTableName(Type type) => type.Name;   // MUST be deterministic for the store's lifetime
}

var options = DocumentStoreOptions.ForFile("app.db");
options.TableNamingConvention = new SimpleTypeNameConvention();
```

## Pool, timeouts, disposal and health

- Each operation rents a pooled connection. Each open transaction holds one until it ends.
  `MaxPoolSize` mainly helps read concurrency, because SQLite serializes writers anyway.
- Blob read streams have a separate budget of `MaxPoolSize`. The store can therefore hold up to
  `2 × MaxPoolSize` connections, plus 1 for in-memory.
- When the pool is exhausted, operations throw `TimeoutException` after `PoolWaitTimeoutMs`. The
  usual cause is an undisposed transaction. `-1` (infinite) hides such leaks as hangs.
- `BusyTimeoutMs` bounds lock waits but is not a hard total. Values below 1000, including 0,
  still wait about 1 s, so "fail immediately when locked" cannot be expressed. A
  `Default Timeout=` in the connection string overrides the derived command timeout.
- Disposing the store runs a WAL checkpoint (only when WAL is on) and closes the pool. A second
  dispose is a no-op. Afterwards every call throws `ObjectDisposedException`.
- `IsHealthyAsync()` returns `false` instead of throwing, including when the store is disposed.
  Use it in health checks. It rents a pooled connection, so on an exhausted pool it waits up to
  `PoolWaitTimeoutMs` (30 s) before answering `false`; pass a token from a short-lived
  `CancellationTokenSource` to bound it. A **cancelled** token is the one exception: it throws
  `OperationCanceledException` rather than reporting the store unhealthy, so catch it in the probe
  if your health framework expects a boolean.

Opening a database created elsewhere with a different page size:

```csharp
var options = DocumentStoreOptions.ForFile("existing.db");
options.PageSize = 0;   // accept the file's page size
```

## Pitfalls

- `new DocumentStoreOptions("app.db")` is accepted, but `Validate()` (run by `Build()`, the
  factory and the first DI resolution) then throws `ArgumentException`: the string is not keyword form.
- `ForFile`/`UseFile` interpolate the path unquoted, so a path containing `;` changes the
  connection string. Build it with `SqliteConnectionStringBuilder` instead.
- `Data Source=:memory:`, `file::memory:` and `Mode=Memory` without `Cache=Shared` are rejected.
  So is an empty data source.
- Hand-built in-memory options default to WAL on and are rejected. Set `EnableWalMode = false`.
- `SerializerOptions = new JsonSerializerOptions()` (no resolver) is rejected. Add
  `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` (JIT) or a source-generated context.
- Do not register the store as scoped or create one per request.
