# Blobs

## Contents
- [Signatures](#signatures)
- [Basics](#basics)
- [Streaming write](#streaming-write)
- [Streaming read](#streaming-read)
- [Blob plus document, atomically](#blob-plus-document-atomically)
- [Compare-and-swap](#compare-and-swap)
- [Corrupt rows and legacy tables](#corrupt-rows-and-legacy-tables)
- [Pitfalls](#pitfalls)

Blobs are raw bytes stored verbatim (no JSONB) in the reserved table `__store_blobs`, keyed by
a string id. Use them for files, images and attachments. Every write bumps the blob's version.

## Signatures

```csharp
// IDocumentOperations (store and transaction)
Task CreateBlobTableAsync(CancellationToken ct = default);   // required first; idempotent
Task PutBlobAsync(string id, ReadOnlyMemory<byte> data, CancellationToken ct = default);
Task PutBlobAsync(string id, ReadOnlyMemory<byte> data, BlobWriteOptions options, CancellationToken ct = default);
Task PutBlobAsync(string id, Stream source, long length, CancellationToken ct = default);
Task PutBlobAsync(string id, Stream source, long length, BlobWriteOptions options, CancellationToken ct = default);
Task<long> PutBlobWithVersionAsync(string id, ReadOnlyMemory<byte> data, long expectedVersion, CancellationToken ct = default);
Task<long> PutBlobWithVersionAsync(string id, ReadOnlyMemory<byte> data, long expectedVersion, BlobWriteOptions options, CancellationToken ct = default);
Task<long> PutBlobWithVersionAsync(string id, Stream source, long length, long expectedVersion, CancellationToken ct = default);
Task<long> PutBlobWithVersionAsync(string id, Stream source, long length, long expectedVersion, BlobWriteOptions options, CancellationToken ct = default);
Task<byte[]?> GetBlobAsync(string id, CancellationToken ct = default);               // null when absent
Task<bool> DeleteBlobAsync(string id, CancellationToken ct = default);
Task DeleteBlobWithVersionAsync(string id, long expectedVersion, CancellationToken ct = default);
Task<bool> BlobExistsAsync(string id, CancellationToken ct = default);
Task<long?> BlobLengthAsync(string id, CancellationToken ct = default);              // no payload read
Task<BlobMetadata?> GetBlobMetadataAsync(string id, CancellationToken ct = default);  // no payload read
Task<IReadOnlyList<BlobMetadata>> ListBlobsAsync(string? idPrefix = null, int skip = 0, int? take = null, CancellationToken ct = default);

// IDocumentStore only
Task<Stream?> OpenBlobReadAsync(string id, CancellationToken ct = default);          // MUST dispose
Task<bool> RebuildBlobTableAsync(CancellationToken ct = default);

public sealed record BlobMetadata(string Id, long Length, string? ContentType,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, long Version);
public sealed class BlobWriteOptions { public string? ContentType { get; init; } }
public static class BlobLimits { public const long MaxBlobLength = 1_000_000_000L; }
```

## Basics

```csharp
await store.CreateBlobTableAsync();   // at startup; without it every blob call throws "no such table: __store_blobs"

await store.PutBlobAsync("user/42/avatar.png", pngBytes,           // byte[] converts implicitly
    new BlobWriteOptions { ContentType = "image/png" });

byte[]? bytes = await store.GetBlobAsync("user/42/avatar.png");
BlobMetadata? meta = await store.GetBlobMetadataAsync("user/42/avatar.png");
// meta.Length, meta.ContentType, meta.CreatedAt (first write), meta.UpdatedAt, meta.Version

foreach (var b in await store.ListBlobsAsync("user/42/"))           // id order
    Console.WriteLine($"{b.Id} {b.Length}");
var page = await store.ListBlobsAsync("user/", skip: 50, take: 50);

bool deleted = await store.DeleteBlobAsync("user/42/avatar.png");
```

- An overwrite replaces the content type and keeps `CreatedAt`. Timestamps come from SQLite's
  clock. They are null for rows written by old versions or by raw SQL.
- `ContentType = null` records no content type. A whitespace value throws `ArgumentException`.
- `ListBlobsAsync`'s `idPrefix` is a **case-sensitive key range, not a pattern**. `%`, `_`, `*`
  and `[` are literal, and `"User/"` does not match `"user/"`.
- Listing is by id only. Design ids hierarchically (`tenant/entity/name`) so prefixes do the
  filtering. There are no secondary indexes on blob metadata.
- `PutBlobAsync(id, data, default)` binds to the `CancellationToken` overload, not
  `BlobWriteOptions`. Pass the options explicitly when you mean them.

## Streaming write

```csharp
var file = new FileInfo(path);
await using (var source = File.OpenRead(path))
{
    await store.PutBlobAsync("report", source, file.Length,
        new BlobWriteOptions { ContentType = "application/pdf" });
}
```

`length` means "consume exactly this many bytes from the current position":
- **Seekable source**: `length` must equal `Length - Position`, or `ArgumentException` is thrown
  before any I/O. Rewind a `MemoryStream` you just wrote (`ms.Position = 0`).
- **Non-seekable source** (network, pipe): exactly `length` bytes are read and any extra is left
  unread, with no error. A source that ends early throws `EndOfStreamException`.
- Any failed write is atomic: the previous blob under that id stays intact. On the store the
  write uses its own transaction; inside a transaction it uses a SAVEPOINT.
- The store never disposes `source`.
- A `length` above `BlobLimits.MaxBlobLength` (1 GB), or a negative one, throws
  `ArgumentOutOfRangeException`.

ASP.NET Core upload: `await store.PutBlobAsync(id, request.Body, request.ContentLength!.Value, ...)`.
Validate `ContentLength` first.

## Streaming read

```csharp
await using (var blob = await store.OpenBlobReadAsync("report"))
{
    if (blob is null) return;                 // absent
    blob.Seek(1_000_000, SeekOrigin.Begin);   // seekable, read-only
    var window = new byte[8];
    await blob.ReadExactlyAsync(window);
}

// ASP.NET Core: the result disposes the stream after sending it
var meta = await store.GetBlobMetadataAsync(id);
var stream = await store.OpenBlobReadAsync(id);
return stream is null ? Results.NotFound()
    : Results.File(stream, meta?.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
```

- **You must dispose the stream.** It owns its own SQLite connection, outside the pool, and a
  read snapshot.
- Open streams are capped at `MaxPoolSize`. Opening another waits up to 30 s, then throws
  `TimeoutException`.
- While a stream is open:
  - With WAL on a file DB, other writers keep working, but the WAL cannot be truncated.
  - With `EnableWalMode = false` on a file DB, writes to **any** table from other connections
    fail with `SQLITE_BUSY`.
  - On an in-memory DB, writes to the blob table fail with `SQLITE_LOCKED`, and
    `BusyTimeoutMs` does not help.

  Read, then dispose promptly.
- The stream reads the snapshot taken when it opened.
- It is not available on `IDocumentTransaction`. Inside a transaction, use `GetBlobAsync`, which
  loads the whole payload.

## Blob plus document, atomically

```csharp
await store.ExecuteInTransactionAsync(async tx =>
{
    await using var source = File.OpenRead(path);
    await tx.PutBlobAsync("invoice-17", source, new FileInfo(path).Length);       // ON tx
    await tx.UpsertAsync("invoice-17", new Attachment("invoice-17", "invoice.pdf", "application/pdf", source.Length));
});
```

## Compare-and-swap

The rules are the same as for documents:
- `expectedVersion = 0` on a put means "insert, the id must be free" (else `AlreadyExists`). A
  row stored at version 0 (raw SQL) is updated and lifted to 1 instead.
- A non-zero value means "only if the stored version matches" (else `VersionMismatch`, or
  `DocumentNotFound` when the blob is missing).
- The return value is the stored version.
- `DeleteBlobWithVersionAsync` on a missing blob throws `ConcurrencyException`
  (`DocumentNotFound`), whereas `DeleteBlobAsync` returns false.
- A streamed CAS put checks the guard before any byte is written.

```csharp
using LiteDocumentStore.Exceptions;

long v = await store.PutBlobWithVersionAsync("doc.pdf", pdf, 0, new BlobWriteOptions { ContentType = "application/pdf" });
v = await store.PutBlobWithVersionAsync("doc.pdf", newPdf, v);
try { await store.PutBlobWithVersionAsync("doc.pdf", other, 1); }
catch (ConcurrencyException ex) { Console.WriteLine($"{ex.Kind}: stored v{ex.ActualVersion}"); }
await store.DeleteBlobWithVersionAsync("doc.pdf", v);
```

## Corrupt rows and legacy tables

- A blob row whose `data` is not a BLOB (only possible via raw SQL) makes the following throw
  `CorruptDataException`: `GetBlobAsync`, `OpenBlobReadAsync`, `BlobLengthAsync`,
  `GetBlobMetadataAsync` and `ListBlobsAsync` (the whole listing fails).
  - `BlobExistsAsync` still returns true.
  - Deleting or overwriting the row is the recovery.
- An empty blob (zero bytes) is legal.
- A blob table created by an older release is upgraded in place by `CreateBlobTableAsync`, which
  logs a warning when the payload column precedes the metadata columns. In that layout, metadata
  reads and listings become very slow on large blobs. Run `await store.RebuildBlobTableAsync()`
  once, during maintenance:
  - it copies everything in one transaction;
  - it needs disk space for two copies;
  - it holds the write lock while it runs;
  - it returns false when nothing needed doing.

## Pitfalls

- Never write blobs through raw SQL as TEXT. Use the blob API.
- Do not store large binary data inside documents (as `byte[]` -> base64). Use blobs, and keep
  the blob id in the document.
- A blank id throws `ArgumentException`. Ids are case-sensitive.
