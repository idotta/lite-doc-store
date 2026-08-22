using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for cancellation against a real SQLite database: a cancelled operation must
/// leave no trace, and the token must reach the migration and introspection layers too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CancellationIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private sealed record Doc(string Name, int Value);

    private async Task<IDocumentStore> CreateFileStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-cancel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForFile(path));
        await store.CreateTableAsync<Doc>();
        return store;
    }

    [Fact]
    public async Task CancelledWrite_DoesNotPersistTheDocument()
    {
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.UpsertAsync("doc-1", new Doc("Cancelled", 1), cts.Token));

        Assert.Equal(0, await store.CountAsync<Doc>());
        Assert.Null(await store.GetAsync<Doc>("doc-1"));
    }

    [Fact]
    public async Task CancellingOneOperation_LeavesTheStoreUsable()
    {
        // The cancelled operation must return its pooled connection, not poison it.
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.UpsertAsync("doc-1", new Doc("Cancelled", 1), cts.Token));

        await store.UpsertAsync("doc-2", new Doc("Fine", 2));

        Assert.Equal(new Doc("Fine", 2), await store.GetAsync<Doc>("doc-2"));
        Assert.Equal(1, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task CancelledWriteInsideATransaction_RollsBackTheWholeTransaction()
    {
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ExecuteInTransactionAsync(async transaction =>
            {
                await transaction.UpsertAsync("doc-1", new Doc("First", 1));
                await cts.CancelAsync();
                await transaction.UpsertAsync("doc-2", new Doc("Second", 2), cts.Token);
            }));

        // Disposing without a commit rolled the transaction back, so neither write survives.
        Assert.Equal(0, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task ExecuteRawAsync_HandsTheCallbackTheCallersToken()
    {
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ExecuteRawAsync((connection, token) =>
                connection.ExecuteAsync("CREATE TABLE ShouldNotExist (id TEXT)", token), cts.Token));

        var exists = await store.ExecuteRawAsync((connection, token) =>
            connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ShouldNotExist'",
                token));

        Assert.Equal(0, exists);
    }

    [Fact]
    public async Task MigrationRunner_WithAnAlreadyCancelledToken_AppliesNothing()
    {
        await using var store = await CreateFileStoreAsync();
        var migration = new Migration(
            1,
            "create_widget",
            "CREATE TABLE Widget (id TEXT PRIMARY KEY)",
            "DROP TABLE Widget");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await store.ExecuteRawAsync(async (connection, _) =>
        {
            var runner = new MigrationRunner(connection);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runner.ApplyMigrationAsync(migration, cts.Token));

            Assert.Equal(0, await runner.GetCurrentVersionAsync());

            var introspector = new SchemaIntrospector(connection);
            Assert.False(await introspector.TableExistsAsync("Widget"));
        });
    }

    [Fact]
    public async Task CreateAsync_WithAnAlreadyCancelledToken_LeavesNoOpenHandle()
    {
        // The factory opens the pool's first connection, so it is a cancellation point like any
        // other async member. Microsoft.Data.Sqlite opens the file before the token is observed,
        // so what matters is that nothing is still holding it afterwards.
        var path = Path.Combine(Path.GetTempPath(), $"lds-cancel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForFile(path), cts.Token));

        // Throws IOException on Windows while any connection still holds the database file.
        File.Delete(path);
    }

    [Fact]
    public async Task SchemaIntrospector_WithAnAlreadyCancelledToken_Throws()
    {
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await store.ExecuteRawAsync(async (connection, _) =>
        {
            var introspector = new SchemaIntrospector(connection);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => introspector.GetTablesAsync(cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => introspector.TableExistsAsync("Doc", cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => introspector.GetColumnsAsync("Doc", cts.Token));
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _databasePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Windows can still hold the handle briefly; a temp file left behind is harmless.
            }
        }
    }
}
