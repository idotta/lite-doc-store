using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the <see cref="Stream"/> surface of a blob read stream: the synchronous
/// read overloads, seeking from every origin, and the members a read-only stream has to refuse.
/// </summary>
/// <remarks>
/// The lifetime and slot-accounting behaviour of the same stream lives in
/// <see cref="BlobStreamingIntegrationTests"/>; this file covers the <c>Stream</c> contract it
/// implements on top of SQLite's incremental blob I/O.
/// </remarks>
public class BlobReadStreamIntegrationTests : IAsyncLifetime
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

    private async Task<IDocumentStore> CreateFileStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-readstream-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForFile(path));
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

    private async Task<(IDocumentStore Store, Stream Stream, byte[] Payload)> OpenAsync(int length = 1024)
    {
        var store = await CreateFileStoreAsync();
        var payload = Payload(length);
        await store.PutBlobAsync("b/1", payload);

        var stream = await store.OpenBlobReadAsync("b/1");
        Assert.NotNull(stream);
        return (store, stream, payload);
    }

    [Fact]
    public async Task Capabilities_AreReadOnlyAndSeekable()
    {
        var (store, stream, payload) = await OpenAsync();
        await using var _ = store;
        await using var s = stream;

        Assert.True(s.CanRead);
        Assert.True(s.CanSeek);
        Assert.False(s.CanWrite);
        Assert.Equal((long)payload.Length, s.Length);
        Assert.Equal(0L, s.Position);
    }

    [Fact]
    public async Task Read_ByteArrayOverload_FillsTheBufferAndAdvancesThePosition()
    {
        var (store, stream, payload) = await OpenAsync();
        await using var _ = store;
        await using var s = stream;

        var buffer = new byte[payload.Length];
        int total = 0;
        int read;

        // The offset/count overload, which the Memory/Span ones do not go through.
        while ((read = s.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, buffer);
        Assert.Equal((long)payload.Length, s.Position);
        Assert.Equal(0, s.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task Read_SpanOverload_ReadsFromTheCurrentPosition()
    {
        var (store, stream, payload) = await OpenAsync();
        await using var _ = store;
        await using var s = stream;

        s.Position = 16;
        Span<byte> buffer = new byte[8];

        Assert.Equal(8, s.Read(buffer));
        Assert.Equal(payload.AsSpan(16, 8).ToArray(), buffer.ToArray());
    }

    [Fact]
    public async Task ReadByte_ReturnsEachByteThenMinusOneAtTheEnd()
    {
        var (store, stream, payload) = await OpenAsync(length: 3);
        await using var _ = store;
        await using var s = stream;

        Assert.Equal(payload[0], (byte)s.ReadByte());
        Assert.Equal(payload[1], (byte)s.ReadByte());
        Assert.Equal(payload[2], (byte)s.ReadByte());
        Assert.Equal(-1, s.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_ReadsFromTheCurrentPosition()
    {
        var (store, stream, payload) = await OpenAsync();
        await using var _ = store;
        await using var s = stream;

        s.Position = 32;
        var buffer = new byte[16];

        // The offset/count overload on purpose: CA1835 would route the test past the member it
        // exists to cover, and the blob read is exact, so CA2022's partial-read worry does not apply.
#pragma warning disable CA1835, CA2022
        Assert.Equal(16, await s.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
#pragma warning restore CA1835, CA2022
        Assert.Equal(payload.AsSpan(32, 16).ToArray(), buffer);
    }

    [Fact]
    public async Task ReadAsync_WithAnAlreadyCancelledToken_ThrowsAndDoesNotMove()
    {
        var (store, stream, _) = await OpenAsync();
        await using var st = store;
        await using var s = stream;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var buffer = new byte[8];

        // Neither read returns a count to check - both fault before reading anything.
#pragma warning disable CA1835, CA2022
        await Assert.ThrowsAsync<TaskCanceledException>(() => s.ReadAsync(buffer, 0, buffer.Length, cts.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await s.ReadAsync(buffer.AsMemory(), cts.Token));
#pragma warning restore CA1835, CA2022
        Assert.Equal(0L, s.Position);
    }

    [Fact]
    public async Task Seek_FromEveryOrigin_MovesThePosition()
    {
        var (store, stream, payload) = await OpenAsync(length: 100);
        await using var _ = store;
        await using var s = stream;

        Assert.Equal(10L, s.Seek(10, SeekOrigin.Begin));
        Assert.Equal(payload[10], (byte)s.ReadByte());

        Assert.Equal(20L, s.Seek(9, SeekOrigin.Current));
        Assert.Equal(payload[20], (byte)s.ReadByte());

        Assert.Equal(99L, s.Seek(-1, SeekOrigin.End));
        Assert.Equal(payload[99], (byte)s.ReadByte());
        Assert.Equal(-1, s.ReadByte());
    }

    [Fact]
    public async Task Flush_IsANoOpOnAReadOnlyStream()
    {
        var (store, stream, _) = await OpenAsync();
        await using var st = store;
        await using var s = stream;

        s.Flush();
        await s.FlushAsync(CancellationToken.None);

        Assert.Equal(0L, s.Position);
    }

    [Fact]
    public async Task WriteMembers_Throw()
    {
        var (store, stream, _) = await OpenAsync();
        await using var st = store;
        await using var s = stream;

        var buffer = new byte[4];

        Assert.Throws<NotSupportedException>(() => s.SetLength(4));
        Assert.Throws<NotSupportedException>(() => s.Write(buffer, 0, buffer.Length));
        Assert.Throws<NotSupportedException>(() => s.Write(buffer.AsSpan()));
    }

    [Fact]
    public async Task EveryReadMember_AfterDisposal_Throws()
    {
        var (store, stream, _) = await OpenAsync();
        await using var st = store;

        await stream.DisposeAsync();
        // Idempotent: the second disposal must not double-release the stream slot.
        await stream.DisposeAsync();
        stream.Dispose();

        var buffer = new byte[4];

        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer.AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));

        // The async overloads return a faulted task rather than throwing synchronously.
#pragma warning disable CA1835, CA2022
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None));
#pragma warning restore CA1835, CA2022
    }

    [Fact]
    public async Task Dispose_Synchronously_ClosesTheStreamAndIsIdempotent()
    {
        var (store, stream, _) = await OpenAsync();
        await using var st = store;

        // The synchronous disposal path, which DisposeAsync does not go through.
        stream.Dispose();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());

        // The slot the stream held is back, or this would block until the open bound times out.
        await using var next = await store.OpenBlobReadAsync("b/1");
        Assert.NotNull(next);
    }

    [Fact]
    public async Task AbandonedStream_ReleasesItsSlotWhenFinalized()
    {
        // A caller who never disposes costs one handle until finalization - but not a stream slot
        // forever, which would make the store refuse every later OpenBlobReadAsync.
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("b/1", Payload(16));

        var slots = ((DocumentStore)store).MaxPoolSize;

        for (int i = 0; i < slots + 1; i++)
        {
            await AbandonAStreamAsync(store);

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = await store.OpenBlobReadAsync("b/1", cts.Token);
        Assert.NotNull(stream);
    }

    // Separated so the stream is unreachable by the time the caller collects: a local in the test
    // method would still be rooted.
    private static async Task AbandonAStreamAsync(IDocumentStore store)
    {
        var stream = await store.OpenBlobReadAsync("b/1");
        Assert.NotNull(stream);
        Assert.Equal(16L, stream.Length);
    }

    [Fact]
    public async Task CopyToAsync_ReadsTheWholePayload()
    {
        var (store, stream, payload) = await OpenAsync(length: 200_000);
        await using var st = store;
        await using var s = stream;

        using var destination = new MemoryStream();
        await s.CopyToAsync(destination, CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
    }
}
