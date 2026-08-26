using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for streamed blob I/O against real SQLite: exact-length enforcement, the
/// self-owning read stream and its lifetime, and what survives a failed overwrite.
/// </summary>
public class BlobStreamingIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _databasePaths = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        // A read stream holds its connection until disposed or finalized.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _databasePaths)
        {
            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // A handle the provider has not finalized yet; the temp directory keeps it.
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed record Doc(string Name);

    private async Task<IDocumentStore> CreateFileStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-blobstream-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForFile(path));
        await store.CreateBlobTableAsync();
        await store.CreateTableAsync<Doc>();
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

    [Fact]
    public async Task PutBlobAsync_FromAStream_RoundTripsAPayloadLargerThanTheCopyBuffer()
    {
        await using var store = await CreateFileStoreAsync();
        var payload = Payload(500_000);

        await store.PutBlobAsync("big", new MemoryStream(payload), payload.Length);

        await using var stream = await store.OpenBlobReadAsync("big");
        Assert.NotNull(stream);
        Assert.Equal(payload.Length, stream.Length);

        using var read = new MemoryStream();
        await stream.CopyToAsync(read);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task OpenBlobReadAsync_SupportsSeekingToReadARange()
    {
        await using var store = await CreateFileStoreAsync();
        var payload = Payload(1000);
        await store.PutBlobAsync("ranged", new MemoryStream(payload), payload.Length);

        await using var stream = await store.OpenBlobReadAsync("ranged");
        Assert.NotNull(stream);
        Assert.True(stream.CanSeek);

        stream.Seek(600, SeekOrigin.Begin);
        var buffer = new byte[100];
        await stream.ReadExactlyAsync(buffer);

        Assert.Equal(payload.AsSpan(600, 100).ToArray(), buffer);
    }

    [Fact]
    public async Task OpenBlobReadAsync_MissingId_ReturnsNull()
    {
        await using var store = await CreateFileStoreAsync();

        Assert.Null(await store.OpenBlobReadAsync("absent"));
    }

    [Fact]
    public async Task OpenBlobReadAsync_BlankId_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenBlobReadAsync(" "));
    }

    [Fact]
    public async Task OpenBlobReadAsync_ZeroLengthBlob_ReturnsAnEmptyStream()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("empty", new MemoryStream([]), 0);

        await using var stream = await store.OpenBlobReadAsync("empty");
        Assert.NotNull(stream);
        Assert.Equal(0, stream.Length);
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadStream_AfterDisposal_Throws()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1, 2, 3]), 3);

        var stream = await store.OpenBlobReadAsync("b");
        Assert.NotNull(stream);
        await stream.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public async Task ReadStream_OutlivesTheStore()
    {
        // The stream owns its own connection, so it is tied to neither the pool nor the store.
        var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([7, 8, 9]), 3);

        var stream = await store.OpenBlobReadAsync("b");
        Assert.NotNull(stream);
        await store.DisposeAsync();

        await using (stream)
        {
            var buffer = new byte[3];
            await stream.ReadExactlyAsync(buffer);
            Assert.Equal(new byte[] { 7, 8, 9 }, buffer);
        }
    }

    [Fact]
    public async Task OpenReadStreams_DoNotConsumePooledConnections()
    {
        // The point of the unpooled connection: more open streams than the pool holds, and normal
        // operations still run.
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1]), 1);

        var poolSize = ((DocumentStore)store).MaxPoolSize;
        var streams = new List<Stream>();

        try
        {
            for (int i = 0; i < poolSize + 2; i++)
            {
                var stream = await store.OpenBlobReadAsync("b");
                Assert.NotNull(stream);
                streams.Add(stream);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(1, await store.BlobLengthAsync("b", cts.Token));
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task PutBlobAsync_SourceShorterThanTheDeclaredLength_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => store.PutBlobAsync("short", new MemoryStream([1, 2, 3]), 10));
    }

    [Fact]
    public async Task PutBlobAsync_SourceLongerThanTheDeclaredLength_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutBlobAsync("long", new MemoryStream([1, 2, 3, 4, 5]), 3));
        Assert.Equal("length", ex.ParamName);
    }

    [Fact]
    public async Task PutBlobAsync_LengthMismatch_StoresNothing()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => store.PutBlobAsync("short", new MemoryStream([1, 2, 3]), 10));

        Assert.False(await store.BlobExistsAsync("short"));
    }

    [Fact]
    public async Task PutBlobAsync_FailedOverwrite_LeavesThePreviousPayloadIntact()
    {
        // The reason the store-level write takes its own transaction: without it the row would be
        // left holding the zeroblob the reserve statement wrote.
        await using var store = await CreateFileStoreAsync();
        var original = Payload(200);
        await store.PutBlobAsync("b", new MemoryStream(original), original.Length);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => store.PutBlobAsync("b", new MemoryStream(Payload(50)), 300));

        Assert.Equal(original, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_CancelledMidCopy_LeavesThePreviousPayloadIntact()
    {
        await using var store = await CreateFileStoreAsync();
        var original = Payload(300);
        await store.PutBlobAsync("b", new MemoryStream(original), original.Length);

        using var cts = new CancellationTokenSource();
        var source = new CancellingStream(Payload(500_000), cts, cancelAfter: 100_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.PutBlobAsync("b", source, 500_000, cts.Token));

        Assert.Equal(original, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_PreCancelledToken_Throws()
    {
        await using var store = await CreateFileStoreAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.PutBlobAsync("b", new MemoryStream([1, 2, 3]), 3, cts.Token));

        Assert.False(await store.BlobExistsAsync("b"));
    }

    [Fact]
    public async Task OpenBlobReadAsync_PreCancelledToken_ThrowsAndLeaksNothing()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1]), 1);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenBlobReadAsync("b", cts.Token));

        // The store is still usable, so nothing was left holding a lock.
        Assert.Equal(1, await store.BlobLengthAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_Overwrite_ShrinksTheStoredPayload()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream(Payload(1000)), 1000);

        await store.PutBlobAsync("b", new MemoryStream([1, 2]), 2);

        Assert.Equal(2, await store.BlobLengthAsync("b"));
        Assert.Equal(new byte[] { 1, 2 }, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_NegativeLength_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.PutBlobAsync("b", new MemoryStream([1]), -1));
    }

    [Fact]
    public async Task PutBlobAsync_LengthAboveTheLimit_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.PutBlobAsync("b", new MemoryStream([1]), BlobLimits.MaxBlobLength + 1));
    }

    [Fact]
    public async Task PutBlobAsync_NullSource_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PutBlobAsync("b", null!, 0));
    }

    [Fact]
    public async Task PutBlobAsync_UnreadableSource_Throws()
    {
        await using var store = await CreateFileStoreAsync();
        var source = new MemoryStream([1, 2, 3]);
        await source.DisposeAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.PutBlobAsync("b", source, 3));
        Assert.Equal("source", ex.ParamName);
    }

    [Fact]
    public async Task BlobLengthAsync_MissingId_ReturnsNull()
    {
        await using var store = await CreateFileStoreAsync();

        Assert.Null(await store.BlobLengthAsync("absent"));
    }

    [Fact]
    public async Task PutBlobAsync_OnATransaction_CommitsWithTheDocument()
    {
        await using var store = await CreateFileStoreAsync();
        var payload = Payload(300);

        await store.ExecuteInTransactionAsync(async txn =>
        {
            await txn.UpsertAsync("doc", new Doc("with-blob"));
            await txn.PutBlobAsync("b", new MemoryStream(payload), payload.Length);
            Assert.Equal(payload.Length, await txn.BlobLengthAsync("b"));
        });

        Assert.NotNull(await store.GetAsync<Doc>("doc"));
        Assert.Equal(payload, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_OnARolledBackTransaction_StoresNothing()
    {
        await using var store = await CreateFileStoreAsync();

        await using (var txn = await store.BeginTransactionAsync())
        {
            await txn.PutBlobAsync("b", new MemoryStream([1, 2, 3]), 3);
            await txn.RollbackAsync();
        }

        Assert.False(await store.BlobExistsAsync("b"));
    }

    /// <summary>
    /// A source that cancels the operation part-way through being read, to exercise a write that
    /// fails after some bytes have already reached the reserved blob.
    /// </summary>
    private sealed class CancellingStream(byte[] data, CancellationTokenSource cts, int cancelAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= cancelAfter)
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
