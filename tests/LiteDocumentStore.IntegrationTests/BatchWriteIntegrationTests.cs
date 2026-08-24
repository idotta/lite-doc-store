using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for chunked batch writes: sizes around the chunk boundary, sizes past
/// what a single statement can bind, duplicate-id rejection, and atomicity.
/// </summary>
[Collection(nameof(LiteDocumentStoreCollection))]
public class BatchWriteIntegrationTests
{
    private const int ChunkSize = SqlGenerator.MaxBatchItemsPerStatement;

    private readonly LiteDocumentStoreTestFixture _fixture;

    public BatchWriteIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<BatchDoc>();
        return store;
    }

    private static IEnumerable<(string, BatchDoc)> Docs(int count, string prefix = "doc") =>
        Enumerable.Range(0, count).Select(i => ($"{prefix}-{i:D6}", new BatchDoc($"Name {i}", i)));

    [Theory]
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]
    [InlineData(ChunkSize + 1)]
    [InlineData(1000)]
    public async Task UpsertMany_AtAndAroundTheChunkBoundary_WritesEveryDocument(int count)
    {
        var store = await CreateStoreWithTableAsync();

        var affected = await store.UpsertManyAsync(Docs(count));

        Assert.Equal(count, affected);
        Assert.Equal(count, await store.CountAsync<BatchDoc>());
        var last = await store.GetAsync<BatchDoc>($"doc-{count - 1:D6}");
        Assert.Equal(count - 1, last?.Index);
    }

    [Fact]
    public async Task UpsertMany_PastTheSingleStatementParameterLimit_WritesEveryDocument()
    {
        // 17_000 items is 34_000 bound parameters in one statement: past
        // SQLITE_MAX_VARIABLE_NUMBER (32_766), which is what chunking exists to avoid.
        const int count = 17_000;
        var store = await CreateStoreWithTableAsync();

        var affected = await store.UpsertManyAsync(Docs(count));

        Assert.Equal(count, affected);
        Assert.Equal(count, await store.CountAsync<BatchDoc>());
    }

    [Theory]
    [InlineData(ChunkSize)]
    [InlineData(ChunkSize + 1)]
    [InlineData(17_000)]
    public async Task DeleteMany_PastTheChunkBoundary_DeletesEveryDocument(int count)
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(count));

        var deleted = await store.DeleteManyAsync<BatchDoc>(
            Enumerable.Range(0, count).Select(i => $"doc-{i:D6}"));

        Assert.Equal(count, deleted);
        Assert.Equal(0, await store.CountAsync<BatchDoc>());
    }

    [Fact]
    public async Task UpsertMany_RepeatedBatch_UpdatesInPlaceAndBumpsVersion()
    {
        const int count = ChunkSize + 1;
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(count));

        var affected = await store.UpsertManyAsync(Docs(count, "doc"));

        Assert.Equal(count, affected);
        Assert.Equal(count, await store.CountAsync<BatchDoc>());
        var versioned = await store.GetWithVersionAsync<BatchDoc>("doc-000000");
        Assert.Equal(2L, versioned!.Version);
    }

    [Fact]
    public async Task UpsertMany_WithADuplicateId_ThrowsAndWritesNothing()
    {
        var store = await CreateStoreWithTableAsync();
        var items = new[]
        {
            ("a", new BatchDoc("first", 0)),
            ("b", new BatchDoc("second", 1)),
            ("a", new BatchDoc("third", 2))
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertManyAsync(items));

        Assert.Contains("Duplicate ID 'a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("indexes 0 and 2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await store.CountAsync<BatchDoc>());
    }

    [Fact]
    public async Task UpsertMany_WithADuplicateIdAcrossChunks_ThrowsBeforeTheFirstChunkIsWritten()
    {
        var store = await CreateStoreWithTableAsync();
        var items = Docs(ChunkSize + 1).ToList();
        items.Add(("doc-000000", new BatchDoc("clash", -1)));

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertManyAsync(items));

        Assert.Equal(0, await store.CountAsync<BatchDoc>());
    }

    [Fact]
    public async Task DeleteMany_WithRepeatedIds_DeletesOnceAndCountsOnce()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(3));

        var deleted = await store.DeleteManyAsync<BatchDoc>(
            ["doc-000000", "doc-000000", "doc-000001"]);

        Assert.Equal(2, deleted);
        Assert.Equal(1, await store.CountAsync<BatchDoc>());
    }

    [Fact]
    public async Task UpsertMany_WhenAChunkFails_RollsBackEveryChunk()
    {
        var store = await CreateStoreWithTableAsync();

        // Seeded with the Index the batch's *last* item carries, so a unique index on Index
        // fails the final chunk only after the earlier ones have already been executed.
        await store.UpsertAsync("seed", new BatchDoc("seed", ChunkSize));
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE UNIQUE INDEX ux_batchdoc_index ON [BatchDoc] (json_extract(data, '$.Index'))";
            return await command.ExecuteNonQueryAsync(ct);
        });

        var items = Docs(ChunkSize + 1).ToList();

        await Assert.ThrowsAsync<SqliteException>(() => store.UpsertManyAsync(items));

        // Only the seeded row survives: every chunk of the failed batch was rolled back.
        Assert.Equal(1, await store.CountAsync<BatchDoc>());
        Assert.NotNull(await store.GetAsync<BatchDoc>("seed"));
    }

    [Fact]
    public async Task UpsertMany_InsideATransaction_JoinsItAndRollsBackWithIt()
    {
        var store = await CreateStoreWithTableAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertManyAsync(Docs(ChunkSize + 1));
            Assert.Equal(ChunkSize + 1, await transaction.CountAsync<BatchDoc>());
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await store.CountAsync<BatchDoc>());
    }

    [Fact]
    public async Task DeleteMany_InsideATransaction_JoinsItAndCommitsWithIt()
    {
        const int count = ChunkSize + 1;
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(count));

        await store.ExecuteInTransactionAsync(async transaction =>
        {
            await transaction.DeleteManyAsync<BatchDoc>(
                Enumerable.Range(0, count).Select(i => $"doc-{i:D6}"));
        });

        Assert.Equal(0, await store.CountAsync<BatchDoc>());
    }

    private sealed record BatchDoc(string Name, int Index);
}
