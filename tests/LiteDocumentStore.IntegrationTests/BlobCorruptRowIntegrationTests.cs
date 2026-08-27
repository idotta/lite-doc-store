using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the blob corruption contract: a <c>__store_blobs</c> row whose
/// <c>data</c> column holds something other than a BLOB.
/// </summary>
/// <remarks>
/// <para>
/// The store's own DDL declares <c>data BLOB NOT NULL</c>, so such a row is only reachable
/// through raw SQL or on a table a consumer created themselves with a nullable or untyped
/// <c>data</c> column. Every test here therefore builds that table by hand — with the current
/// column set and <c>data</c> last, so <c>CreateBlobTableAsync</c> would neither add nor reorder
/// anything and the fixture isolates the payload, not the layout.
/// </para>
/// <para>
/// The six read paths used to disagree about such a row: it was simultaneously present
/// (<c>BlobExistsAsync</c>), absent (<c>GetBlobAsync</c>, <c>BlobLengthAsync</c>) and
/// unlistable, and three of them leaked a provider exception. Row presence now decides
/// not-found; the payload decides corrupt.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class BlobCorruptRowIntegrationTests
{
    /// <summary>Ids seeded with a payload no read may accept, and the storage class each holds.</summary>
    public static TheoryData<string, string> CorruptIds => new()
    {
        { "c/null", "null" },
        { "c/text", "text" },
        { "c/int", "integer" },
        { "c/real", "real" },
    };

    /// <summary>Ids seeded with a payload every read must accept.</summary>
    public static TheoryData<string> ReadableIds => new() { "c/blob", "c/empty", "c/zero" };

    private static async Task<IDocumentStore> CreateStoreWithNullableBlobTableAsync(int maxPoolSize = 4)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = maxPoolSize;

        var store = await new DocumentStoreFactory().CreateAsync(options);

        // Deliberately not CreateBlobTableAsync: the store's own DDL forbids a NULL payload, and
        // a consumer-created table is the only way to reach the rows under test.
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE [__store_blobs] (
                    id TEXT PRIMARY KEY,
                    content_type TEXT NULL,
                    created_at INTEGER NULL,
                    updated_at INTEGER NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    data BLOB);
                INSERT INTO [__store_blobs] (id, version, data) VALUES
                    ('c/blob', 1, x'0102030405'),
                    ('c/empty', 1, x''),
                    ('c/zero', 1, zeroblob(0)),
                    ('c/null', 1, NULL),
                    ('c/text', 1, 'hello'),
                    ('c/int', 1, 42),
                    ('c/real', 1, 1.5);";
            return await command.ExecuteNonQueryAsync(ct);
        });

        return store;
    }

    private static void AssertNamesTheRow(CorruptDataException exception, string id, string storedTypeName)
    {
        Assert.Equal(id, exception.Id);
        Assert.Equal("__store_blobs", exception.TableName);
        Assert.Equal(storedTypeName, exception.StoredTypeName);

        // A blob is raw bytes; there is no type it was being read as.
        Assert.Null(exception.TargetType);

        Assert.Contains(id, exception.Message, StringComparison.Ordinal);
        Assert.Contains(storedTypeName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task GetBlobAsync_WithAnUnreadablePayload_ThrowsInsteadOfLookingMissing(
        string id,
        string storedTypeName)
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => store.GetBlobAsync(id));

        AssertNamesTheRow(exception, id, storedTypeName);
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task BlobLengthAsync_WithAnUnreadablePayload_ThrowsInsteadOfReportingALength(
        string id,
        string storedTypeName)
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // length() answers for every one of these — 5 characters for 'hello', 2 digits for 42,
        // 3 for 1.5 — so the old contract returned a plausible byte count that was not one.
        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => store.BlobLengthAsync(id));

        AssertNamesTheRow(exception, id, storedTypeName);
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task GetBlobInfoAsync_WithAnUnreadablePayload_ThrowsAStoreException(
        string id,
        string storedTypeName)
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // A SQL NULL used to leak InvalidOperationException from the reader, and the other three
        // silently reported a character or digit count as a byte length.
        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => store.GetBlobInfoAsync(id));

        AssertNamesTheRow(exception, id, storedTypeName);
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task OpenBlobReadAsync_WithAnUnreadablePayload_ThrowsAStoreException(
        string id,
        string storedTypeName)
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // A SQL NULL and a number used to leak SqliteException; TEXT was worse — the handle
        // opened and read the value's UTF-8 bytes as though they were the payload.
        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => store.OpenBlobReadAsync(id));

        AssertNamesTheRow(exception, id, storedTypeName);
    }

    [Fact]
    public async Task ListBlobsAsync_WithOneUnreadableRow_ThrowsNamingThatRow()
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // 'c/blob' sorts before 'c/int', so the listing has already produced readable rows when
        // it reaches the first corrupt one — the id has to travel with each row to name it.
        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => store.ListBlobsAsync());

        Assert.Equal("c/int", exception.Id);
        Assert.Equal("integer", exception.StoredTypeName);
    }

    [Fact]
    public async Task ListBlobsAsync_RestrictedToReadableRows_StillLists()
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // The listing fails on a corrupt row rather than skipping it, matching GetAllAsync:
        // returning fewer rows than the table holds is data loss the caller cannot detect. A
        // prefix that excludes the corrupt rows is how a caller works around one.
        var listed = await store.ListBlobsAsync("c/b");

        Assert.Equal(["c/blob"], listed.Select(blob => blob.Id));
    }

    [Theory]
    [MemberData(nameof(ReadableIds))]
    public async Task EveryReadPath_WithABlobPayload_Succeeds(string id)
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        var expectedLength = id == "c/blob" ? 5 : 0;

        // An empty blob is a blob, written either way: the guard must not sweep it up.
        Assert.Equal(expectedLength, (await store.GetBlobAsync(id))!.Length);
        Assert.Equal(expectedLength, await store.BlobLengthAsync(id));
        Assert.Equal(expectedLength, (await store.GetBlobInfoAsync(id))!.Length);

        await using var stream = await store.OpenBlobReadAsync(id);
        Assert.Equal(expectedLength, stream!.Length);
    }

    [Fact]
    public async Task EveryReadPath_WithAnAbsentId_StillReportsItAbsent()
    {
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // Row presence decides not-found, so the corrupt rows in the table must not turn a
        // genuinely missing id into the new throw.
        Assert.Null(await store.GetBlobAsync("c/no-such-id"));
        Assert.Null(await store.BlobLengthAsync("c/no-such-id"));
        Assert.Null(await store.GetBlobInfoAsync("c/no-such-id"));
        Assert.Null(await store.OpenBlobReadAsync("c/no-such-id"));
        Assert.False(await store.BlobExistsAsync("c/no-such-id"));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task BlobExistsAsync_WithAnUnreadablePayload_StillReportsThePresentRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // Unchanged, and the whole point of the contract: one answer for the whole store. The
        // row is there, which is exactly why the reads above throw instead of returning null.
        Assert.True(await store.BlobExistsAsync(id));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task DeleteBlobAsync_WithAnUnreadablePayload_RemovesTheRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // The guard is on reads only: a caller has to be able to get rid of a damaged row.
        Assert.True(await store.DeleteBlobAsync(id));
        Assert.False(await store.BlobExistsAsync(id));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task DeleteBlobWithVersionAsync_WithAnUnreadablePayload_RemovesTheRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        await store.DeleteBlobWithVersionAsync(id, 1);

        Assert.False(await store.BlobExistsAsync(id));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task PutBlobAsync_WithAnUnreadablePayload_RepairsTheRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        // Overwriting is the other recovery path, and no writer reads the payload, so the guard
        // cannot reach it.
        await store.PutBlobAsync(id, new byte[] { 1, 2, 3 });

        Assert.Equal(3, (await store.GetBlobAsync(id))!.Length);
        Assert.Equal(3, await store.BlobLengthAsync(id));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task PutBlobWithVersionAsync_WithAnUnreadablePayload_RepairsTheRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        var version = await store.PutBlobWithVersionAsync(id, new byte[] { 9 }, 1);

        Assert.Equal(2, version);
        Assert.Equal([9], await store.GetBlobAsync(id));
    }

    [Theory]
    [MemberData(nameof(CorruptIds))]
    public async Task StreamedPutBlobAsync_WithAnUnreadablePayload_RepairsTheRow(
        string id,
        string storedTypeName)
    {
        _ = storedTypeName;
        using var store = await CreateStoreWithNullableBlobTableAsync();

        using var source = new MemoryStream([7, 7, 7, 7]);
        await store.PutBlobAsync(id, source, 4);

        Assert.Equal(4, (await store.GetBlobAsync(id))!.Length);
    }

    [Fact]
    public async Task OpenBlobReadAsync_WhenItRejectsAPayload_ReleasesTheBlobStreamSlot()
    {
        // One slot, so a leaked one is a timeout on the very next open rather than a soft
        // degradation. This is what pins the guard throwing through the release path.
        using var store = await CreateStoreWithNullableBlobTableAsync(maxPoolSize: 1);

        await Assert.ThrowsAsync<CorruptDataException>(() => store.OpenBlobReadAsync("c/text"));

        await using var stream = await store.OpenBlobReadAsync("c/blob");
        Assert.Equal(5, stream!.Length);
    }

    [Fact]
    public async Task OpenBlobReadAsync_WhenItRejectsAPayloadRepeatedly_NeverExhaustsTheSlots()
    {
        using var store = await CreateStoreWithNullableBlobTableAsync(maxPoolSize: 1);

        // The release has to be idempotent and unconditional: one un-released slot out of many
        // attempts still ends in a TimeoutException naming the cap.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<CorruptDataException>(() => store.OpenBlobReadAsync("c/null"));
        }

        await using var stream = await store.OpenBlobReadAsync("c/blob");
        Assert.NotNull(stream);
    }
}
