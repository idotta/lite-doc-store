# Native AOT and serialization

## Contents
- [How documents are serialized](#how-documents-are-serialized)
- [Native AOT setup](#native-aot-setup)
- [Naming policies and paths](#naming-policies-and-paths)
- [What is not available](#what-is-not-available)
- [Pitfalls](#pitfalls)

## How documents are serialized

Documents go through System.Text.Json using `DocumentStoreOptions.SerializerOptions`. Those
options are kept by reference, not copied.

| `SerializerOptions` | Result |
|---|---|
| `null` | Reflection fallback. Works on JIT. **Refused under Native AOT** (`ArgumentException`, ParamName `SerializerOptions`). |
| `new JsonSerializerOptions()` with no resolver | **Refused everywhere** with the same exception |
| `new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), ... }` | JIT with custom settings (naming policy, converters) |
| `new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default, ... }` | AOT- and trim-safe |

The same options drive both serialization and the expression-to-path resolution used by the
index DDL.

## Native AOT setup

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDocumentStore;

var options = DocumentStoreOptions.Builder()
    .UseFile("app.db")
    .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
    .Build();

builder.Services.AddLiteDocumentStore(options);

// ...
await store.CreateTableAsync<Person>();
await store.UpsertAsync("p1", new Person("p1", "Ada", "ada@x.io", 36));
await store.CreateIndexAsync<Person>(p => p.Email);                    // walks member names only
await store.PatchAsync("p1", DocumentPatch<Person>.Set("$.Age", 37));  // scalar patch values need no JsonTypeInfo

public sealed record Person(string Id, string Name, string Email, int Age);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Person))]
// [JsonSerializable(typeof(Order))] ... one entry per document type
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

csproj: `<PublishAot>true</PublishAot>`. The library itself raises no `IL2xxx`/`IL3xxx` warnings.

- **Register every document type** with `[JsonSerializable(typeof(T))]`. A missing type fails at
  runtime with `DocumentSerializationException` (a type-metadata failure). On the
  expression-index APIs it fails with `ArgumentException`.
- **Trimmed builds without AOT** (`PublishTrimmed` on JIT) are not detected. A null
  `SerializerOptions` is accepted there, but the reflection fallback can silently lose trimmed
  members. Supply a source-generated context for trimmed deployments too.
- The library has no OpenSSL dependency, so it runs on distroless and chiseled images.

## Naming policies and paths

A naming policy or `[JsonPropertyName]` changes the stored keys. String paths must follow them:

```csharp
var opts = DocumentStoreOptions.ForFile("app.db");
opts.SerializerOptions = new JsonSerializerOptions
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),     // or a source-generated context
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

await store.QueryAsync<Person, string>("$.email", "ada@x.io");            // not $.Email
await store.CreateIndexAsync<Person>(p => p.Email);                         // resolves to $.email automatically
```

- For a source-generated context, set the policy with
  `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]`.
- Changing the naming policy on an existing database leaves the old documents with the old keys.
  Their indexes and queries stop matching. Treat a policy change as a data migration.
- Query and patch values are bound through the same metadata the documents were written with,
  so converters (string enums, custom date formats) are honoured on any path the metadata
  resolves. Two cases fall back to STJ's default formats: a path the metadata cannot resolve,
  and a value whose type differs from the member's (an `int` for a `long` member). An enum on
  that fallback is refused.
- Enums are stored as numbers by default. Add `JsonStringEnumConverter<T>` (AOT-safe), or
  `UseStringEnumConverter = true` on the source-generated context, to store names; query with the
  enum value itself either way. An enum stored as a name supports only `Equal`/`In`; a range
  over it throws.

## What is not available

These APIs do not exist. Do not invent them:
- LINQ-predicate queries (`QueryAsync<T>(x => x.Age > 30)`).
- `SelectAsync` projections.
- A `Connection` property on the store.
- `DefaultDocumentStoreFactory`.

Instead:
- filtering: `DocumentQuery<T>` or `QueryAsync<T, TValue>(path, value)`;
- projections, joins, aggregates and OR logic: `ExecuteRawAsync`;
- the remaining expression APIs (`CreateIndexAsync`, `CreateCompositeIndexAsync`,
  `DropIndexAsync<T>`, `AddVirtualColumnAsync`), which only read member names and are AOT-safe.

## Pitfalls

- `GetAsync<T>` on an existing row for a `T` not in the context throws
  `DocumentSerializationException`; a missing id still returns null, so the gap can go unnoticed
  until data exists.
- `CorruptDataException` and `DocumentSerializationException` are unrelated siblings. Catch both
  if needed.
- `DeserializeDocument<T>("null")` returns `default` for reference types, but throws for
  non-nullable value types.
