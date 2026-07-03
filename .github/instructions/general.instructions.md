# LiteDocumentStore Project Instructions

## Project Overview

LiteDocumentStore is a high-performance, hybrid SQLite library for .NET that combines:
- **Document store convenience**: JSON/JSONB storage with automatic serialization
- **Relational database power**: Full SQL access, joins, indexes, and traditional tables

The goal is NOT an opaque document database. Users should seamlessly mix document-style and relational patterns.

## Core Technologies

- **.NET 10** - Target framework
- **SQLite 3.45+** - Required for JSONB support
- **Microsoft.Data.Sqlite** - SQLite provider (raw ADO.NET; no ORM)
- **System.Text.Json** - AOT-safe serializer via `JsonTypeInfo<T>`

## Key Architectural Principles

1. **Performance First**: Design for SQLite's strength (35% faster than raw file I/O for small blobs)
2. **Async Everything**: All database operations must be async
3. **JSONB Format**: Always use `jsonb()` on write, `json()` on read
4. **WAL Mode**: Default to WAL + synchronous=NORMAL
5. **Zero SQL Injection**: Table names from types, all queries parameterized
6. **Hybrid Philosophy**: Never prevent raw SQL or relational patterns

## Project Structure

```
src/
├── LiteDocumentStore/                    # Main library
│   ├── Core/DocumentStore.cs             # Core store implementation
│   ├── Core/SqliteCommandExtensions.cs   # Raw ADO.NET helpers (replaced Dapper)
│   └── Serialization/JsonHelper.cs       # AOT-safe JSON serialization
└── tests/
    ├── LiteDocumentStore.UnitTests/      # Unit tests (mocked)
    └── LiteDocumentStore.IntegrationTests/ # Integration tests (real SQLite)
    └── LiteDocumentStore.Benchmarks/ # Benchmark tests
```

## When Modifying This Project

- See `CLAUDE.md` at the repo root for the authoritative architecture guide
- Keep the library AOT-clean: no reflection-based serialization, no `Expression.Compile`, no `dynamic`
- All public APIs need XML documentation
- New features need both unit and integration tests
- Consider performance implications of every change
- Maintain backward compatibility once v1.0 is released
