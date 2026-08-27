using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the <see cref="IDocumentOperations"/> members invoked <em>on a
/// transaction</em> rather than on the store. The delegation is one line per member, but the
/// contract is not: every one of them has to run on the transaction's own connection, see the
/// transaction's uncommitted writes, and vanish with a rollback.
/// </summary>
/// <remarks>
/// Nothing here touches the store while a transaction is open: these run on a shared-cache
/// in-memory database, which locks at table granularity, so an overlapping write would fail with
/// <c>SQLITE_LOCKED</c> rather than testing anything. Cross-checks happen after the transaction
/// ends.
/// </remarks>
[Collection(nameof(LiteDocumentStoreCollection))]
public class TransactionOperationsIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public TransactionOperationsIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly string[] ExpectedPrefixedIds = ["b/1", "b/2"];

    private sealed record TxnDoc(string Name, int Age, string? Email = null);

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<TxnDoc>();
        return store;
    }

    private async Task<IDocumentStore> CreateStoreWithBlobTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateBlobTableAsync();
        return store;
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    // ---- Documents -------------------------------------------------------------------

    [Fact]
    public async Task CreateTableAsync_OnATransaction_RollsBackWithIt()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.CreateTableAsync<TxnDoc>();
            await transaction.UpsertAsync("a", new TxnDoc("Ann", 30));

            Assert.Equal(1L, await transaction.CountAsync<TxnDoc>());

            await transaction.RollbackAsync();
        }

        // SQLite rolls DDL back like any other statement, so the table is gone with it. The store
        // surfaces the provider's error rather than TableNotFoundException, which nothing throws.
        var missing = await Assert.ThrowsAsync<SqliteException>(() => store.CountAsync<TxnDoc>());
        Assert.Contains("no such table", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAsync_OnATransaction_IsVisibleInsideItAndRollsBack()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("a", new TxnDoc("Ann", 30));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            Assert.True(await transaction.DeleteAsync<TxnDoc>("a"));
            Assert.Null(await transaction.GetAsync<TxnDoc>("a"));
            Assert.False(await transaction.DeleteAsync<TxnDoc>("a"));

            await transaction.RollbackAsync();
        }

        Assert.NotNull(await store.GetAsync<TxnDoc>("a"));
    }

    [Fact]
    public async Task GetAllAsync_OnATransaction_SeesItsOwnUncommittedWrites()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("a", new TxnDoc("Ann", 30));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync("b", new TxnDoc("Bob", 40));

            var all = (await transaction.GetAllAsync<TxnDoc>()).ToList();

            Assert.Equal(2, all.Count);
            Assert.Contains(all, d => d.Name == "Bob");

            await transaction.RollbackAsync();
        }

        Assert.Single(await store.GetAllAsync<TxnDoc>());
    }

    [Fact]
    public async Task GetWithVersionAsync_OnATransaction_ReturnsTheVersionTheTransactionWrote()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("a", new TxnDoc("Ann", 30));

        await using var transaction = await store.BeginTransactionAsync();

        var before = await transaction.GetWithVersionAsync<TxnDoc>("a");
        Assert.NotNull(before);
        Assert.Equal(1L, before.Version);

        await transaction.UpsertAsync("a", new TxnDoc("Ann", 31));

        var after = await transaction.GetWithVersionAsync<TxnDoc>("a");
        Assert.Equal(2L, after!.Version);
        Assert.Equal(31, after.Data.Age);
        Assert.Null(await transaction.GetWithVersionAsync<TxnDoc>("missing"));

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task PatchWithVersionAsync_OnATransaction_AppliesOnAMatchAndThrowsOnAMismatch()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("a", new TxnDoc("Ann", 30));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            var version = await transaction.PatchWithVersionAsync(
                "a", DocumentPatch<TxnDoc>.Set("$.Age", 31), expectedVersion: 1);

            Assert.Equal(2L, version);
            Assert.Equal(31, (await transaction.GetAsync<TxnDoc>("a"))!.Age);

            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.PatchWithVersionAsync("a", DocumentPatch<TxnDoc>.Set("$.Age", 32), expectedVersion: 1));

            // Inside a transaction the stored-version read runs under the same locks as the
            // guarded write, so the reported actual version is exact.
            Assert.Equal(ConcurrencyConflictKind.VersionMismatch, conflict.Kind);
            Assert.Equal(2L, conflict.ActualVersion!.Value);

            await transaction.CommitAsync();
        }

        Assert.Equal(31, (await store.GetAsync<TxnDoc>("a"))!.Age);
    }

    [Fact]
    public async Task PatchWithVersionAsync_OnATransaction_MissingDocument_ReportsNotFound()
    {
        var store = await CreateStoreWithTableAsync();

        await using var transaction = await store.BeginTransactionAsync();

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            transaction.PatchWithVersionAsync("missing", DocumentPatch<TxnDoc>.Set("$.Age", 1), expectedVersion: 1));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, conflict.Kind);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task QueryOverloads_OnATransaction_FilterTheTransactionsOwnWrites()
    {
        var store = await CreateStoreWithTableAsync();

        await using var transaction = await store.BeginTransactionAsync();

        await transaction.UpsertAsync("a", new TxnDoc("Ann", 30, "ann@example.com"));
        await transaction.UpsertAsync("b", new TxnDoc("Bob", 40));

        var query = DocumentQuery<TxnDoc>.Where("$.Age", QueryOperator.GreaterThanOrEqual, 35);

        Assert.Single(await transaction.QueryAsync(query));
        Assert.Equal(1L, await transaction.CountAsync(query));
        Assert.True(await transaction.ExistsAsync(query));
        Assert.False(await transaction.ExistsAsync(
            DocumentQuery<TxnDoc>.Where("$.Age", QueryOperator.GreaterThan, 100)));
        Assert.Single(await transaction.QueryAsync<TxnDoc, string>("$.Name", "Ann"));
        Assert.True(await transaction.ExistsAsync<TxnDoc>("a"));

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task DropIndexAsync_OnATransaction_DropsWhatCreateIndexAsyncCreated()
    {
        var store = await CreateStoreWithTableAsync();
        var table = store.GetTableName<TxnDoc>();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.CreateIndexAsync<TxnDoc>(d => d.Email!);
            await transaction.CreateIndexAsync<TxnDoc>(d => d.Name, "ix_txn_named");
            await transaction.CreateCompositeIndexAsync<TxnDoc>([d => d.Name, d => d.Age], "ix_txn_composite");

            Assert.Equal(3L, await CountIndexesAsync(transaction, table));

            // The expression overload derives the same name CreateIndexAsync generated; the
            // explicitly named one has to go through the string overload.
            await transaction.DropIndexAsync<TxnDoc>(d => d.Email!);
            await transaction.DropIndexAsync("ix_txn_named");
            await transaction.DropIndexAsync("ix_txn_composite");

            Assert.Equal(0L, await CountIndexesAsync(transaction, table));

            await transaction.CommitAsync();
        }

        Assert.Equal(0L, await CountIndexesAsync(store, table));
    }

    [Fact]
    public async Task AddVirtualColumnAsync_OnATransaction_AddsAQueryableColumn()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("a", new TxnDoc("Ann", 30));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.AddVirtualColumnAsync<TxnDoc>(d => d.Age, "age_col", createIndex: true, "INTEGER");

            var age = await transaction.ExecuteRawAsync(async (connection, ct) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT age_col FROM [{store.GetTableName<TxnDoc>()}] WHERE id = 'a'";
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            });

            Assert.Equal(30L, age);

            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task Dispose_WithoutCommitting_RollsBackAndBlocksFurtherOperations()
    {
        var store = await CreateStoreWithTableAsync();

        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new TxnDoc("Ann", 30));

        // The synchronous disposal path, not DisposeAsync.
        transaction.Dispose();
        transaction.Dispose();

        Assert.False(transaction.IsCommitted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.GetAsync<TxnDoc>("a"));
        Assert.Equal(0L, await store.CountAsync<TxnDoc>());
    }

    [Fact]
    public async Task Dispose_AfterCommitting_KeepsTheCommitAndReleasesOnce()
    {
        var store = await CreateStoreWithTableAsync();

        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("a", new TxnDoc("Ann", 30));
        await transaction.CommitAsync();

        // There is nothing left to roll back, so disposal must not undo the commit or hand the
        // same connection back to the pool a second time.
        transaction.Dispose();

        Assert.True(transaction.IsCommitted);
        Assert.Equal(1L, await store.CountAsync<TxnDoc>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync());
    }

    // ---- Blobs -----------------------------------------------------------------------

    [Fact]
    public async Task BlobReads_OnATransaction_SeeTheTransactionsOwnWrite()
    {
        var store = await CreateStoreWithBlobTableAsync();
        var payload = Payload(2048);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.PutBlobAsync("b/1", payload, new BlobWriteOptions { ContentType = "application/octet-stream" });

            Assert.True(await transaction.BlobExistsAsync("b/1"));
            Assert.Equal(payload, await transaction.GetBlobAsync("b/1"));
            Assert.Equal((long)payload.Length, (await transaction.BlobLengthAsync("b/1"))!.Value);

            var info = await transaction.GetBlobInfoAsync("b/1");
            Assert.NotNull(info);
            Assert.Equal("application/octet-stream", info.ContentType);
            Assert.Equal((long)payload.Length, info.Length);
            Assert.Equal(1L, info.Version);

            Assert.False(await transaction.BlobExistsAsync("missing"));
            Assert.Null(await transaction.GetBlobAsync("missing"));
            Assert.Null(await transaction.GetBlobInfoAsync("missing"));

            await transaction.CommitAsync();
        }

        Assert.Equal(payload, await store.GetBlobAsync("b/1"));
    }

    [Fact]
    public async Task PutBlobAsync_FromAStreamWithOptions_OnATransaction_StoresTheContentType()
    {
        var store = await CreateStoreWithBlobTableAsync();
        var payload = Payload(4096);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            using var source = new MemoryStream(payload);
            await transaction.PutBlobAsync("b/1", source, payload.Length, new BlobWriteOptions { ContentType = "image/png" });

            await transaction.CommitAsync();
        }

        var info = await store.GetBlobInfoAsync("b/1");
        Assert.Equal("image/png", info!.ContentType);
        Assert.Equal(payload, await store.GetBlobAsync("b/1"));
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_OnATransaction_GuardsEveryOverload()
    {
        var store = await CreateStoreWithBlobTableAsync();
        var payload = Payload(512);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            Assert.Equal(1L, await transaction.PutBlobWithVersionAsync("b/1", payload, expectedVersion: 0));
            Assert.Equal(2L, await transaction.PutBlobWithVersionAsync(
                "b/1", payload, expectedVersion: 1, new BlobWriteOptions { ContentType = "text/plain" }));

            using (var source = new MemoryStream(payload))
            {
                Assert.Equal(3L, await transaction.PutBlobWithVersionAsync("b/1", source, payload.Length, expectedVersion: 2));
            }

            using (var source = new MemoryStream(payload))
            {
                Assert.Equal(4L, await transaction.PutBlobWithVersionAsync(
                    "b/1", source, payload.Length, expectedVersion: 3, new BlobWriteOptions { ContentType = "text/csv" }));
            }

            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.PutBlobWithVersionAsync("b/1", payload, expectedVersion: 1));
            Assert.Equal(ConcurrencyConflictKind.VersionMismatch, conflict.Kind);
            Assert.Equal(4L, conflict.ActualVersion!.Value);

            await transaction.CommitAsync();
        }

        var info = await store.GetBlobInfoAsync("b/1");
        Assert.Equal(4L, info!.Version);
        Assert.Equal("text/csv", info.ContentType);
    }

    [Fact]
    public async Task DeleteBlobAsync_OnATransaction_RollsBackWithIt()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("b/1", Payload(64));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            Assert.True(await transaction.DeleteBlobAsync("b/1"));
            Assert.False(await transaction.DeleteBlobAsync("b/1"));
            Assert.False(await transaction.BlobExistsAsync("b/1"));

            await transaction.RollbackAsync();
        }

        Assert.True(await store.BlobExistsAsync("b/1"));
    }

    [Fact]
    public async Task DeleteBlobWithVersionAsync_OnATransaction_DeletesOnAMatchAndThrowsOnAMismatch()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("b/1", Payload(64));
        await store.PutBlobAsync("b/2", Payload(64));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.DeleteBlobWithVersionAsync("b/1", expectedVersion: 7));
            Assert.Equal(ConcurrencyConflictKind.VersionMismatch, conflict.Kind);
            Assert.Equal(1L, conflict.ActualVersion!.Value);

            await transaction.DeleteBlobWithVersionAsync("b/1", expectedVersion: 1);
            Assert.False(await transaction.BlobExistsAsync("b/1"));

            var missing = await Assert.ThrowsAsync<ConcurrencyException>(() =>
                transaction.DeleteBlobWithVersionAsync("missing", expectedVersion: 1));
            Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, missing.Kind);

            await transaction.CommitAsync();
        }

        Assert.False(await store.BlobExistsAsync("b/1"));
        Assert.True(await store.BlobExistsAsync("b/2"));
    }

    [Fact]
    public async Task ListBlobsAsync_OnATransaction_SeesUncommittedWritesInIdOrder()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("a/1", Payload(8));

        await using var transaction = await store.BeginTransactionAsync();

        await transaction.PutBlobAsync("b/2", Payload(8));
        await transaction.PutBlobAsync("b/1", Payload(8));

        var listed = await transaction.ListBlobsAsync("b/");

        Assert.Equal(ExpectedPrefixedIds, listed.Select(b => b.Id));
        Assert.Equal(3, (await transaction.ListBlobsAsync()).Count);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DeserializeDocument_OnATransaction_RoundTripsWhatSerializeDocumentWrote()
    {
        var store = await CreateStoreWithTableAsync();

        await using var transaction = await store.BeginTransactionAsync();

        var json = System.Text.Encoding.UTF8.GetString(transaction.SerializeDocument(new TxnDoc("Ann", 30)));
        var back = transaction.DeserializeDocument<TxnDoc>(json);

        Assert.Equal("Ann", back!.Name);
        Assert.Equal(store.GetTableName<TxnDoc>(), transaction.GetTableName<TxnDoc>());

        await transaction.RollbackAsync();
    }

    private static Task<long> CountIndexesAsync(IDocumentOperations operations, string table) =>
        operations.ExecuteRawAsync(async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = @Table AND name NOT LIKE 'sqlite_%'";
            command.Parameters.AddWithValue("@Table", table);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        });
}
