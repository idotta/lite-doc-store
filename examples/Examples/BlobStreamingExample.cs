namespace LiteDocumentStore.Examples;

/// <summary>
/// Raw binary payloads: the whole-array API, the streaming API that never materializes the
/// payload, the length probe, and storing a blob atomically with the document that describes it.
/// </summary>
/// <remarks>
/// This sample uses a file database rather than <see cref="DocumentStoreOptions.ForInMemory"/>.
/// A shared-cache in-memory database takes table-level locks, so a read stream held open across
/// other writes would block them — which is not a property of the streaming API, only of
/// in-memory SQLite, and not what a sample should demonstrate.
/// </remarks>
internal static class BlobStreamingExample
{
    internal sealed record Attachment(string Id, string FileName, string ContentType, long Length);

    public static async Task RunAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lds-blobs-{Guid.NewGuid():N}.db");
        var sourcePath = Path.Combine(Path.GetTempPath(), $"lds-blobs-{Guid.NewGuid():N}.bin");

        try
        {
            await File.WriteAllBytesAsync(sourcePath, BuildPayload(2 * 1024 * 1024));

            await using var store = await new DocumentStoreFactory()
                .CreateAsync(DocumentStoreOptions.ForFile(databasePath));

            await store.CreateBlobTableAsync();
            await store.CreateTableAsync<Attachment>();

            // Small payloads: hand over the bytes and be done.
            await store.PutBlobAsync("icon", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            Console.WriteLine($"Small blob                 => {await store.BlobLengthAsync("icon")} bytes");

            // Large payloads: stream from the source, so neither side is ever fully in memory.
            // The length is explicit because SQLite reserves the row at exactly that size before
            // the first byte is written — incremental blob I/O cannot resize a blob afterwards.
            var file = new FileInfo(sourcePath);
            await using (var source = File.OpenRead(sourcePath))
            {
                await store.PutBlobAsync("report", source, file.Length);
            }

            Console.WriteLine($"Streamed in                => {await store.BlobLengthAsync("report")} bytes");

            // Reading back: dispose the stream. It owns a connection and an open read snapshot
            // until you do — 'await using', or handing it to something that disposes it, such as
            // an ASP.NET File(stream, contentType) result.
            await using (var blob = await store.OpenBlobReadAsync("report"))
            {
                if (blob is null)
                {
                    Console.WriteLine("Blob missing");
                    return;
                }

                // Seekable, so a range read costs nothing but the bytes it asks for.
                blob.Seek(1_000_000, SeekOrigin.Begin);
                var window = new byte[8];
                await blob.ReadExactlyAsync(window);
                Console.WriteLine($"Bytes at offset 1,000,000  => {Convert.ToHexString(window)}");

                blob.Seek(0, SeekOrigin.Begin);
                Console.WriteLine($"Checksum over the stream   => {await ChecksumAsync(blob)}");
            }

            // A missing id is null, not an exception — and nothing to dispose.
            Console.WriteLine($"Absent blob                => {await store.OpenBlobReadAsync("nope") is null}");

            // The declared length is enforced in both directions rather than silently padding or
            // truncating the stored payload.
            try
            {
                await store.PutBlobAsync("truncated", new MemoryStream([1, 2, 3]), 64);
            }
            catch (EndOfStreamException ex)
            {
                Console.WriteLine($"Short source rejected      => {ex.Message}");
            }

            // A failed write leaves the previous payload alone: the reserve and the copy run in
            // one transaction, so the row is never left holding the zero bytes it was sized to.
            Console.WriteLine($"Report still intact        => {await store.BlobLengthAsync("report")} bytes");

            // A blob and the document describing it commit together.
            await store.ExecuteInTransactionAsync(async transaction =>
            {
                await using var source = File.OpenRead(sourcePath);
                await transaction.PutBlobAsync("invoice", source, file.Length);
                await transaction.UpsertAsync(
                    "invoice",
                    new Attachment("invoice", "invoice.pdf", "application/pdf", file.Length));
            });

            var metadata = await store.GetAsync<Attachment>("invoice");
            Console.WriteLine(
                $"Committed together         => {metadata!.FileName}, " +
                $"{await store.BlobLengthAsync("invoice")} bytes");
        }
        finally
        {
            Cleanup(sourcePath);
            Cleanup(databasePath);
            Cleanup($"{databasePath}-wal");
            Cleanup($"{databasePath}-shm");
        }
    }

    private static byte[] BuildPayload(int length)
    {
        var payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private static async Task<string> ChecksumAsync(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash)[..16];
    }

    private static void Cleanup(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A handle SQLite has not released yet; the temp directory keeps it.
            }
        }
    }
}
