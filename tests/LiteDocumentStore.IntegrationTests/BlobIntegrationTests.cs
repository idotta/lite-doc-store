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

        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("m1", new BlobMeta("recording", 3));
            await tx.PutBlobAsync("m1", new byte[] { 1, 2, 3 });
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
            store.ExecuteInTransactionAsync(async tx =>
            {
                await tx.UpsertAsync("m1", new BlobMeta("recording", 3));
                await tx.PutBlobAsync("m1", new byte[] { 1, 2, 3 });
                throw new InvalidOperationException("boom");
            }));

        Assert.Null(await store.GetAsync<BlobMeta>("m1"));
        Assert.Null(await store.GetBlobAsync("m1"));
        Assert.False(await store.BlobExistsAsync("m1"));
    }

    [Fact]
    public async Task PutBlobAsync_EmptyPayload_StoresAPresentZeroLengthBlob()
    {
        var store = await CreateStoreWithBlobTableAsync();

        await store.PutBlobAsync("empty", ReadOnlyMemory<byte>.Empty);

        // Present but empty, not absent: the distinction a caller branches on, and the one a
        // NOT NULL data column plus a zero-length payload has to preserve.
        Assert.True(await store.BlobExistsAsync("empty"));
        Assert.Equal(Array.Empty<byte>(), await store.GetBlobAsync("empty"));
        Assert.Equal(0, await store.BlobLengthAsync("empty"));
    }

    [Fact]
    public async Task PutBlobAsync_SingleByte_RoundTrips()
    {
        var store = await CreateStoreWithBlobTableAsync();

        await store.PutBlobAsync("one", new byte[] { 42 });

        Assert.Equal(new byte[] { 42 }, await store.GetBlobAsync("one"));
        Assert.Equal(1, await store.BlobLengthAsync("one"));
    }

    [Fact]
    public async Task PutBlobAsync_OverwritingWithAnEmptyPayload_LeavesThePayloadEmptyNotStale()
    {
        var store = await CreateStoreWithBlobTableAsync();
        await store.PutBlobAsync("shrinking", new byte[] { 1, 2, 3, 4 });

        await store.PutBlobAsync("shrinking", ReadOnlyMemory<byte>.Empty);

        Assert.Equal(Array.Empty<byte>(), await store.GetBlobAsync("shrinking"));
        Assert.Equal(0, await store.BlobLengthAsync("shrinking"));
    }

    [Fact]
    public async Task PutAndGetBlob_MultiMegabytePayload_RoundTripsThroughTheByteArrayPath()
    {
        // The streaming path already covers a payload past the copy buffer; this is the
        // materializing overload, over enough data to span SQLite's overflow pages.
        var store = await CreateStoreWithBlobTableAsync();
        var payload = new byte[4 * 1024 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await store.PutBlobAsync("large", payload);

        Assert.Equal(payload.Length, await store.BlobLengthAsync("large"));
        Assert.Equal(payload, await store.GetBlobAsync("large"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlobOperations_WithABlankId_ThrowNamingTheId(string? id)
    {
        var store = await CreateStoreWithBlobTableAsync();

        foreach (var operation in BlankIdOperations(store, id!))
        {
            var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() => operation());
            Assert.Equal("id", exception.ParamName);
        }
    }

    private static IEnumerable<Func<Task>> BlankIdOperations(IDocumentStore store, string id) =>
    [
        () => store.PutBlobAsync(id, new byte[] { 1 }),
        () => store.GetBlobAsync(id),
        () => store.DeleteBlobAsync(id),
        () => store.BlobExistsAsync(id),
        () => store.BlobLengthAsync(id),
        () => store.GetBlobInfoAsync(id),
    ];

    [Fact]
    public async Task BlobOperations_OnAMissingId_ReportAbsenceRatherThanThrowing()
    {
        var store = await CreateStoreWithBlobTableAsync();

        Assert.Null(await store.GetBlobAsync("absent"));
        Assert.Null(await store.BlobLengthAsync("absent"));
        Assert.Null(await store.GetBlobInfoAsync("absent"));
        Assert.False(await store.BlobExistsAsync("absent"));
        Assert.False(await store.DeleteBlobAsync("absent"));
    }

    private sealed record BlobMeta(string Kind, int SampleCount);
}
