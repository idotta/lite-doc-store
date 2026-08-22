using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the cancellation token threaded through every async member.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite runs SQLite I/O synchronously, so a token cannot interrupt a statement
/// already executing. What it does guarantee — and what these pin — is that a cancelled token is
/// observed before the work starts, and that the wait for a pooled connection is cancellable.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CancellationTests
{
    private sealed record Doc(string Name, int Value);

    private static async Task<IDocumentStore> CreateStoreAsync(int? maxPoolSize = null)
    {
        var options = DocumentStoreOptions.ForInMemory();
        if (maxPoolSize is { } size)
        {
            options.MaxPoolSize = size;
        }

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    public static TheoryData<string, Func<IDocumentStore, CancellationToken, Task>> CancellableOperations() =>
        new()
        {
            { "CreateTableAsync", (s, ct) => s.CreateTableAsync<Doc>(ct) },
            { "UpsertAsync", (s, ct) => s.UpsertAsync("a", new Doc("a", 1), ct) },
            { "UpsertManyAsync", (s, ct) => s.UpsertManyAsync([("a", new Doc("a", 1))], ct) },
            { "UpsertWithVersionAsync", (s, ct) => s.UpsertWithVersionAsync("a", new Doc("a", 1), 0, ct) },
            { "GetWithVersionAsync", (s, ct) => s.GetWithVersionAsync<Doc>("a", ct) },
            { "GetAsync", (s, ct) => s.GetAsync<Doc>("a", ct) },
            { "GetAllAsync", (s, ct) => s.GetAllAsync<Doc>(ct) },
            { "DeleteAsync", (s, ct) => s.DeleteAsync<Doc>("a", ct) },
            { "DeleteManyAsync", (s, ct) => s.DeleteManyAsync<Doc>(["a"], ct) },
            { "ExistsAsync", (s, ct) => s.ExistsAsync<Doc>("a", ct) },
            { "CountAsync", (s, ct) => s.CountAsync<Doc>(ct) },
            { "QueryAsync", (s, ct) => s.QueryAsync<Doc, string>("$.Name", "a", ct) },
            { "CreateIndexAsync", (s, ct) => s.CreateIndexAsync<Doc>(d => d.Name, null, ct) },
            { "CreateCompositeIndexAsync", (s, ct) => s.CreateCompositeIndexAsync<Doc>([d => d.Name], null, ct) },
            { "AddVirtualColumnAsync", (s, ct) => s.AddVirtualColumnAsync<Doc>(d => d.Name, "name_col", false, "TEXT", ct) },
            { "CreateBlobTableAsync", (s, ct) => s.CreateBlobTableAsync(ct) },
            { "PutBlobAsync", (s, ct) => s.PutBlobAsync("a", new byte[] { 1 }, ct) },
            { "GetBlobAsync", (s, ct) => s.GetBlobAsync("a", ct) },
            { "DeleteBlobAsync", (s, ct) => s.DeleteBlobAsync("a", ct) },
            { "BlobExistsAsync", (s, ct) => s.BlobExistsAsync("a", ct) },
            { "ExecuteRawAsync", (s, ct) => s.ExecuteRawAsync((c, t) => c.ExecuteAsync("SELECT 1", t), ct) },
            { "BeginTransactionAsync", (s, ct) => s.BeginTransactionAsync(ct) },
            { "ExecuteInTransactionAsync", (s, ct) => s.ExecuteInTransactionAsync(_ => Task.CompletedTask, ct) },
        };

    [Theory]
    [MemberData(nameof(CancellableOperations))]
    public async Task StoreOperation_WithAnAlreadyCancelledToken_Throws(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, cts.Token));

        Assert.True(
            exception is OperationCanceledException,
            $"{operationName} did not observe the cancelled token; got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(CancellableOperations))]
    public async Task StoreOperation_WithAnUncancelledToken_Succeeds(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();
        using var cts = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() => operation(store, cts.Token));

        Assert.True(exception is null, $"{operationName} failed with {exception}");
    }

    [Fact]
    public async Task Operation_WaitingOnASaturatedPool_IsCancellable()
    {
        // One connection, held by an open transaction, so the next rent has to queue. Without a
        // token on the operation this wait was unabortable — the point of P0-3.
        await using var store = await CreateStoreAsync(maxPoolSize: 1);
        await using var holdingTransaction = await store.BeginTransactionAsync();

        using var cts = new CancellationTokenSource();
        var queued = store.GetAsync<Doc>("a", cts.Token);

        Assert.False(queued.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task TransactionOperation_WithAnAlreadyCancelledToken_Throws()
    {
        await using var store = await CreateStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.UpsertAsync("a", new Doc("a", 1), cts.Token));
    }

    [Fact]
    public async Task IsHealthyAsync_WithAnAlreadyCancelledToken_ReturnsFalse()
    {
        // It reports failure rather than throwing, so a health endpoint stays a health endpoint.
        await using var store = await CreateStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.False(await store.IsHealthyAsync(cts.Token));
    }
}
