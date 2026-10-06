# Indexing and schema

## Contents
- [Signatures](#signatures)
- [Expression vs string paths](#expression-vs-string-paths)
- [Creating indexes](#creating-indexes)
- [IndexOptions and IndexFilter](#indexoptions-and-indexfilter)
- [Index names and re-creation](#index-names-and-re-creation)
- [Dropping indexes](#dropping-indexes)
- [Virtual columns](#virtual-columns)
- [Verifying an index is used](#verifying-an-index-is-used)
- [SchemaIntrospector](#schemaintrospector)
- [Pitfalls](#pitfalls)

All DDL is on `IDocumentOperations` (store and transaction). Call `CreateTableAsync<T>()`
first. The index DDL does not create the table.

## Signatures

```csharp
Task CreateIndexAsync<T>(Expression<Func<T, object>> jsonPath, string? indexName = null, CancellationToken ct = default);
Task CreateIndexAsync<T>(string jsonPath, string? indexName = null, CancellationToken ct = default);
Task CreateIndexAsync<T>(Expression<Func<T, object>> jsonPath, string? indexName, IndexOptions options, CancellationToken ct = default);
Task CreateIndexAsync<T>(string jsonPath, string? indexName, IndexOptions options, CancellationToken ct = default);

Task CreateCompositeIndexAsync<T>(Expression<Func<T, object>>[] jsonPaths, string? indexName = null, CancellationToken ct = default);
Task CreateCompositeIndexAsync<T>(string[] jsonPaths, string? indexName = null, CancellationToken ct = default);
Task CreateCompositeIndexAsync<T>(Expression<Func<T, object>>[] jsonPaths, string? indexName, IndexOptions options, CancellationToken ct = default);
Task CreateCompositeIndexAsync<T>(string[] jsonPaths, string? indexName, IndexOptions options, CancellationToken ct = default);

Task AddVirtualColumnAsync<T>(Expression<Func<T, object>> jsonPath, string columnName, bool createIndex = false, string columnType = "TEXT", CancellationToken ct = default);
Task AddVirtualColumnAsync<T>(string jsonPath, string columnName, bool createIndex = false, string columnType = "TEXT", CancellationToken ct = default);

Task DropIndexAsync(string indexName, CancellationToken ct = default);                        // DROP INDEX IF EXISTS
Task DropIndexAsync<T>(Expression<Func<T, object>> expression, CancellationToken ct = default); // drops the DERIVED name only
```

An index is `json_extract(data, '<path>')` on T's table.

## Expression vs string paths

- **Expression overloads** (`x => x.Address.City`) resolve each member through
  `SerializerOptions`, so they index the **serialized** key, honouring naming policies and
  `[JsonPropertyName]`.
  - They throw `ArgumentException` for a `[JsonIgnore]`, `[JsonExtensionData]` or unserialized
    member, or for a type missing from a source-generated context. This prevents an index that
    is NULL in every row.
  - The expression must be a member chain rooted at the lambda parameter. Value-type members
    are fine (the boxing is unwrapped). Use `x => x.Email!` for nullable reference members.
- **String overloads** use the path verbatim and are never checked against the documents. A
  path absent from the rows indexes NULL everywhere, and a `Unique` one then accepts every
  duplicate, because NULLs are distinct (a wrong path that hits another key constrains that key
  instead). Use them for array elements (`$.Tags[0]`), quoted keys (`$."a.b"`), or
  keys not written by T's serializer.
- The root `$` is refused by all index and virtual-column DDL.

Prefer the expression overloads whenever a property exists.

## Creating indexes

```csharp
await store.CreateTableAsync<Customer>();

await store.CreateIndexAsync<Customer>(c => c.Email!, "idx_customer_email");
await store.CreateIndexAsync<Customer>(c => c.City, "idx_customer_city");               // nested chains work too: x => x.Address.City -> $.Address.City
await store.CreateCompositeIndexAsync<Customer>([c => c.City, c => c.Age], "idx_customer_city_age");

// String paths: array element / non-identifier key -> an explicit name is REQUIRED
await store.CreateIndexAsync<Product>("$.Tags[0]", "idx_product_first_tag");
await store.CreateIndexAsync<Person>("$.full-name", "idx_person_full_name");
await store.CreateIndexAsync<Doc>("$.\"a.b\"", "idx_doc_dotted");
```

Run these at startup, after `CreateTableAsync`. Re-running with the identical definition is a
no-op. Put versioned schema changes in a migration instead (see migrations.md).

## IndexOptions and IndexFilter

```csharp
public sealed class IndexOptions
{
    public bool Unique { get; init; }
    public string? Collation { get; init; }   // e.g. "NOCASE"; must be an identifier
    public bool Descending { get; init; }
    public IndexFilter? Filter { get; init; } // partial index WHERE
}

IndexFilter.IsNull(path) / IndexFilter.IsNotNull(path)   .AndIsNull(path) / .AndIsNotNull(path)
```

```csharp
await store.CreateIndexAsync<Customer>(
    c => c.Email,
    "idx_customer_email_unique",          // indexName is positional here: pass null to derive
    new IndexOptions
    {
        Unique = true,
        Collation = "NOCASE",
        Filter = IndexFilter.IsNotNull("$.Email").AndIsNull("$.DeletedAt"),
    });
```

- `Unique` makes a violating write throw an untranslated `SqliteException` ("UNIQUE constraint
  failed"). Creating a unique index over existing duplicates throws the same way.
- `Collation` affects uniqueness, for example making case variants duplicates. The library's
  queries compare with BINARY, so a NOCASE index usually does **not** serve `QueryAsync` lookups.
  Check the query plan.
- On a composite index, `Collation` and `Descending` apply to **every** column. Mixed
  per-column directions need raw SQL.
- `IndexFilter` is value-free by design (IS NULL / IS NOT NULL, AND only), because SQLite
  forbids bound parameters in a partial index. A filter such as `status = 'active'` must be
  created through `ExecuteRawAsync`.
- `IndexFilter` is immutable. Discarding the return value of `AndIsNull` loses the term.

## Index names and re-creation

With `indexName: null`, the name is derived:
- single path: `idx_{table}_{path}_{6-hex digest}`, e.g. `idx_Customer_Email_223fe7`
- composite: `idx_{table}_composite_{p1}_{p2}_{digest}`
- virtual-column index: `idx_{table}_{column}_{digest}`

Do not hand-compute derived names. **Pass an explicit name** whenever you will refer to the
index later (raw SQL, `IndexExistsAsync`, `DropIndexAsync(string)`).

No name can be derived for:
- array indexers (`$.Tags[0]`);
- quoted members (`$."a.b"`);
- keys whose folded form is not an identifier (`$.full-name`).

These throw `ArgumentException` unless you pass a name.

Re-creating a name:
- **Identical definition**: a no-op.
- **Different definition** (other options, path, or another index holding the name): throws
  `InvalidOperationException` showing both definitions.

To change an index's options, `DropIndexAsync(name)` first, then create it again.

## Dropping indexes

- `DropIndexAsync("name")` drops any index by name. Use it for explicit names, composite indexes
  and virtual-column indexes.
- `DropIndexAsync<T>(x => x.Email)` drops only the name that `CreateIndexAsync<T>(x => x.Email)`
  with **no explicit name** would derive.
- Both are `IF EXISTS`, so a wrong name succeeds silently and drops nothing.

Databases from releases before 0.7.0 used different derived names. There,
`DropIndexAsync<T>(expr)` drops nothing, and `CreateIndexAsync<T>(expr)` adds a **second** index
over the same path. List the old names and drop them by name:

```sql
SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx\_%' ESCAPE '\';
```

## Virtual columns

A virtual column is a generated column `GENERATED ALWAYS AS (json_extract(data, '<path>')) VIRTUAL`.
Optionally indexed, it gives raw SQL a real column name to seek and range-scan on.

```csharp
await store.AddVirtualColumnAsync<Product>(p => p.Category, "category", createIndex: true);
await store.AddVirtualColumnAsync<Product>(p => p.Price, "price", createIndex: true, columnType: "REAL");

var table = store.GetTableName<Product>();
var expensive = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT json(data) FROM [{table}] WHERE [price] > @min ORDER BY [price]";
    cmd.Parameters.AddWithValue("@min", 100.0);
    var rows = new List<Product>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) rows.Add(store.DeserializeDocument<Product>(r.GetString(0))!);
    return rows;
});
```

- `columnType` must be `TEXT`, `INTEGER`, `REAL`, `BLOB` or `NUMERIC`. Use `REAL` or `INTEGER`
  for numbers. A `TEXT` column compares numbers as strings, which silently breaks ranges.
- The column must be a valid identifier.
- If a column with that name already exists (compared ignoring ASCII case), the call is a no-op
  only when it is the **identical** generated column (same path, same `columnType`). Anything
  else — another path or type, a plain column, or an equivalent column a migration spelled
  differently — throws `InvalidOperationException` before any DDL runs. To change a virtual
  column, drop it first (`ALTER TABLE ... DROP COLUMN` via `ExecuteRawAsync`, after dropping its
  index) or use a migration.
- The column's index serves queries on the **column** only, not `json_extract(...)` queries,
  and vice versa. Query the column by name in raw SQL.

## Verifying an index is used

SQLite uses an expression index only when the query expression is textually identical:
`json_extract(data, '$.Email')`. `QueryAsync` and `DocumentQuery` emit exactly that. Raw SQL must
spell the path the same way: `'$.Email'`, not `'$.email'` and not `'$."Email"'`.

```csharp
var plan = await store.ExecuteRawAsync(async (conn, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"EXPLAIN QUERY PLAN SELECT id FROM [{store.GetTableName<Customer>()}] " +
                      "WHERE json_extract(data, '$.Email') = 'a@b.c'";
    var details = new List<string>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) details.Add(r.GetString(3));   // 'detail' column
    return string.Join(" | ", details);
});
// Good: "SEARCH ... USING INDEX idx_customer_email". Bad: "SCAN <table>".
```

## SchemaIntrospector

`new SchemaIntrospector(SqliteConnection)` needs a live connection, which you get only inside
`ExecuteRawAsync` or a migration's `UpAsync`/`DownAsync`. Do not keep it beyond the callback.

```csharp
Task<IEnumerable<TableInfo>> GetTablesAsync(CancellationToken ct = default);        // Name, Sql
Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);
Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default); // ColumnId, Name, Type, NotNull, DefaultValue, IsHidden
Task<IEnumerable<IndexInfo>> GetIndexesAsync(string? tableName = null, CancellationToken ct = default); // Name, TableName, Sql
Task<bool> IndexExistsAsync(string indexName, CancellationToken ct = default);
Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken ct = default);
Task<string> GetSqliteVersionAsync(CancellationToken ct = default);
Task<DatabaseStatistics> GetDatabaseStatisticsAsync(CancellationToken ct = default); // PageCount, PageSize, DatabaseSizeBytes
```

```csharp
var table = store.GetTableName<Customer>();
var report = await store.ExecuteRawAsync(async (conn, ct) =>
{
    var s = new SchemaIntrospector(conn);
    var indexes = (await s.GetIndexesAsync(table, ct)).ToList();
    var hasCity = await s.ColumnExistsAsync(table, "city", ct);   // case-insensitive
    var stats = await s.GetDatabaseStatisticsAsync(ct);
    return (indexes.Count, hasCity, stats.DatabaseSizeBytes);
});
```

- Do several introspection calls in **one** callback. Each `ExecuteRawAsync` opens a fresh
  connection.
- The listings exclude `sqlite_%` internals but include store-owned tables (`__store_blobs`,
  `__store_migrations`).
- Virtual columns appear with `IsHidden = true`.
- Name lookups ignore ASCII case, as SQLite does. Still prefer the exact name from
  `store.GetTableName<T>()` or the explicit index name. `GetColumnsAsync`/`ColumnExistsAsync` on
  a missing table return empty/`false`; they do not throw.

## Pitfalls

- An index over the CLR name when a naming policy is active is useless. Use the expression
  overload, or the serialized string path.
- The options overloads have no default for `indexName`. Write `CreateIndexAsync<T>(x => x.A, null, opts)`.
- An `IndexOptions` XML remark that says re-creating an existing name "skips" it is stale. A
  differing definition throws.
- DDL on a missing table throws a raw `SqliteException`.
