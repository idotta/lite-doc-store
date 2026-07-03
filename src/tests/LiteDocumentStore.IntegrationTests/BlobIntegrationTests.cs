using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for raw binary blob storage against real SQLite,
/// including transactional atomicity with document writes.
/// </summary>
[Collection(nameof(LiteDocumentStoreCollection))]
public class BlobIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public BlobIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithBlobTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateBlobTableAsync();
        return store;
    }

    [Fact]
    public async Task PutAndGetBlob_RoundTripsBytes()
    {
        var store = await CreateStoreWithBlobTableAsync();
        var payload = new byte[] { 1, 2, 3, 250, 251, 252 };

        await store.PutBlobAsync("b1", payload);
        var retrieved = await store.GetBlobAsync("b1");

        Assert.NotNull(retrieved);
        Assert.Equal(payload, retrieved);
    }

    [Fact]
    public async Task PutBlob_ExistingId_OverwritesPayload()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("b1", new byte[] { 1, 2, 3 });

        await store.PutBlobAsync("b1", new byte[] { 9, 8 });
        var retrieved = await store.GetBlobAsync("b1");

        Assert.Equal(new byte[] { 9, 8 }, retrieved);
    }

    [Fact]
    public async Task PutBlob_SlicedMemory_StoresOnlyTheSlice()
    {
        var store = await CreateStoreWithBlobTableAsync();
        var backing = new byte[] { 0, 1, 2, 3, 4, 5 };

        await store.PutBlobAsync("b1", backing.AsMemory(2, 3));
        var retrieved = await store.GetBlobAsync("b1");

        Assert.Equal(new byte[] { 2, 3, 4 }, retrieved);
    }

    [Fact]
    public async Task GetBlob_Missing_ReturnsNull()
    {
        var store = await CreateStoreWithBlobTableAsync();

        var retrieved = await store.GetBlobAsync("missing");

        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteBlob_Existing_ReturnsTrueAndRemoves()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("b1", new byte[] { 1 });

        var deleted = await store.DeleteBlobAsync("b1");

        Assert.True(deleted);
        Assert.False(await store.BlobExistsAsync("b1"));
    }

    [Fact]
    public async Task DeleteBlob_Missing_ReturnsFalse()
    {
        var store = await CreateStoreWithBlobTableAsync();

        Assert.False(await store.DeleteBlobAsync("missing"));
    }

    [Fact]
    public async Task BlobExists_ReflectsPresence()
    {
        var store = await CreateStoreWithBlobTableAsync();

        Assert.False(await store.BlobExistsAsync("b1"));
        await store.PutBlobAsync("b1", new byte[] { 1 });
        Assert.True(await store.BlobExistsAsync("b1"));
    }

    [Fact]
    public async Task Transaction_DocumentAndBlob_CommitTogether()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.CreateTableAsync<BlobMeta>();

        await store.ExecuteInTransactionAsync(async () =>
        {
            await store.UpsertAsync("m1", new BlobMeta("recording", 3));
            await store.PutBlobAsync("m1", new byte[] { 1, 2, 3 });
        });

        Assert.NotNull(await store.GetAsync<BlobMeta>("m1"));
        Assert.NotNull(await store.GetBlobAsync("m1"));
    }

    [Fact]
    public async Task Transaction_Failure_RollsBackBothDocumentAndBlob()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.CreateTableAsync<BlobMeta>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ExecuteInTransactionAsync(async () =>
            {
                await store.UpsertAsync("m1", new BlobMeta("recording", 3));
                await store.PutBlobAsync("m1", new byte[] { 1, 2, 3 });
                throw new InvalidOperationException("boom");
            }));

        Assert.Null(await store.GetAsync<BlobMeta>("m1"));
        Assert.Null(await store.GetBlobAsync("m1"));
        Assert.False(await store.BlobExistsAsync("m1"));
    }

    private sealed record BlobMeta(string Kind, int SampleCount);
}
