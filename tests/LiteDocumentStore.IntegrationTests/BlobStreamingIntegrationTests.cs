using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for streamed blob I/O against real SQLite: exact-length enforcement, the
/// self-owning read stream and its lifetime, and what survives a failed overwrite.
/// </summary>
[Trait("Category", "Integration")]
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

    /// <summary>
    /// <c>PRAGMA busy_timeout</c> for the two lock tests, deliberately far longer than either of
    /// them may take: if SQLite's busy handler were invoked for the conflict below, one
    /// <c>sqlite3_step</c> would block inside SQLite for this long, and nothing above it could cut
    /// that short.
    /// </summary>
    private const int BusyHandlerBudgetMs = 30_000;

    /// <summary>
    /// The writer's command timeout, in seconds — the window Microsoft.Data.Sqlite re-runs a
    /// locked statement in. One second is the provider's smallest usable value (0 means retry
    /// forever, measured).
    /// </summary>
    private const int WriterCommandTimeoutSeconds = 1;

    private const int SqliteLocked = 6;              // SQLITE_LOCKED
    private const int SqliteLockedSharedCache = 262; // SQLITE_LOCKED_SHAREDCACHE

    private static async Task<IDocumentStore> CreateInMemoryStoreAsync()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.BusyTimeoutMs = BusyHandlerBudgetMs;

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateBlobTableAsync();
        await store.CreateTableAsync<Doc>();
        return store;
    }

    /// <summary>
    /// Inserts a blob row over a pooled connection — a different connection from the read
    /// stream's, which is what makes it a writer contending with the stream's read lock. The
    /// command timeout is set explicitly so the provider's retry window is decoupled from
    /// <c>busy_timeout</c>, which the store otherwise derives it from.
    /// </summary>
    private static Task<int> InsertBlobRowAsync(IDocumentStore store, string id) =>
        store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = WriterCommandTimeoutSeconds;
            command.CommandText =
                "INSERT INTO [__store_blobs] (id, content_type, created_at, updated_at, version, data) " +
                "VALUES (@Id, NULL, 0, 0, 1, x'00')";
            command.Parameters.AddWithValue("@Id", id);
            return await command.ExecuteNonQueryAsync(ct);
        });

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
        // The point of the unpooled connection: every stream slot taken, and ordinary operations
        // still run rather than queueing behind them.
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1]), 1);

        var poolSize = ((DocumentStore)store).MaxPoolSize;
        var streams = new List<Stream>();

        try
        {
            for (int i = 0; i < poolSize; i++)
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
    public async Task OpenBlobReadAsync_BeyondTheStreamBound_ThrowsAndRecoversOnDisposal()
    {
        // Unbounded stream connections would be an exhaustion path with no signal; the bound is
        // separate from the operation pool so the two cannot starve each other.
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1]), 1);

        var poolSize = ((DocumentStore)store).MaxPoolSize;
        var streams = new List<Stream>();

        try
        {
            for (int i = 0; i < poolSize; i++)
            {
                streams.Add((await store.OpenBlobReadAsync("b"))!);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.OpenBlobReadAsync("b", cts.Token));

            // Freeing one slot lets the next caller through.
            await streams[0].DisposeAsync();
            streams.RemoveAt(0);

            await using var recovered = await store.OpenBlobReadAsync("b");
            Assert.NotNull(recovered);
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
    public async Task OpenBlobReadAsync_MissingId_ReleasesItsStreamSlot()
    {
        // The null path has to release, or a store would run out of slots by looking for blobs
        // that are not there.
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", new MemoryStream([1]), 1);

        for (int i = 0; i < ((DocumentStore)store).MaxPoolSize + 2; i++)
        {
            Assert.Null(await store.OpenBlobReadAsync("absent"));
        }

        await using var stream = await store.OpenBlobReadAsync("b");
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task PutBlobAsync_SeekableSourceShorterThanTheDeclaredLength_ThrowsBeforeWriting()
    {
        await using var store = await CreateFileStoreAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutBlobAsync("short", new MemoryStream([1, 2, 3]), 10));
        Assert.Equal("length", ex.ParamName);
    }

    [Fact]
    public async Task PutBlobAsync_SeekableSourceLongerThanTheDeclaredLength_ThrowsBeforeWriting()
    {
        await using var store = await CreateFileStoreAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutBlobAsync("long", new MemoryStream([1, 2, 3, 4, 5]), 3));
        Assert.Equal("length", ex.ParamName);
    }

    [Fact]
    public async Task PutBlobAsync_SeekableSourceMeasuredFromItsCurrentPosition()
    {
        await using var store = await CreateFileStoreAsync();
        var source = new MemoryStream([0, 1, 2, 3, 4, 5]);
        source.Position = 2;

        await store.PutBlobAsync("b", source, 4);

        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_NonSeekableSourceEndingEarly_Throws()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => store.PutBlobAsync("short", new NonSeekableStream([1, 2, 3]), 10));
    }

    [Fact]
    public async Task PutBlobAsync_NonSeekableSource_ConsumesExactlyTheDeclaredLengthAndNoMore()
    {
        // A read past the declared length would block indefinitely on a live network stream or
        // pipe, and would swallow a byte belonging to whatever follows in a framed one.
        await using var store = await CreateFileStoreAsync();
        var source = new NonSeekableStream([1, 2, 3, 4, 5]);

        await store.PutBlobAsync("b", source, 3);

        Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetBlobAsync("b"));
        Assert.Equal(3, source.BytesRead);
        Assert.Equal(new byte[] { 4, 5 }, source.ReadRemaining());
    }

    [Fact]
    public async Task PutBlobAsync_ZeroLengthFromANonSeekableSource_DoesNotReadIt()
    {
        // Even a zero-length blob used to probe for one more byte, which blocks on a stream that
        // is open but has nothing to say yet.
        await using var store = await CreateFileStoreAsync();
        var source = new NonSeekableStream([1, 2, 3]);

        await store.PutBlobAsync("b", source, 0);

        Assert.Equal(0, source.BytesRead);
        Assert.Equal(0, await store.BlobLengthAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_LengthMismatch_StoresNothing()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => store.PutBlobAsync("short", new NonSeekableStream([1, 2, 3]), 10));

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
            () => store.PutBlobAsync("b", new NonSeekableStream(Payload(50)), 300));

        Assert.Equal(original, await store.GetBlobAsync("b"));
    }

    [Fact]
    public async Task PutBlobAsync_FailedInsideACallersTransaction_CannotBeCommitted()
    {
        // The savepoint is what makes this true. The reserve statement has already replaced the
        // payload with zero bytes by the time the copy fails, so without one a caller who catches
        // and commits would persist a corrupt blob.
        await using var store = await CreateFileStoreAsync();
        var original = Payload(200);
        await store.PutBlobAsync("b", new MemoryStream(original), original.Length);

        await using (var txn = await store.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => txn.PutBlobAsync("b", new NonSeekableStream(Payload(50)), 300));

            // The caller swallows the failure and commits everything else.
            await txn.UpsertAsync("doc", new Doc("unrelated"));
            await txn.CommitAsync();
        }

        Assert.Equal(original, await store.GetBlobAsync("b"));
        Assert.NotNull(await store.GetAsync<Doc>("doc"));
    }

    [Fact]
    public async Task PutBlobAsync_FailedInsideACallersTransaction_LeavesTheTransactionUsable()
    {
        await using var store = await CreateFileStoreAsync();

        await store.ExecuteInTransactionAsync(async txn =>
        {
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => txn.PutBlobAsync("b", new NonSeekableStream([1, 2]), 64));

            // Rolling back to the savepoint must not have ended the caller's transaction.
            await txn.PutBlobAsync("b", new MemoryStream([9, 9, 9]), 3);
            await txn.UpsertAsync("doc", new Doc("after-failure"));
        });

        Assert.Equal(new byte[] { 9, 9, 9 }, await store.GetBlobAsync("b"));
        Assert.NotNull(await store.GetAsync<Doc>("doc"));
    }

    [Fact]
    public async Task PutBlobAsync_SucceedingInsideACallersTransaction_StillRollsBackWithIt()
    {
        // The savepoint is released on success, so the write remains part of the caller's
        // transaction rather than being committed independently by it.
        await using var store = await CreateFileStoreAsync();

        await using (var txn = await store.BeginTransactionAsync())
        {
            await txn.PutBlobAsync("b", new MemoryStream([1, 2, 3]), 3);
            await txn.RollbackAsync();
        }

        Assert.False(await store.BlobExistsAsync("b"));
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
    /// Pins what an open read stream costs on a shared-cache in-memory database: the deferred
    /// BEGIN takes no lock, but the rowid lookup behind the stream does, and under shared cache
    /// that lock is table-level, so a blob-table write fails with SQLITE_LOCKED /
    /// SQLITE_LOCKED_SHAREDCACHE. It is not a wait: SQLite does not invoke the busy handler for a
    /// shared-cache table conflict, so the 30 s <c>busy_timeout</c> configured here never runs and
    /// the call is bounded by the provider's 1 s retry window instead. Document writes and blob
    /// reads are unaffected, and disposing the stream releases the lock. The file + WAL control
    /// below is what makes this discriminating.
    /// </summary>
    [Fact]
    public async Task OpenBlobReadAsync_OnASharedCacheInMemoryStore_LocksTheBlobTableAgainstWriters()
    {
        await using var store = await CreateInMemoryStoreAsync();
        await store.PutBlobAsync("b", Payload(64));

        var stream = await store.OpenBlobReadAsync("b");
        Assert.NotNull(stream);

        var stopwatch = Stopwatch.StartNew();
        var blocked = await Assert.ThrowsAsync<SqliteException>(() => InsertBlobRowAsync(store, "other"));
        stopwatch.Stop();

        Assert.Equal(SqliteLocked, blocked.SqliteErrorCode);
        Assert.Equal(SqliteLockedSharedCache, blocked.SqliteExtendedErrorCode);

        // SQLite returned the conflict rather than waiting on it. busy_timeout is 30 s and the
        // provider cannot interrupt a step that is blocked inside SQLite, so a busy-handler wait
        // would show up here as ~30 s. What bounds the call instead is the provider re-running the
        // statement for its 1 s command timeout, which is why the bound below is well above 1 s
        // and far below 30 s rather than near zero.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"expected the busy handler never to run; took {stopwatch.ElapsedMilliseconds} ms");

        // A document table is a different table, so its lock is not the one held.
        await store.UpsertAsync("d", new Doc("written while a blob stream was open"));
        Assert.NotNull(await store.GetAsync<Doc>("d"));

        // Reads of the blob table share the lock rather than conflicting with it.
        Assert.Equal(64, (await store.GetBlobAsync("b"))!.Length);

        // The lock belongs to the stream's read transaction, and the stream owns that transaction,
        // so the same write succeeds once the stream is disposed and nothing else has changed. A
        // test cannot dispose the inner SqliteBlob on its own — it is private to BlobReadStream —
        // so what is pinned here is the consumer-visible half: held while the stream lives,
        // released when the stream is disposed.
        await stream.DisposeAsync();

        Assert.Equal(1, await InsertBlobRowAsync(store, "after-disposal"));
    }

    /// <summary>
    /// The positive control for the test above: the same shape on a file database in WAL mode,
    /// where the writer's INSERT succeeds. Without it that test could be asserting something that
    /// fails everywhere and would still look like a pass.
    /// </summary>
    [Fact]
    public async Task OpenBlobReadAsync_OnAFileDatabaseInWalMode_LeavesBlobTableWritersRunning()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b", Payload(64));

        await using var stream = await store.OpenBlobReadAsync("b");
        Assert.NotNull(stream);

        Assert.Equal(1, await InsertBlobRowAsync(store, "other"));
        await store.UpsertAsync("d", new Doc("written while a blob stream was open"));
        Assert.Equal(64, (await store.GetBlobAsync("b"))!.Length);
    }

    /// <summary>
    /// A source that cannot be measured up front, and that records how much of it was consumed —
    /// the shape of a request body or a socket, where reading past the declared length is what
    /// would block.
    /// </summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public int BytesRead => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ReadRemaining() => data.AsSpan(_position).ToArray();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
