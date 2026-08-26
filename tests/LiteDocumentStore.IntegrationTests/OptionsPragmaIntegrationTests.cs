using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Regression tests for the options that used to be applied and silently ignored: a page size
/// SQLite dropped on the floor, WAL mode on an in-memory database, and
/// <see cref="DocumentStoreOptions.EnableForeignKeys"/> = false, which left foreign keys on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptionsPragmaIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _databasePaths = [];
    private readonly List<IDocumentStore> _stores = [];
    private readonly DocumentStoreFactory _factory = new();

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-pragma-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private async Task<IDocumentStore> CreateStoreAsync(DocumentStoreOptions options)
    {
        var store = await _factory.CreateAsync(options);
        _stores.Add(store);
        return store;
    }

    private static Task<string?> ReadPragmaAsync(IDocumentStore store, string pragma) =>
        store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            var value = await command.ExecuteScalarAsync(ct);
            return value?.ToString();
        });

    [Fact]
    public async Task PageSize_OnANewWalDatabase_IsApplied()
    {
        // Regression: journal_mode was applied first, and SQLite refuses to change the page size
        // of a database already in WAL mode, so the option was a no-op even on a new file.
        var options = DocumentStoreOptions.ForFile(NewDatabasePath());
        options.PageSize = 8192;

        var store = await CreateStoreAsync(options);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));

        Assert.Equal("8192", await ReadPragmaAsync(store, "PRAGMA page_size;"));
        Assert.Equal("wal", await ReadPragmaAsync(store, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task PageSize_OnAnExistingDatabaseWithADifferentPageSize_ThrowsAtStoreCreation()
    {
        var path = NewDatabasePath();
        var initial = DocumentStoreOptions.ForFile(path);
        initial.PageSize = 4096;

        var store = await CreateStoreAsync(initial);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));
        await store.DisposeAsync();
        _stores.Remove(store);

        var reopened = DocumentStoreOptions.ForFile(path);
        reopened.PageSize = 8192;

        var ex = await Assert.ThrowsAsync<IncompatiblePageSizeException>(() => _factory.CreateAsync(reopened));
        Assert.Equal(8192, ex.RequestedPageSize);
        Assert.Equal(4096, ex.ActualPageSize);
    }

    [Fact]
    public async Task PageSize_OfZero_AcceptsWhateverTheDatabaseHas()
    {
        var path = NewDatabasePath();
        var initial = DocumentStoreOptions.ForFile(path);
        initial.PageSize = 8192;

        var store = await CreateStoreAsync(initial);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));
        await store.DisposeAsync();
        _stores.Remove(store);

        var reopened = DocumentStoreOptions.ForFile(path);
        reopened.PageSize = 0;

        var second = await CreateStoreAsync(reopened);

        Assert.Equal("8192", await ReadPragmaAsync(second, "PRAGMA page_size;"));
        Assert.Equal("first", (await second.GetAsync<Doc>("a"))!.Name);
    }

    [Fact]
    public async Task PageSize_MismatchIsDetectedSynchronouslyToo()
    {
        var path = NewDatabasePath();
        var initial = DocumentStoreOptions.ForFile(path);
        initial.PageSize = 4096;

        var store = await CreateStoreAsync(initial);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));
        await store.DisposeAsync();
        _stores.Remove(store);

        var reopened = DocumentStoreOptions.ForFile(path);
        reopened.PageSize = 16384;

        Assert.Throws<IncompatiblePageSizeException>(() => _factory.Create(reopened));
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public async Task EnableForeignKeys_IsStatedOnTheConnection(bool enabled, string expected)
    {
        // Microsoft.Data.Sqlite opens connections with foreign_keys already ON, so the OFF has to
        // be written out: skipping it left EnableForeignKeys = false doing nothing.
        var options = DocumentStoreOptions.ForFile(NewDatabasePath());
        options.EnableForeignKeys = enabled;

        var store = await CreateStoreAsync(options);

        Assert.Equal(expected, await ReadPragmaAsync(store, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task WalMode_OnAnInMemoryDatabase_ThrowsAtStoreCreation()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.EnableWalMode = true;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _factory.CreateAsync(options));
        Assert.Contains("cannot use WAL mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithAnOptionSqliteWouldIgnore_ThrowsBeforeOpeningAConnection()
    {
        // The DI registration resolves through the factory with a raw options object, so this is
        // the only place a hand-built DocumentStoreOptions is checked.
        var options = DocumentStoreOptions.ForFile(NewDatabasePath());
        options.BusyTimeoutMs = -1;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _factory.CreateAsync(options));
        Assert.Equal(nameof(DocumentStoreOptions.BusyTimeoutMs), ex.ParamName);
        Assert.False(File.Exists(options.ConnectionString.Replace("Data Source=", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PrivateInMemoryWithSharedCache_IsRejected()
    {
        // "Data Source=:memory:;Cache=Shared" reads as shared and is not: measured against
        // Microsoft.Data.Sqlite, a second connection opens an empty database, so a pooled store
        // would lose every write made through another connection.
        var options = new DocumentStoreOptions("Data Source=:memory:;Cache=Shared")
        {
            EnableWalMode = false,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _factory.CreateAsync(options));
        Assert.Contains("private in-memory database", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedInMemoryStore_IsVisibleAcrossSeveralPooledConnections()
    {
        // The counterpart to the rejection above: the forms the store does accept really are one
        // database. Each open transaction holds its own physical connection, so reading the
        // document through three at once proves it crosses connections, not just the pool's first.
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 4;

        var store = await CreateStoreAsync(options);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));

        var transactions = new List<IDocumentTransaction>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                transactions.Add(await store.BeginTransactionAsync());
            }

            foreach (var transaction in transactions)
            {
                Assert.Equal("first", (await transaction.GetAsync<Doc>("a"))!.Name);
            }
        }
        finally
        {
            foreach (var transaction in transactions)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task OptimizeForPerformance_AppliesEveryPragmaItSets()
    {
        // The builder preset only writes fields; this is the half that proves SQLite accepted
        // them — the page size in particular, which it silently ignores in the wrong order.
        var options = DocumentStoreOptions.Builder()
            .UseFile(NewDatabasePath())
            .OptimizeForPerformance()
            .Build();

        var store = await CreateStoreAsync(options);
        await store.CreateTableAsync<Doc>();

        Assert.Equal("wal", await ReadPragmaAsync(store, "PRAGMA journal_mode;"));
        Assert.Equal("1", await ReadPragmaAsync(store, "PRAGMA synchronous;"));
        Assert.Equal("8192", await ReadPragmaAsync(store, "PRAGMA page_size;"));
        Assert.Equal("-4000", await ReadPragmaAsync(store, "PRAGMA cache_size;"));
    }

    [Fact]
    public async Task OptimizeForSafety_AppliesFullSynchronousAndForeignKeys()
    {
        var options = DocumentStoreOptions.Builder()
            .UseFile(NewDatabasePath())
            .WithForeignKeys(false)
            .OptimizeForSafety()
            .Build();

        var store = await CreateStoreAsync(options);
        await store.CreateTableAsync<Doc>();

        Assert.Equal("wal", await ReadPragmaAsync(store, "PRAGMA journal_mode;"));
        Assert.Equal("2", await ReadPragmaAsync(store, "PRAGMA synchronous;"));
        Assert.Equal("1", await ReadPragmaAsync(store, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task OptimizeForTesting_ProducesAUsableInMemoryStore()
    {
        var store = await CreateStoreAsync(DocumentStoreOptions.Builder().OptimizeForTesting().Build());
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("first", 1));

        // "memory", not "wal": the preset turns WAL off because SQLite would answer this way
        // regardless, and the store now refuses that combination up front.
        Assert.Equal("memory", await ReadPragmaAsync(store, "PRAGMA journal_mode;"));
        Assert.Equal("0", await ReadPragmaAsync(store, "PRAGMA synchronous;"));
        Assert.Equal(1, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task AdditionalPragmas_AreAppliedToEveryPooledConnection()
    {
        var options = DocumentStoreOptions.Builder()
            .UseFile(NewDatabasePath())
            .WithMaxPoolSize(3)
            .AddPragma("PRAGMA temp_store = MEMORY")
            .Build();

        var store = await CreateStoreAsync(options);
        await store.CreateTableAsync<Doc>();

        // Each open transaction pins its own physical connection, so reading through three at
        // once proves the pragma was applied per connection and not just to the pool's first.
        var transactions = new List<IDocumentTransaction>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                transactions.Add(await store.BeginTransactionAsync());
                Assert.Equal(
                    "2",
                    await transactions[i].ExecuteRawAsync(async (connection, ct) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA temp_store;";
                        return (await command.ExecuteScalarAsync(ct))?.ToString();
                    }));
            }
        }
        finally
        {
            foreach (var transaction in transactions)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var path in _databasePaths)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // A lingering handle only leaks a temp file; it must not fail the run.
                    }
                }
            }
        }
    }

    private sealed record Doc(string Name, int Value);
}
