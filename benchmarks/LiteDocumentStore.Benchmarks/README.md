# LiteDocumentStore Benchmarks

BenchmarkDotNet benchmarks that validate the performance characteristics of the LiteDocumentStore
library. `Program.cs` hands `args` to `BenchmarkSwitcher.FromAssembly(...)`, so every benchmark class
in this project is discovered automatically — adding one needs no registration.

## Running Benchmarks

### Run All Benchmarks

```bash
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks
```

Without arguments, the switcher prompts for which benchmarks to run.

### Run Specific Benchmarks

Arguments meant for BenchmarkDotNet must come after `--`, or `dotnet run` consumes them itself and
the filter never reaches the switcher. Quote the pattern so the shell does not glob it:

```bash
# Comparison benchmarks (LiteDocumentStore vs Dapper vs LiteDB)
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks -- --filter '*Comparison*'

# Virtual column benchmarks
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks -- --filter '*VirtualColumn*'

# Connection model benchmarks
dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks -- --filter '*ConnectionModel*'
```

### Generate Reports

BenchmarkDotNet automatically generates reports in `BenchmarkDotNet.Artifacts/results/`:
- HTML reports for viewing in browser
- CSV files for importing into spreadsheets
- Markdown summary tables

## Available Benchmarks

### `ComparisonBenchmark`

**Purpose**: LiteDocumentStore vs raw Dapper vs LiteDB across the core operations. Dapper and LiteDB
are benchmark-only package references — comparison baselines, never library dependencies.

**Operations tested**: single insert, bulk insert (100 documents), query by id, full table scan,
query by category, delete. Scan operations run over 1,000 documents.

**What to look for**:
- LiteDocumentStore should be competitive with raw Dapper (within 5-10% overhead for the abstraction)
- Both SQLite-based solutions should outperform LiteDB for bulk operations
- LiteDB may be faster for single operations due to less serialization overhead

### `SimplifiedComparisonBenchmark`

The same LiteDocumentStore-vs-Dapper pairs with a shorter job (`RunStrategy.Throughput`, 5
iterations, 3 warmups) for quick iteration, plus bulk-delete and update benchmarks that
`ComparisonBenchmark` lacks. It drops the LiteDB arm. Largely redundant with `ComparisonBenchmark`;
use it while iterating, not for numbers you intend to quote.

### `VirtualColumnBenchmark`

**Purpose**: what indexed virtual (generated) columns do to query time on frequently queried JSON
fields.

**Test data**: 50,000 documents in a product-catalog shape, with virtual columns over `category`,
`sku` and the nested `metadata.brand`.

**Benchmarks**:
1. Category query — with and without a virtual column (the without arm is the baseline)
2. SKU query — with and without
3. Nested property (brand) query — with and without
4. The same category and SKU queries written as raw SQL, indexed and unindexed, which separates the
   index's contribution from the store's own overhead
5. `AddVirtualColumn_Overhead` — the cost of adding the column and its index

### `ConnectionModelBenchmark` and `StorePathBenchmark`

Both live in `ConnectionModelBenchmark.cs`. `ConnectionModelBenchmark` measures the primitives — a
held connection vs opening per operation vs renting from the store's own pool, for reads, writes and
64 concurrent reads. `StorePathBenchmark` measures the store's real path over a file database, a
shared-cache in-memory database and a private `:memory:` one.

These two are the evidence behind the connection model documented in `CLAUDE.md`. **Re-run both
before changing that design** — the trade-off it records (a few percent on a single file-DB read,
3x on 64 concurrent reads) came from these numbers.

## How Virtual Columns Work

Virtual columns are SQLite generated columns that extract JSON fields using `json_extract()`:

```csharp
// Add a virtual column with automatic index
await store.AddVirtualColumnAsync<Product>(
    p => p.Category,
    "category",
    createIndex: true);

// SQLite executes:
// ALTER TABLE [Product] ADD COLUMN [category] TEXT
//   GENERATED ALWAYS AS (json_extract(data, '$.Category')) VIRTUAL
// CREATE INDEX IF NOT EXISTS [idx_Product_category] ON [Product] ([category])
```

**Benefits**:
- No storage overhead (VIRTUAL columns computed on read)
- Indexes work on virtual columns for fast lookups
- Transparent to application code — queries still use expressions

### Reading the results

The gain comes from replacing a full scan (which evaluates `json_extract` per row) with a B-tree
lookup, so it grows with the dataset and with how expensive the path is to extract — a nested path
benefits more than a top-level one. This project's numbers are the source of truth for how much;
run it rather than quoting a figure, since the answer moves with dataset size, cardinality and
hardware.

### When to Use Virtual Columns

✅ **Use virtual columns when**:
- Field is queried frequently (hot path)
- Dataset is large (1000+ documents)
- Query uses equality or range comparisons
- Field values are indexed well (good cardinality)

❌ **Avoid virtual columns when**:
- Field is rarely queried
- Dataset is small (< 100 documents)
- Field values have low cardinality (e.g. boolean)
- Memory/storage is extremely constrained

### Trade-offs

**Advantages**:
- Large query speed improvements on selective fields
- No storage overhead (VIRTUAL columns)
- Transparent to application queries
- Standard SQLite indexes

**Disadvantages**:
- Small overhead on writes (computing the index entry)
- Index storage space (though typically small)
- Schema changes required (ALTER TABLE)
- Not retroactive without rebuild

## Best Practices

1. **Always Run in Release Mode**: Debug builds have different performance characteristics
2. **Close Other Applications**: For consistent results
3. **Run Multiple Times**: BenchmarkDotNet handles this automatically with multiple iterations
4. **Consider Warmup**: First run may be slower due to JIT compilation (BenchmarkDotNet handles this)

## Adding New Benchmarks

1. Create a new class in this project — `BenchmarkSwitcher` finds it, so there is nothing to register
2. Add `[MemoryDiagnoser]` to the class; add `[SimpleJob(...)]` if the default job is too slow
3. Mark methods with `[Benchmark]`, and give the arm you are comparing against
   `[Benchmark(Baseline = true)]` so BenchmarkDotNet prints a ratio column
4. Use `Description = "..."` for readable result rows
5. Use `[GlobalSetup]` / `[GlobalCleanup]` for fixtures — setup cost is excluded from the measurement

Example:

```csharp
[MemoryDiagnoser]
public class MyBenchmark
{
    [GlobalSetup]
    public void Setup()
    {
        // Initialize test data
    }

    [Benchmark(Baseline = true, Description = "Existing approach")]
    public void Baseline()
    {
    }

    [Benchmark(Description = "New approach")]
    public void MyOperation()
    {
        // Code to benchmark
    }
}
```

## Continuous Performance Monitoring

These benchmarks are not part of CI — they are far too slow for a per-push pipeline, and shared
runners are too noisy for the numbers to mean anything. Run them locally when changing a hot path.
If they ever do get automated, it needs a dedicated runner:

```yaml
# Example GitHub Actions step
- name: Run Benchmarks
  run: dotnet run -c Release --project benchmarks/LiteDocumentStore.Benchmarks -- --exporters json

- name: Store Benchmark Results
  uses: benchmark-action/github-action-benchmark@v1
  with:
    tool: 'benchmarkdotnet'
    output-file-path: BenchmarkDotNet.Artifacts/results/results.json
```

## References

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [Performance Best Practices for .NET](https://learn.microsoft.com/en-us/dotnet/core/performance/)
- [SQLite JSON Functions](https://www.sqlite.org/json1.html)
