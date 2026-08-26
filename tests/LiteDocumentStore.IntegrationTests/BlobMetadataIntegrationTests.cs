using System.Collections.Concurrent;
using LiteDocumentStore.Exceptions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for blob metadata against real SQLite: the table layout, the in-place
/// upgrade of a table created before the metadata columns existed, the rebuild that fixes its
/// column order, listing, and the compare-and-swap blob writes.
/// </summary>
[Trait("Category", "Integration")]
public class BlobMetadataIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _databasePaths = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
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
                        // A handle the provider has not finalized yet.
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed record Doc(string Name);

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-blobmeta-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private async Task<IDocumentStore> CreateFileStoreAsync(
        ILoggerFactory? loggerFactory = null,
        bool createBlobTable = true)
    {
        var store = await new DocumentStoreFactory(new DefaultConnectionFactory(), null, loggerFactory)
            .CreateAsync(DocumentStoreOptions.ForFile(NewDatabasePath()));

        if (createBlobTable)
        {
            await store.CreateBlobTableAsync();
        }

        await store.CreateTableAsync<Doc>();
        return store;
    }

    private static async Task<List<string>> ColumnNamesAsync(IDocumentStore store) =>
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('__store_blobs') ORDER BY cid";
            await using var reader = await command.ExecuteReaderAsync(ct);

            var names = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

    private static async Task ExecuteRawAsync(IDocumentStore store, string sql) =>
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync(ct);
        });

    private static void AssertRecent(DateTimeOffset? timestamp)
    {
        Assert.NotNull(timestamp);
        var age = DateTimeOffset.UtcNow - timestamp.Value;
        Assert.True(age > TimeSpan.FromMinutes(-5) && age < TimeSpan.FromMinutes(5),
            $"expected a timestamp near now, got {timestamp} ({age} ago)");
    }

    [Fact]
    public async Task CreateBlobTableAsync_CreatesTheTableWithThePayloadColumnLast()
    {
        await using var store = await CreateFileStoreAsync();

        Assert.Equal(
            ["id", "content_type", "created_at", "updated_at", "version", "data"],
            await ColumnNamesAsync(store));
    }

    [Fact]
    public async Task PutBlobAsync_RecordsLengthContentTypeTimestampsAndVersion()
    {
        await using var store = await CreateFileStoreAsync();

        await store.PutBlobAsync("doc", new byte[] { 1, 2, 3 },
            new BlobWriteOptions { ContentType = "application/pdf" });

        var info = await store.GetBlobInfoAsync("doc");
        Assert.NotNull(info);
        Assert.Equal("doc", info.Id);
        Assert.Equal(3, info.Length);
        Assert.Equal("application/pdf", info.ContentType);
        Assert.Equal(1, info.Version);
        AssertRecent(info.CreatedAt);
        AssertRecent(info.UpdatedAt);
    }

    [Fact]
    public async Task GetBlobInfoAsync_ReturnsNullForAnAbsentId()
    {
        await using var store = await CreateFileStoreAsync();

        Assert.Null(await store.GetBlobInfoAsync("missing"));
    }

    [Fact]
    public async Task PutBlobAsync_OverwriteBumpsTheVersionAndKeepsTheCreationTime()
    {
        await using var store = await CreateFileStoreAsync();

        await store.PutBlobAsync("doc", new byte[] { 1 },
            new BlobWriteOptions { ContentType = "text/plain" });
        var first = await store.GetBlobInfoAsync("doc");

        await Task.Delay(20);
        await store.PutBlobAsync("doc", new byte[] { 1, 2, 3, 4 },
            new BlobWriteOptions { ContentType = "application/json" });

        var second = await store.GetBlobInfoAsync("doc");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, second.Version);
        Assert.Equal(4, second.Length);
        Assert.Equal("application/json", second.ContentType);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.True(second.UpdatedAt > first.UpdatedAt);
    }

    [Fact]
    public async Task PutBlobAsync_WithoutOptionsClearsTheContentTypeItReplaced()
    {
        await using var store = await CreateFileStoreAsync();

        await store.PutBlobAsync("doc", new byte[] { 1 },
            new BlobWriteOptions { ContentType = "text/plain" });

        // The stored type described the payload being replaced, so an overwrite that states
        // nothing must not leave it claiming to describe the new bytes.
        await store.PutBlobAsync("doc", new byte[] { 2 });

        var info = await store.GetBlobInfoAsync("doc");
        Assert.NotNull(info);
        Assert.Null(info.ContentType);
        Assert.Equal(2, info.Version);
    }

    [Fact]
    public async Task PutBlobAsync_FromAStreamRecordsTheSameMetadata()
    {
        await using var store = await CreateFileStoreAsync();
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);

        await store.PutBlobAsync("streamed", new MemoryStream(payload), payload.Length,
            new BlobWriteOptions { ContentType = "application/octet-stream" });

        var info = await store.GetBlobInfoAsync("streamed");
        Assert.NotNull(info);
        Assert.Equal(payload.Length, info.Length);
        Assert.Equal("application/octet-stream", info.ContentType);
        Assert.Equal(1, info.Version);
        AssertRecent(info.UpdatedAt);
        Assert.Equal(payload, await store.GetBlobAsync("streamed"));
    }

    [Fact]
    public async Task PutBlobAsync_FromAStreamBumpsTheVersionOnOverwrite()
    {
        await using var store = await CreateFileStoreAsync();

        await store.PutBlobAsync("streamed", new MemoryStream(new byte[10]), 10);
        await store.PutBlobAsync("streamed", new MemoryStream(new byte[4]), 4);

        var info = await store.GetBlobInfoAsync("streamed");
        Assert.NotNull(info);
        Assert.Equal(2, info.Version);
        Assert.Equal(4, info.Length);
    }

    // --- the in-place upgrade of a table created before the metadata columns existed ---

    private async Task<IDocumentStore> CreateStoreOverLegacyBlobTableAsync(
        ILoggerFactory? loggerFactory = null)
    {
        var store = await CreateFileStoreAsync(loggerFactory, createBlobTable: false);

        await ExecuteRawAsync(store,
            "CREATE TABLE __store_blobs (id TEXT PRIMARY KEY, data BLOB NOT NULL)");
        await ExecuteRawAsync(store,
            "INSERT INTO __store_blobs (id, data) VALUES ('legacy', x'01020304')");

        return store;
    }

    [Fact]
    public async Task CreateBlobTableAsync_AddsTheMetadataColumnsToALegacyTableWithoutLosingRows()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();

        await store.CreateBlobTableAsync();

        Assert.Equal(
            ["id", "data", "content_type", "created_at", "updated_at", "version"],
            await ColumnNamesAsync(store));

        var info = await store.GetBlobInfoAsync("legacy");
        Assert.NotNull(info);
        Assert.Equal(4, info.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.GetBlobAsync("legacy"));

        // A row that already existed has no true creation time, so the store does not invent one.
        Assert.Null(info.CreatedAt);
        Assert.Null(info.UpdatedAt);
        Assert.Null(info.ContentType);

        // But it is compare-and-swappable immediately, off the column default.
        Assert.Equal(1, info.Version);
    }

    [Fact]
    public async Task CreateBlobTableAsync_IsIdempotentOverALegacyTable()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();

        await store.CreateBlobTableAsync();
        await store.CreateBlobTableAsync();
        await store.CreateBlobTableAsync();

        Assert.Equal(6, (await ColumnNamesAsync(store)).Count);
        Assert.NotNull(await store.GetBlobInfoAsync("legacy"));
    }

    [Fact]
    public async Task CreateBlobTableAsync_UpgradingALegacyTableWarnsAboutTheColumnOrder()
    {
        var loggerFactory = new CapturingLoggerFactory();
        await using var store = await CreateStoreOverLegacyBlobTableAsync(loggerFactory);

        await store.CreateBlobTableAsync();

        // The ALTER leaves the payload ahead of the metadata, which is the slow layout; a caller
        // who never hears about it has no reason to schedule the rebuild.
        Assert.Contains(loggerFactory.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("RebuildBlobTableAsync"));
    }

    [Fact]
    public async Task CreateBlobTableAsync_OnACurrentTableDoesNotWarn()
    {
        var loggerFactory = new CapturingLoggerFactory();
        await using var store = await CreateFileStoreAsync(loggerFactory);

        await store.CreateBlobTableAsync();

        Assert.DoesNotContain(loggerFactory.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <remarks>
    /// This pins that concurrent upgrades converge on one correct table, not the re-check under
    /// the write lock that makes them safe: the interleaving that exercises it — both stores
    /// reading the column list before either takes the lock — cannot be scheduled through the
    /// public API, and removing the re-check leaves this test passing. The re-check is kept for
    /// the cross-process case, where the loser blocks at <c>BEGIN IMMEDIATE</c> and would
    /// otherwise issue a second ALTER and fail with "duplicate column name".
    /// </remarks>
    [Fact]
    public async Task CreateBlobTableAsync_UpgradesConcurrentlyWithoutCorruptingTheTable()
    {
        var path = NewDatabasePath();
        var options = DocumentStoreOptions.ForFile(path);

        await using (var seed = await new DocumentStoreFactory().CreateAsync(options))
        {
            await ExecuteRawAsync(seed,
                "CREATE TABLE __store_blobs (id TEXT PRIMARY KEY, data BLOB NOT NULL)");
        }

        await using var a = await new DocumentStoreFactory().CreateAsync(options);
        await using var b = await new DocumentStoreFactory().CreateAsync(options);

        // The loser of the race must find the columns already there under the write lock rather
        // than failing with "duplicate column name".
        await Task.WhenAll(a.CreateBlobTableAsync(), b.CreateBlobTableAsync());

        Assert.Equal(6, (await ColumnNamesAsync(a)).Count);
    }

    [Fact]
    public async Task RebuildBlobTableAsync_MovesThePayloadColumnLastAndKeepsEveryRow()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();
        await store.CreateBlobTableAsync();

        await store.PutBlobAsync("fresh", new byte[] { 9, 9 },
            new BlobWriteOptions { ContentType = "text/csv" });
        var before = await store.GetBlobInfoAsync("fresh");

        Assert.True(await store.RebuildBlobTableAsync());

        Assert.Equal(
            ["id", "content_type", "created_at", "updated_at", "version", "data"],
            await ColumnNamesAsync(store));

        var after = await store.GetBlobInfoAsync("fresh");
        Assert.Equal(before, after);
        Assert.Equal(new byte[] { 9, 9 }, await store.GetBlobAsync("fresh"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.GetBlobAsync("legacy"));
        Assert.Equal(2, (await store.ListBlobsAsync()).Count);
    }

    [Fact]
    public async Task RebuildBlobTableAsync_UpgradesATableThatNeverHadTheMetadataColumns()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();

        // A table that predates the metadata columns ends in 'data' as well, so a layout check
        // alone reports it current — and then every metadata read fails with "no such column".
        Assert.True(await store.RebuildBlobTableAsync());

        Assert.Equal(
            ["id", "content_type", "created_at", "updated_at", "version", "data"],
            await ColumnNamesAsync(store));

        var info = await store.GetBlobInfoAsync("legacy");
        Assert.NotNull(info);
        Assert.Equal(4, info.Length);
        Assert.Equal(1, info.Version);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.GetBlobAsync("legacy"));
    }

    [Fact]
    public async Task RebuildBlobTableAsync_ReportsNothingToDoWhenThereIsNoBlobTable()
    {
        await using var store = await CreateFileStoreAsync(createBlobTable: false);

        Assert.False(await store.RebuildBlobTableAsync());
    }

    [Fact]
    public async Task RebuildBlobTableAsync_IsANoOpOnATableThatAlreadyHasTheCurrentLayout()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("doc", new byte[] { 1 });

        Assert.False(await store.RebuildBlobTableAsync());
        Assert.False(await store.RebuildBlobTableAsync());
        Assert.Equal(new byte[] { 1 }, await store.GetBlobAsync("doc"));
    }

    [Fact]
    public async Task RebuildBlobTableAsync_LeavesTheBlobUsableForStreamingAfterwards()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();
        await store.CreateBlobTableAsync();
        await store.RebuildBlobTableAsync();

        // Incremental blob I/O addresses rows by rowid, and the rebuild assigns new ones.
        await using var stream = await store.OpenBlobReadAsync("legacy");
        Assert.NotNull(stream);
        using var read = new MemoryStream();
        await stream.CopyToAsync(read);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, read.ToArray());
    }

    // --- listing ---

    private static async Task SeedListingAsync(IDocumentStore store)
    {
        foreach (var id in new[] { "u/1", "u/2", "u/10", "u/1%x", "U/UPPER", "v/1" })
        {
            await store.PutBlobAsync(id, new byte[] { 7 });
        }
    }

    [Fact]
    public async Task ListBlobsAsync_ReturnsEveryBlobInIdOrder()
    {
        await using var store = await CreateFileStoreAsync();
        await SeedListingAsync(store);

        var listed = await store.ListBlobsAsync();

        Assert.Equal(["U/UPPER", "u/1", "u/1%x", "u/10", "u/2", "v/1"], listed.Select(b => b.Id));
        Assert.All(listed, b => Assert.Equal(1, b.Length));
    }

    [Fact]
    public async Task ListBlobsAsync_MatchesThePrefixLiterallyAndCaseSensitively()
    {
        await using var store = await CreateFileStoreAsync();
        await SeedListingAsync(store);

        // "U/UPPER" is excluded: a prefix is a key range, not a LIKE pattern, which would have
        // matched it case-insensitively.
        Assert.Equal(["u/1", "u/1%x", "u/10", "u/2"], (await store.ListBlobsAsync("u/")).Select(b => b.Id));
        Assert.Equal(["u/1", "u/1%x", "u/10"], (await store.ListBlobsAsync("u/1")).Select(b => b.Id));

        // '%' is a literal character here, not a wildcard.
        Assert.Equal(["u/1%x"], (await store.ListBlobsAsync("u/1%")).Select(b => b.Id));
        Assert.Empty(await store.ListBlobsAsync("u/1%y"));
    }

    [Fact]
    public async Task ListBlobsAsync_PagesWithSkipAndTake()
    {
        await using var store = await CreateFileStoreAsync();
        await SeedListingAsync(store);

        Assert.Equal(["U/UPPER", "u/1"], (await store.ListBlobsAsync(take: 2)).Select(b => b.Id));
        Assert.Equal(["u/1%x", "u/10"], (await store.ListBlobsAsync(skip: 2, take: 2)).Select(b => b.Id));

        // Skip without take needs SQLite's LIMIT -1.
        Assert.Equal(["u/2", "v/1"], (await store.ListBlobsAsync(skip: 4)).Select(b => b.Id));
        Assert.Empty(await store.ListBlobsAsync(skip: 99));
        Assert.Empty(await store.ListBlobsAsync(take: 0));
        Assert.Equal(["u/10"], (await store.ListBlobsAsync("u/1", skip: 2, take: 1)).Select(b => b.Id));
    }

    [Fact]
    public async Task ListBlobsAsync_RejectsANegativeSkipOrTake()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListBlobsAsync(skip: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListBlobsAsync(take: -1));
    }

    [Fact]
    public async Task ListBlobsAsync_UsesTheIndexForAPrefixRange()
    {
        await using var store = await CreateFileStoreAsync();
        await SeedListingAsync(store);

        var plan = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                EXPLAIN QUERY PLAN
                SELECT id, length(data), content_type, created_at, updated_at, version
                FROM __store_blobs WHERE id >= 'u/' AND id < 'u0' ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(3));
            }

            return string.Join(" | ", rows);
        });

        // The reason the prefix is a range and not LIKE/GLOB/substr, all of which scan.
        Assert.Contains("SEARCH", plan);
        Assert.DoesNotContain("SCAN", plan);
    }

    // --- compare-and-swap ---

    [Fact]
    public async Task PutBlobWithVersionAsync_InsertsAtVersionOneWhenTheIdIsFree()
    {
        await using var store = await CreateFileStoreAsync();

        var version = await store.PutBlobWithVersionAsync("doc", new byte[] { 1 }, 0);

        Assert.Equal(1, version);
        Assert.Equal(new byte[] { 1 }, await store.GetBlobAsync("doc"));
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_ReportsATakenIdAsAlreadyExists()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("doc", new byte[] { 1 });

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.PutBlobWithVersionAsync("doc", new byte[] { 2 }, 0));

        Assert.Equal(ConcurrencyConflictKind.AlreadyExists, ex.Kind);
        Assert.Equal("doc", ex.DocumentId);
        Assert.Equal("__store_blobs", ex.TableName);
        Assert.Equal(1, ex.ActualVersion);
        Assert.Contains("blob", ex.Message);
        Assert.Equal(new byte[] { 1 }, await store.GetBlobAsync("doc"));
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_OverwritesOnAMatchAndRefusesAStaleVersion()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("doc", new byte[] { 1 });

        var version = await store.PutBlobWithVersionAsync("doc", new byte[] { 2 }, 1,
            new BlobWriteOptions { ContentType = "text/plain" });
        Assert.Equal(2, version);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.PutBlobWithVersionAsync("doc", new byte[] { 3 }, 1));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, ex.Kind);
        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal(new byte[] { 2 }, await store.GetBlobAsync("doc"));

        var info = await store.GetBlobInfoAsync("doc");
        Assert.Equal("text/plain", info!.ContentType);
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_ReportsAMissingBlobAsNotFound()
    {
        await using var store = await CreateFileStoreAsync();

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.PutBlobWithVersionAsync("missing", new byte[] { 1 }, 3));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, ex.Kind);
        Assert.Null(ex.ActualVersion);
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_LiftsARowLeftAtVersionZeroByRawSql()
    {
        await using var store = await CreateFileStoreAsync();
        await ExecuteRawAsync(store,
            "INSERT INTO __store_blobs (id, version, data) VALUES ('raw', 0, x'ff')");

        // Expected version 0 means "insert"; the id is taken, so it retries as a 0-guarded update
        // rather than leaving the row outside the concurrency model forever.
        var version = await store.PutBlobWithVersionAsync("raw", new byte[] { 1 }, 0);

        Assert.Equal(1, version);
        Assert.Equal(new byte[] { 1 }, await store.GetBlobAsync("raw"));
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_FromAStreamGuardsBeforeWritingAnyBytes()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("doc", new byte[] { 1, 2, 3, 4 });

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.PutBlobWithVersionAsync("doc", new MemoryStream(new byte[9]), 9, 7));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, ex.Kind);

        // The reserve statement is the guard, so a rejected write never replaced the payload with
        // a zeroblob.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.GetBlobAsync("doc"));
    }

    [Fact]
    public async Task PutBlobWithVersionAsync_FromAStreamInsertsAndOverwritesOnAMatch()
    {
        await using var store = await CreateFileStoreAsync();

        Assert.Equal(1, await store.PutBlobWithVersionAsync("doc", new MemoryStream([1, 2]), 2, 0));
        Assert.Equal(2, await store.PutBlobWithVersionAsync("doc", new MemoryStream([3]), 1, 1,
            new BlobWriteOptions { ContentType = "text/plain" }));

        Assert.Equal(new byte[] { 3 }, await store.GetBlobAsync("doc"));
        var info = await store.GetBlobInfoAsync("doc");
        Assert.Equal("text/plain", info!.ContentType);
        Assert.Equal(2, info.Version);
    }

    [Fact]
    public async Task DeleteBlobWithVersionAsync_DeletesOnAMatchAndRefusesOtherwise()
    {
        await using var store = await CreateFileStoreAsync();
        await store.PutBlobAsync("doc", new byte[] { 1 });

        var stale = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteBlobWithVersionAsync("doc", 5));
        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, stale.Kind);
        Assert.True(await store.BlobExistsAsync("doc"));

        await store.DeleteBlobWithVersionAsync("doc", 1);
        Assert.False(await store.BlobExistsAsync("doc"));

        var missing = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteBlobWithVersionAsync("doc", 1));
        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, missing.Kind);
    }

    [Fact]
    public async Task DeleteBlobWithVersionAsync_AtVersionZeroMeansTheRowStillSittingAtZero()
    {
        await using var store = await CreateFileStoreAsync();
        await ExecuteRawAsync(store,
            "INSERT INTO __store_blobs (id, version, data) VALUES ('raw', 0, x'ff')");
        await store.PutBlobAsync("normal", new byte[] { 1 });

        await store.DeleteBlobWithVersionAsync("raw", 0);
        Assert.False(await store.BlobExistsAsync("raw"));

        // On a delete, 0 carries no insert sense — a row at version 1 is a plain mismatch.
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteBlobWithVersionAsync("normal", 0));
        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, ex.Kind);
    }

    [Fact]
    public async Task BlobCompareAndSwap_RejectsANegativeExpectedVersion()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.PutBlobWithVersionAsync("doc", new byte[] { 1 }, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.PutBlobWithVersionAsync("doc", new MemoryStream(new byte[1]), 1, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.DeleteBlobWithVersionAsync("doc", -1));
    }

    [Fact]
    public async Task PutBlobAsync_RejectsABlankContentType()
    {
        await using var store = await CreateFileStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutBlobAsync("doc", new byte[] { 1 }, new BlobWriteOptions { ContentType = " " }));
        Assert.False(await store.BlobExistsAsync("doc"));
    }

    // --- inside a transaction ---

    [Fact]
    public async Task BlobMetadata_InsideATransactionCommitsAndRollsBackWithTheDocuments()
    {
        await using var store = await CreateFileStoreAsync();

        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("d1", new Doc("kept"));
            await tx.PutBlobAsync("b1", new byte[] { 1 },
                new BlobWriteOptions { ContentType = "text/plain" });

            // The transaction's own view, before anything is committed.
            var listed = await tx.ListBlobsAsync();
            Assert.Equal(["b1"], listed.Select(b => b.Id));
            Assert.Equal("text/plain", listed[0].ContentType);
        });

        Assert.Equal("text/plain", (await store.GetBlobInfoAsync("b1"))!.ContentType);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ExecuteInTransactionAsync(async tx =>
            {
                await tx.PutBlobWithVersionAsync("b2", new byte[] { 2 }, 0);
                throw new InvalidOperationException("rolled back");
            }));

        Assert.Null(await store.GetBlobInfoAsync("b2"));
    }

    [Fact]
    public async Task CreateBlobTableAsync_UpgradesALegacyTableFromInsideATransaction()
    {
        await using var store = await CreateStoreOverLegacyBlobTableAsync();

        await store.ExecuteInTransactionAsync(async tx =>
        {
            // No nested BEGIN IMMEDIATE here: the caller's transaction already serializes it.
            await tx.CreateBlobTableAsync();
            await tx.PutBlobAsync("added", new byte[] { 5 });
        });

        Assert.Equal(1, (await store.GetBlobInfoAsync("added"))!.Version);
        Assert.Equal(1, (await store.GetBlobInfoAsync("legacy"))!.Version);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
