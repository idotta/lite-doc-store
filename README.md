# LiteDocumentStore

[![CI](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml)
[![Code Quality](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml)
[![NuGet](https://img.shields.io/nuget/v/LiteDocumentStore.svg)](https://www.nuget.org/packages/LiteDocumentStore/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A high-performance, single-file application data format using C# and SQLite (Microsoft.Data.Sqlite). Objects are stored in SQLite's binary **JSONB** format and the store is Native-AOT / trim compatible.

# Core Architecture

## Primary Format
A single SQLite .db file acting as an "Application File Format".

## Data Storage Strategy
Treat SQLite as a hybrid relational/document store. JSON data is stored in **JSONB format** (binary JSON introduced in SQLite 3.45+) for optimal storage efficiency and query performance.

## Data Access Layer
Raw ADO.NET over `Microsoft.Data.Sqlite` — parameters bound explicitly, results read by ordinal (no ORM, no runtime reflection or IL generation), so the library stays Native-AOT / trim safe.

## Custom Logic
Automatic JSON serialization/deserialization of C# objects into SQLite BLOB columns using JSONB format.

## Performance Requirements
- Minimize System Calls: The design must utilize SQLite's ability to be up to 35% faster than raw file I/O for small blobs by reducing open() and close() operations.
- Transaction Batching: Writes should be grouped into transactions to maintain high write speed.
- Async Operations: All database operations are async for optimal performance and scalability.
- JSONB Format: Uses SQLite's JSONB format for binary-optimized JSON storage to eliminate repetitive parsing overhead.

## Configuration
The library defaults to WAL (Write-Ahead Logging) mode and synchronous = NORMAL for optimal balance between safety and performance.

# Usage

## Installation

Build the project:
```bash
dotnet build
```

## Requirements

- .NET 10
- SQLite 3.45+ (for JSONB support)

## Features

- ✅ **Document Store API** (`IDocumentStore`): Type-safe CRUD operations with automatic table naming
- ✅ **Async/Await**: All database operations are fully async
- ✅ **JSONB Format**: Uses SQLite 3.45+ JSONB for binary-optimized JSON storage
- ✅ **Virtual Columns**: Index JSON properties for up to 1,300x faster queries
- ✅ **Transaction Support**: Batch operations for high-performance writes
- ✅ **WAL Mode**: Automatically configured for optimal concurrency
- ✅ **Zero SQL Injection Risk**: Table names derived from types, not user input
- ✅ **Cross-Platform**: Works on Windows, Linux, and macOS
- ✅ **.NET 10**: Built on the latest .NET platform
- ✅ **Native-AOT / Trim Compatible**: `<IsAotCompatible>true</IsAotCompatible>`; serialization goes through `System.Text.Json` `JsonTypeInfo<T>`
- ✅ **Comprehensive Tests**: Unit and integration tests with xUnit

## Quick Start

Register the store through dependency injection and resolve `IDocumentStore`:

```csharp
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLiteDocumentStore(options =>
{
    options.ConnectionString = "Data Source=app.db";
    options.EnableWalMode = true;
    // For Native-AOT, supply source-generated metadata:
    // options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = MyJsonContext.Default };
});

await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IDocumentStore>();

await store.CreateTableAsync<Customer>();
await store.UpsertAsync("c1", new Customer { Name = "Ada", Email = "ada@example.com" });

var customer = await store.GetAsync<Customer>("c1");

// Query documents by JSON path + value
var byName = await store.QueryAsync<Customer, string>("$.Name", "Ada");

// For ranges, joins, or virtual-column seeks, drop to raw SQL via the escape hatch:
var conn = store.Connection; // Microsoft.Data.Sqlite SqliteConnection
```

Without DI, build the store via `IDocumentStoreFactory.CreateAsync(DocumentStoreOptions)`.

## Dependencies

- .NET 10
- Microsoft.Data.Sqlite
- Microsoft.Extensions.DependencyInjection.Abstractions / Logging.Abstractions

## JSONB Storage Benefits

The library uses SQLite's JSONB functions introduced in version 3.45+:
- More compact storage (binary format)
- Faster queries on JSON data
- Reduced parsing overhead
- Compatible with SQLite's JSON functions

## CI/CD

This project uses GitHub Actions for continuous integration and deployment:

- **Continuous Integration**: Automated builds and tests on every push and PR
- **Multi-platform Testing**: Tests run on Ubuntu, Windows, and macOS
- **Code Quality**: Automated code analysis, formatting checks, and security scans
- **NuGet Publishing**: Automated package publishing on GitHub releases
- **Dependency Updates**: Dependabot keeps dependencies up to date

See [.github/WORKFLOWS.md](.github/WORKFLOWS.md) for detailed CI/CD documentation.

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Ensure all tests pass: `dotnet test`
5. Submit a pull request

CI will automatically validate your changes.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
