# Code Style Instructions

## C# Conventions

### General Style

- Use file-scoped namespaces
- Use `var` when the type is obvious from the right side
- Prefer expression-bodied members for simple one-liners
- Use `sealed` on classes not designed for inheritance
- Mark fields as `readonly` when possible

### Naming Conventions

```csharp
// Classes, interfaces, methods, properties: PascalCase
public class DocumentStore { }
public interface IDocumentStore { }
public async Task<T?> GetAsync<T>(string id) { }

// Private fields: _camelCase with underscore prefix
private readonly SqliteConnection _connection;

// Parameters and locals: camelCase
public void Configure(string databasePath) { }

// Constants: PascalCase
public const int DefaultBatchSize = 1000;
```

### Async Patterns

```csharp
// Always use Async suffix for async methods
public async Task<T?> GetAsync<T>(string id)

// Use ConfigureAwait(false) in library code
await _connection.ExecuteAsync(sql).ConfigureAwait(false);

// Prefer ValueTask for hot paths that often complete synchronously
public ValueTask DisposeAsync()
```

### Null Handling

```csharp
// Use nullable reference types
public async Task<T?> GetAsync<T>(string id)

// Use null-coalescing and null-conditional operators
return json ?? throw new InvalidOperationException();
parameter?.Value ?? DBNull.Value;

// Validate arguments with throw expressions
_connection = connection ?? throw new ArgumentNullException(nameof(connection));
```

## SQL Conventions

### Table and Column Names

```sql
-- Use snake_case for SQL identifiers
CREATE TABLE [Customer] (
    id TEXT PRIMARY KEY,
    data BLOB NOT NULL
);

-- Always bracket table names (derived from type names)
SELECT * FROM [{tableName}]
```

### JSONB Operations

```sql
-- Write: Always convert to JSONB
INSERT INTO [Customer] (id, data) VALUES (@Id, jsonb(@Data))

-- Read: Always convert back to JSON text
SELECT json(data) as data FROM [Customer] WHERE id = @Id

-- Query JSON fields
SELECT * FROM [Customer] WHERE json_extract(data, '$.email') = @Email
```

### Parameterization

```csharp
// ALWAYS use parameters, never string concatenation.
// Use the raw ADO.NET helpers in Core/SqliteCommandExtensions.cs (bind by name):
var sql = "SELECT json(data) FROM [Customer] WHERE id = @Id";
var json = await _connection.QueryFirstStringAsync(sql, ("Id", id));

// NEVER do this
var sql = $"SELECT * FROM [Customer] WHERE id = '{id}'"; // SQL INJECTION!
```

## Documentation Style

```csharp
/// <summary>
/// Brief description of what the method does.
/// </summary>
/// <typeparam name="T">Description of type parameter</typeparam>
/// <param name="id">Description of parameter</param>
/// <returns>Description of return value</returns>
/// <exception cref="ArgumentNullException">When id is null</exception>
/// <example>
/// <code>
/// var customer = await repo.GetAsync&lt;Customer&gt;("cust-123");
/// </code>
/// </example>
public async Task<T?> GetAsync<T>(string id)
```

## Error Handling

```csharp
// Validate early, fail fast
if (string.IsNullOrEmpty(id))
    throw new ArgumentException("ID cannot be null or empty", nameof(id));

// Preserve exception context in transactions
catch (Exception)
{
    transaction.Rollback();
    throw; // Re-throw, don't wrap unnecessarily
}
```
