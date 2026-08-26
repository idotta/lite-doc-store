using LiteDocumentStore.Exceptions;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Blob metadata: recording a content type, reading a blob's length, timestamps and version
/// without touching its payload, listing blobs by id prefix, and the compare-and-swap writes that
/// bring blobs inside the optimistic-concurrency model.
/// </summary>
/// <remarks>
/// A file database, for the same reason as <see cref="BlobStreamingExample"/>.
/// </remarks>
internal static class BlobMetadataExample
{
    public static async Task RunAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lds-blobmeta-{Guid.NewGuid():N}.db");

        try
        {
            await using var store = await new DocumentStoreFactory()
                .CreateAsync(DocumentStoreOptions.ForFile(databasePath));

            // Creates the table, and adds the metadata columns to one created by an older version
            // of the library. It logs a warning if that upgrade leaves the payload column ahead of
            // the metadata — RebuildBlobTableAsync copies the table into the current layout.
            await store.CreateBlobTableAsync();
            Console.WriteLine($"Rebuild needed             => {await store.RebuildBlobTableAsync()}");

            // A content type travels with the payload, through an options overload.
            await store.PutBlobAsync(
                "user/42/avatar.png",
                new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                new BlobWriteOptions { ContentType = "image/png" });

            await store.PutBlobAsync(
                "user/42/resume.pdf",
                new MemoryStream(new byte[64_000]),
                64_000,
                new BlobWriteOptions { ContentType = "application/pdf" });

            await store.PutBlobAsync("user/7/avatar.png", new byte[] { 1, 2, 3 },
                new BlobWriteOptions { ContentType = "image/png" });

            // Everything a Content-Length / Content-Type response header needs, with no payload
            // read: the length comes from the row header, not the bytes.
            var info = await store.GetBlobInfoAsync("user/42/resume.pdf");
            Console.WriteLine(
                $"Metadata, no payload read  => {info!.Length} bytes, {info.ContentType}, " +
                $"v{info.Version}, written {info.UpdatedAt:HH:mm:ss}");

            // A prefix is a key range, not a pattern: case-sensitive, wildcards are literal, and
            // it seeks the primary-key index instead of scanning.
            foreach (var blob in await store.ListBlobsAsync("user/42/"))
            {
                Console.WriteLine($"  listed                   => {blob.Id} ({blob.Length} bytes, {blob.ContentType})");
            }

            Console.WriteLine($"Paged (skip 1, take 1)     => " +
                string.Join(", ", (await store.ListBlobsAsync(skip: 1, take: 1)).Select(b => b.Id)));

            // Compare-and-swap, exactly as for documents. 0 means "insert, the id must be free".
            var version = await store.PutBlobWithVersionAsync("user/42/avatar.png", new byte[] { 9 }, 1);
            Console.WriteLine($"Overwrote v1               => now v{version}");

            try
            {
                // Someone else already moved it to v2; this call still believes in v1.
                await store.PutBlobWithVersionAsync("user/42/avatar.png", new byte[] { 8 }, 1);
            }
            catch (ConcurrencyException ex)
            {
                Console.WriteLine(
                    $"Stale write rejected       => {ex.Kind}, expected v{ex.ExpectedVersion}, " +
                    $"stored v{ex.ActualVersion}");
            }

            // An overwrite keeps the creation time and advances the update time, so "first seen"
            // and "last written" stay distinguishable.
            var avatar = await store.GetBlobInfoAsync("user/42/avatar.png");
            Console.WriteLine(
                $"Created vs updated         => {avatar!.CreatedAt:HH:mm:ss.fff} / {avatar.UpdatedAt:HH:mm:ss.fff}");

            // The guarded delete refuses to drop a version the caller has not seen.
            await store.DeleteBlobWithVersionAsync("user/7/avatar.png", 1);
            Console.WriteLine($"Guarded delete             => {await store.ListBlobsAsync() is { Count: 2 }}");
        }
        finally
        {
            Cleanup(databasePath);
            Cleanup($"{databasePath}-wal");
            Cleanup($"{databasePath}-shm");
        }
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
                // The temp directory keeps it.
            }
        }
    }
}
