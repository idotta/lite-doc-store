# Querying and patching

## Contents
- [JSON paths](#json-paths)
- [Simple equality query](#simple-equality-query)
- [DocumentQuery&lt;T&gt;](#documentqueryt)
- [Operators and values](#operators-and-values)
- [Dates](#dates)
- [Count, exists and delete by query](#count-exists-and-delete-by-query)
- [DocumentPatch&lt;T&gt;](#documentpatcht)
- [Pitfalls](#pitfalls)

All of these are on `IDocumentOperations` (the store and transactions). There is **no LINQ
predicate query and no `SelectAsync` projection**. Those were removed for AOT. Use
`DocumentQuery<T>`, or `ExecuteRawAsync` for joins, aggregates, OR logic and projections.

## JSON paths

Query, ordering and patch paths are **strings, used verbatim**. They must name the key that
System.Text.Json actually wrote.

| Serializer config | CLR property | Path to write |
|---|---|---|
| default | `Email` | `$.Email` |
| `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` | `Email` | `$.email` |
| `JsonNamingPolicy.KebabCaseLower` | `FullName` | `$.full-name` |
| `[JsonPropertyName("mail")]` | `Email` | `$.mail` |

A wrong name fails silently. A query matches nothing, and a patch `Set` adds a new stray field
and bumps the version.

Grammar: `$` followed by segments:
- `.member` (nested: `$.Address.City`)
- `[index]` (array element: `$.Tags[0]`)
- `."quoted member"` for a key containing `.` or `[`, and for the empty key: `$."a.b"`, `$.""`.
  In C#: `"$.\"a.b\""`. Inside quotes, only `\"` and `\\` are valid escapes.

Mistakes and what they do:
- `$.a.b` means nested `a` -> `b`, **not** the key `"a.b"`. The query returns nothing, `Set`
  writes a new nested object, and `Remove` does nothing. All of them report success.
- These are refused with `ArgumentException`:
  - an empty path, or a bare `$.`;
  - an apostrophe or `U+0000` anywhere;
  - a `"` inside a member that needs quotes (contains `.` or `[`, is empty, or starts with `"`).

  A key containing an apostrophe is reachable through `ExecuteRawAsync` with the path bound as a
  parameter. A key containing `U+0000`, or a quoted key containing `"`, cannot be addressed at
  all: binding the path silently reads a different key.
- `$` (the root) is allowed in queries and ordering, but refused in patches and index DDL.
- Redundant quotes are dropped: `$."Name"` is the same as `$.Name`.

## Simple equality query

```csharp
Task<IEnumerable<T>> QueryAsync<T, TValue>(string jsonPath, TValue value, CancellationToken ct = default);

var inSeattle = await store.QueryAsync<Customer, string>("$.City", "Seattle");
```

`value` must be non-null. Use `DocumentQuery<T>.WhereIsNull` for null checks.

## DocumentQuery&lt;T&gt;

```csharp
Task<IEnumerable<T>> QueryAsync<T>(DocumentQuery<T> query, CancellationToken ct = default);
Task<long> CountAsync<T>(DocumentQuery<T> query, CancellationToken ct = default);
Task<bool> ExistsAsync<T>(DocumentQuery<T> query, CancellationToken ct = default);
Task<int> DeleteAsync<T>(DocumentQuery<T> query, CancellationToken ct = default);
```

Builder (immutable; every call returns a **new** instance):

| Start (static) | Extend (instance) |
|---|---|
| `All()` | |
| `Where(path, QueryOperator op, object? value)` | `And(path, op, value)` |
| `WhereIsNull(path)` / `WhereIsNotNull(path)` | `AndIsNull(path)` / `AndIsNotNull(path)` |
| `WhereIn(path, IEnumerable<object?> values)` | `AndIn(path, values)` |
| `WhereArrayContains(path, object value)` | `AndArrayContains(path, value)` |
| | `OrderBy(path, bool descending = false)`. Call it repeatedly for tie-breakers. |
| | `Skip(int offset)` (>= 0) / `Take(int limit)` (>= 1) |

```csharp
var q = DocumentQuery<Customer>
    .Where("$.Age", QueryOperator.GreaterThanOrEqual, 30)
    .And("$.City", QueryOperator.Like, "S%")
    .AndIsNotNull("$.Email")
    .AndArrayContains("$.Tags", "vip")
    .OrderBy("$.Age", descending: true)
    .OrderBy("$.Name")
    .Skip(20).Take(10);

var page = await store.QueryAsync(q);

// Conditional composition: reassign, because the builder is immutable
var query = DocumentQuery<Customer>.All();
if (city is not null) query = query.And("$.City", QueryOperator.Equal, city);
```

Predicates combine with **AND only**. For OR on one field use `WhereIn`. For anything else,
run two queries or use raw SQL.

## Operators and values

| `QueryOperator` | Value | Notes |
|---|---|---|
| `Equal`, `NotEqual` | non-null scalar | `NotEqual` uses SQL `<>`, so rows where the path is null or absent are **not** returned |
| `GreaterThan(OrEqual)`, `LessThan(OrEqual)` | non-null scalar | See Dates |
| `Like` | string | `%` and `_`; ASCII case-insensitive |
| `Glob` | string | `*` and `?`; case-sensitive |
| `In` | only through `WhereIn`/`AndIn` | At least 1 value, no null elements. `Where(..., In, ...)` throws. |
| `IsNull` / `IsNotNull` | `null` | `IsNull` matches JSON null **and** an absent key |
| `ArrayContains` | non-null scalar | The JSON array at the path contains the value |

Bindable value types for `DocumentQuery<T>` and `DocumentPatch<T>`: `string`, `bool`, all integral types, `float`,
`double`, `decimal`, `DateTime`, `DateTimeOffset`, `Guid`, `byte[]` and **enums**. Objects and
collections throw `ArgumentException` when the query is built; NaN and Infinity throw too (also on
`QueryAsync<T, TValue>`).

**How a value is bound.** When the query or patch runs, the store resolves the path through the
store's `SerializerOptions` metadata for `T` and serializes a value **of the member's own type**
through it. So bind values as your C# type and let the store match the stored form:
- Enums work whether stored as numbers (default) or names (`JsonStringEnumConverter`,
  `UseStringEnumConverter` on a source-generated context, or `[JsonConverter]` on the property).
  A patch writes the enum the same way.
- A custom scalar converter (e.g. `DateTime` as epoch millis) is honoured for equality and patches.
- `NumberHandling.WriteAsString`, naming policies (paths use the serialized name) work. Under
  `WriteAsString`, ranges and `OrderBy` compare numbers as text (`"10" < "9"`).

Fallback, when the path does not resolve (typo, a key only a derived type writes,
`Dictionary<string, object>`, `object` members) or the value's type differs from the member's
(an `int` for a `long` member): the value is bound as default STJ would write it — `DateTime` as ISO
text, `Guid` as text, `byte[]` as base64, `decimal` as a number. **An enum on the fallback throws
`ArgumentException`** (`ParamName` `query`, `value` or `patch`), because its stored form is unknown; bind
the stored form (`(int)s` or the name) there. A range (`>`, `<`, ...) over an enum stored as its name
throws: names do not sort by value. A converter that writes an object/array cannot be compared.

A query may bind at most 900 parameters, counting large `In` lists. Beyond that it throws
`ArgumentException`, so split the query.

## Dates

- **Range operators refuse a `DateTime` of kind `Utc` or `Local`, and any `DateTimeOffset`**,
  with `ArgumentException` at build time, because the stored ISO text does not sort
  chronologically. Persist instants as `long` (Ticks or Unix ms) and range over that:

  ```csharp
  public sealed record Event(string Id, DateTime At, long AtTicks);
  var since = DocumentQuery<Event>.Where("$.AtTicks", QueryOperator.GreaterThanOrEqual,
      new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
  ```

- An `Unspecified`-kind `DateTime` range is allowed and sorts correctly.
- Equality, `In` and `ArrayContains` on any `DateTime`/`DateTimeOffset` work.
- `OrderBy` over a `DateTime`/`DateTimeOffset` property (nullable included) is chronological,
  exact to the tick, with offsets applied. No expression index serves that ordering, though.
  `Unspecified` kind is ordered as UTC. A path written by a custom converter falls back to plain
  text or number order.

## Count, exists and delete by query

- `CountAsync(query)` and `ExistsAsync(query)` apply **predicates only**. `OrderBy`, `Skip` and
  `Take` are ignored, so the count of a paged query is the total number of matches.
- `DeleteAsync(query)` **honours paging**. It returns the number of rows deleted.
  - Unpaged: deletes every match.
  - `OrderBy(...).Take(n)`: deletes exactly that page. This is the retention pattern.
  - Paging **without** `OrderBy`: deletes an unspecified page. Always order a paged delete.

```csharp
int purged = await store.DeleteAsync(DocumentQuery<Stamp>.All().OrderBy("$.At").Take(1000)); // oldest 1000
int gone   = await store.DeleteAsync(DocumentQuery<Widget>.Where("$.Quantity", QueryOperator.GreaterThanOrEqual, 30));
```

## DocumentPatch&lt;T&gt;

```csharp
Task<long> PatchAsync<T>(string id, DocumentPatch<T> patch, CancellationToken ct = default);
Task<long> PatchWithVersionAsync<T>(string id, DocumentPatch<T> patch, long expectedVersion, CancellationToken ct = default);

DocumentPatch<T>.Set(string jsonPath, object? value)   .AndSet(path, value)
DocumentPatch<T>.Remove(string jsonPath)               .AndRemove(path)
```

A patch changes named fields in **one statement with one version bump**, and returns the new
stored version. Prefer it over read-modify-`UpsertAsync`, which reverts concurrent edits to
fields you did not touch.

```csharp
using LiteDocumentStore.Exceptions;

long version = await store.PatchAsync("a1", DocumentPatch<Account>
    .Set("$.Balance", 310.20m)            // exact JSON number
    .AndSet("$.Frozen", true)             // JSON true, not 1
    .AndSet("$.LastSeenAt", new DateTime(2024, 6, 1))
    .AndRemove("$.Nickname"));            // removes the key

var cur = (await store.GetWithVersionAsync<Account>("a1"))!;
await store.PatchWithVersionAsync("a1", DocumentPatch<Account>.Set("$.Frozen", false), cur.Version);

try { await store.PatchAsync("nobody", DocumentPatch<Account>.Set("$.Owner", "x")); }
catch (ConcurrencyException ex) when (ex.Kind == ConcurrencyConflictKind.DocumentNotFound) { /* patches never insert */ }
```

Rules:
- **Values are scalars only**, the same types as queries, and they need no `JsonTypeInfo`.
  Nested objects and arrays must be written with `UpsertAsync` or raw SQL using `jsonb_set`.
- `Set(path, null)` writes JSON `null`, and the key stays present. `Remove(path)` deletes the key.
- `Set` creates a missing object key, including missing intermediate objects (`$.a.b` on `{}`
  gives `{"a":{"b":…}}`), and an index equal to the array length appends. It silently does
  nothing, while still returning success and bumping the version, when the index is further past
  the end (`$.Tags[5]` on a 2-element array) or a parent along the path is a scalar. There is no
  `[#]` append (rejected); to grow an array arbitrarily, rewrite the document. A patch on a missing **id** throws `ConcurrencyException`
  (`DocumentNotFound`). It never inserts.
- The path must be below the root. `Set("$", ...)` and `Remove("$")` throw. `$[0]` is fine.
- Each path may appear once per patch. A duplicate `Set`, or a `Set` plus a `Remove` of the same
  path, throws `ArgumentException`.
- All sets apply before all removes. Within each group, related paths apply in call order, so
  removing `$.Items[0]` shifts `$.Items[1]`.
- A patch allows at most 499 sets and 999 removes. Beyond that, `ArgumentException` is thrown at
  `PatchAsync`. Split the patch.
- Patch paths are verbatim serialized keys, like query paths.

## Pitfalls

- `q.And(...);` without reassigning drops the predicate. The same goes for `AndSet` and
  `AndRemove`.
- `Equal`/`NotEqual` with `null` throws. Use `IsNull`/`IsNotNull`. Passing a value to `IsNull`
  throws too.
- `Take(0)`, a negative `Take` or a negative `Skip` throws `ArgumentOutOfRangeException`.
- An index is used only when the query path text equals the indexed path. Query the same
  serialized path the index was built over.
- `GetAllAsync` is unordered. Use `DocumentQuery<T>.All().OrderBy(...)` when order matters.
- Calling these on the store inside a transaction block does not enlist. Call them on `tx`.
- `OrderBy` on a string path is ordinal/BINARY (uppercase before lowercase), not culture- or
  case-insensitive. For other orderings use `ExecuteRawAsync` with
  `ORDER BY json_extract(...) COLLATE NOCASE`.
- `Like` has no escape character. Strip or reject `%` and `_` in user input, or use `Glob` with
  `[%]`/`[_]` character classes.
