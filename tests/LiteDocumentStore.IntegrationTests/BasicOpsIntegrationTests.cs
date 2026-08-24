using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the basic bulk/schema operations: <c>GetManyAsync</c>,
/// <c>DeleteAllAsync</c>, <c>DropTableAsync</c> and both <c>DropIndexAsync</c> overloads —
/// on the store, inside a transaction, and around the batch chunk boundary.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public class BasicOpsIntegrationTests
{
    private const int ChunkSize = SqlGenerator.MaxBatchItemsPerStatement;

    // CreateIndexAsync auto-names an index idx_{table}_{json path with the leading "$." stripped}.
    private const string AutoIndexName = "idx_BasicDoc_Email";

    private readonly LiteDocumentStoreTestFixture _fixture;

    public BasicOpsIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<BasicDoc>();
        return store;
    }

    private async Task<IDocumentStore> CreateFileStoreWithTableAsync()
    {
        var store = await _fixture.CreateFileStoreAsync();
        await store.CreateTableAsync<BasicDoc>();
        return store;
    }

    private static IEnumerable<(string, BasicDoc)> Docs(int count) =>
        Enumerable.Range(0, count)
            .Select(i => ($"doc-{i:D6}", new BasicDoc($"Name {i}", i, $"user{i}@example.com")));

    private static IEnumerable<string> Ids(int count) =>
        Enumerable.Range(0, count).Select(i => $"doc-{i:D6}");

    // SchemaIntrospector works on a SqliteConnection, so it is built on a rented connection
    // for the duration of the callback.
    private static Task<TResult> IntrospectAsync<TResult>(
        IDocumentStore store,
        Func<SchemaIntrospector, Task<TResult>> operation) =>
        store.ExecuteRawAsync((connection, _) => operation(new SchemaIntrospector(connection)));

    // ---------------------------------------------------------------- GetManyAsync

    [Fact]
    public async Task GetManyAsync_WithExistingIds_ReturnsEveryDocumentWithItsContent()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(3));

        var found = await store.GetManyAsync<BasicDoc>(["doc-000000", "doc-000002"]);

        Assert.Equal(2, found.Count);
        Assert.Equal(new BasicDoc("Name 0", 0, "user0@example.com"), found["doc-000000"]);
        Assert.Equal(new BasicDoc("Name 2", 2, "user2@example.com"), found["doc-000002"]);
        Assert.False(found.ContainsKey("doc-000001"));
    }

    [Fact]
    public async Task GetManyAsync_WithMissingIds_OmitsThemInsteadOfReturningNulls()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));

        var found = await store.GetManyAsync<BasicDoc>(["doc-000000", "nope", "doc-000001", "also-nope"]);

        Assert.Equal(2, found.Count);
        Assert.False(found.ContainsKey("nope"));
        Assert.False(found.ContainsKey("also-nope"));
        Assert.All(found.Values, document => Assert.NotNull(document));
    }

    [Fact]
    public async Task GetManyAsync_WithNoMatchingId_ReturnsEmpty()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));

        var found = await store.GetManyAsync<BasicDoc>(["nope-1", "nope-2"]);

        Assert.Empty(found);
    }

    [Fact]
    public async Task GetManyAsync_WithDuplicateIds_CollapsesThemToOneEntry()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));

        var found = await store.GetManyAsync<BasicDoc>(
            ["doc-000000", "doc-000000", "doc-000001", "doc-000000"]);

        Assert.Equal(2, found.Count);
        Assert.Equal("Name 0", found["doc-000000"].Name);
    }

    [Fact]
    public async Task GetManyAsync_WithAnEmptyCollection_ReturnsEmptyWithoutTouchingTheDatabase()
    {
        // The table is deliberately never created: a short-circuit on the empty input is the only
        // way this can return instead of failing with "no such table".
        var store = await _fixture.CreateInMemoryStoreAsync();

        var found = await store.GetManyAsync<BasicDoc>([]);

        Assert.Empty(found);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]
    [InlineData(ChunkSize + 1)]
    [InlineData(1000)]
    public async Task GetManyAsync_AtAndAroundTheChunkBoundary_ReturnsEveryDocument(int count)
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(count));

        var found = await store.GetManyAsync<BasicDoc>(Ids(count));

        Assert.Equal(count, found.Count);
        Assert.Equal("Name 0", found["doc-000000"].Name);
        Assert.Equal($"Name {count - 1}", found[$"doc-{count - 1:D6}"].Name);
        Assert.Equal(count - 1, found[$"doc-{count - 1:D6}"].Index);
    }

    [Fact]
    public async Task GetManyAsync_AcrossChunksWithSomeIdsMissing_ReturnsOnlyTheFoundOnes()
    {
        const int stored = ChunkSize + 1;
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(stored));

        // Ask for twice as many ids as exist, so the misses straddle the chunk boundary too.
        var found = await store.GetManyAsync<BasicDoc>(Ids(stored * 2));

        Assert.Equal(stored, found.Count);
        Assert.False(found.ContainsKey($"doc-{stored:D6}"));
    }

    [Fact]
    public async Task GetManyAsync_WithANullCollection_ThrowsArgumentNullException()
    {
        var store = await CreateStoreWithTableAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.GetManyAsync<BasicDoc>(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetManyAsync_WithANullOrWhitespaceIdInTheCollection_ThrowsArgumentException(string? badId)
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(1));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.GetManyAsync<BasicDoc>(["doc-000000", badId!]));
    }

    [Fact]
    public async Task GetManyAsync_WithAnAlreadyCancelledToken_Throws()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetManyAsync<BasicDoc>(["doc-000000", "doc-000001"], cts.Token));
    }

    // -------------------------------------------------------------- DeleteAllAsync

    [Fact]
    public async Task DeleteAllAsync_WithDocuments_ReturnsTheDeletedRowCountAndEmptiesTheTable()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(5));

        var deleted = await store.DeleteAllAsync<BasicDoc>();

        Assert.Equal(5, deleted);
        Assert.Equal(0, await store.CountAsync<BasicDoc>());
        Assert.Empty(await store.GetAllAsync<BasicDoc>());
    }

    [Fact]
    public async Task DeleteAllAsync_OnAnAlreadyEmptyTable_ReturnsZero()
    {
        var store = await CreateStoreWithTableAsync();

        var deleted = await store.DeleteAllAsync<BasicDoc>();

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DeleteAllAsync_LeavesTheTableItselfInPlace()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(3));

        await store.DeleteAllAsync<BasicDoc>();

        // The table still exists, so a plain write + read round-trips without recreating it.
        await store.UpsertAsync("after", new BasicDoc("After", 42, "after@example.com"));
        var reloaded = await store.GetAsync<BasicDoc>("after");
        Assert.Equal(new BasicDoc("After", 42, "after@example.com"), reloaded);
        Assert.Equal(1, await store.CountAsync<BasicDoc>());
        Assert.True(await IntrospectAsync(store, introspector => introspector.TableExistsAsync("BasicDoc")));
    }

    // --------------------------------------------------------------- DropTableAsync

    [Fact]
    public async Task DropTableAsync_RemovesTheTable()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));

        await store.DropTableAsync<BasicDoc>();

        Assert.False(await IntrospectAsync(store, introspector => introspector.TableExistsAsync("BasicDoc")));
    }

    [Fact]
    public async Task DropTableAsync_ThenAnOperationOnThatType_Fails()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("doc-000000", new BasicDoc("Name 0", 0, "user0@example.com"));

        await store.DropTableAsync<BasicDoc>();

        // Nothing in the library maps SQLite's "no such table" to TableNotFoundException today,
        // so the raw SqliteException is what surfaces.
        var exception = await Assert.ThrowsAsync<SqliteException>(() => store.CountAsync<BasicDoc>());
        Assert.Contains("no such table", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DropTableAsync_OnANonExistentTable_DoesNotThrow()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();

        // DROP TABLE IF EXISTS: never created, then dropped twice.
        await store.DropTableAsync<BasicDoc>();
        await store.DropTableAsync<BasicDoc>();

        Assert.False(await IntrospectAsync(store, introspector => introspector.TableExistsAsync("BasicDoc")));
    }

    [Fact]
    public async Task DropTableAsync_ThenCreateTableAsync_StartsFromAnEmptyTable()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(3));

        await store.DropTableAsync<BasicDoc>();
        await store.CreateTableAsync<BasicDoc>();

        Assert.Equal(0, await store.CountAsync<BasicDoc>());
    }

    // --------------------------------------------------------------- DropIndexAsync

    [Fact]
    public async Task DropIndexAsync_ByName_RemovesTheIndex()
    {
        var store = await CreateStoreWithTableAsync();
        await store.CreateIndexAsync<BasicDoc>(d => d.Email, "idx_basicdoc_email_explicit");
        Assert.True(await IntrospectAsync(store,
            introspector => introspector.IndexExistsAsync("idx_basicdoc_email_explicit")));

        await store.DropIndexAsync("idx_basicdoc_email_explicit");

        Assert.False(await IntrospectAsync(store,
            introspector => introspector.IndexExistsAsync("idx_basicdoc_email_explicit")));
        Assert.Empty(await IntrospectAsync(store, introspector => introspector.GetIndexesAsync("BasicDoc")));
    }

    [Fact]
    public async Task DropIndexAsync_ByExpression_RemovesTheAutoNamedIndex()
    {
        var store = await CreateStoreWithTableAsync();
        await store.CreateIndexAsync<BasicDoc>(d => d.Email);
        var created = (await IntrospectAsync(store, introspector => introspector.GetIndexesAsync("BasicDoc"))).ToList();
        Assert.Single(created);
        Assert.Equal(AutoIndexName, created[0].Name);

        await store.DropIndexAsync<BasicDoc>(d => d.Email);

        Assert.Empty(await IntrospectAsync(store, introspector => introspector.GetIndexesAsync("BasicDoc")));
        Assert.False(await IntrospectAsync(store, introspector => introspector.IndexExistsAsync(AutoIndexName)));
    }

    [Fact]
    public async Task DropIndexAsync_ByExpression_LeavesOtherIndexesAlone()
    {
        var store = await CreateStoreWithTableAsync();
        await store.CreateIndexAsync<BasicDoc>(d => d.Email);
        await store.CreateIndexAsync<BasicDoc>(d => d.Name, "idx_basicdoc_name_keep");

        await store.DropIndexAsync<BasicDoc>(d => d.Email);

        Assert.False(await IntrospectAsync(store, introspector => introspector.IndexExistsAsync(AutoIndexName)));
        Assert.True(await IntrospectAsync(store,
            introspector => introspector.IndexExistsAsync("idx_basicdoc_name_keep")));
    }

    [Fact]
    public async Task DropIndexAsync_OnANonExistentIndex_DoesNotThrow()
    {
        var store = await CreateStoreWithTableAsync();

        // DROP INDEX IF EXISTS, by name and by expression, on an index that was never created.
        await store.DropIndexAsync("idx_basicdoc_never_created");
        await store.DropIndexAsync<BasicDoc>(d => d.Email);
        await store.DropIndexAsync("idx_basicdoc_never_created");

        Assert.Empty(await IntrospectAsync(store, introspector => introspector.GetIndexesAsync("BasicDoc")));
    }

    [Fact]
    public async Task DropIndexAsync_ThenCreateIndexAsync_RecreatesIt()
    {
        var store = await CreateStoreWithTableAsync();
        await store.CreateIndexAsync<BasicDoc>(d => d.Email);
        await store.DropIndexAsync<BasicDoc>(d => d.Email);

        await store.CreateIndexAsync<BasicDoc>(d => d.Email);

        Assert.True(await IntrospectAsync(store, introspector => introspector.IndexExistsAsync(AutoIndexName)));
    }

    // ------------------------------------------------------------------ Transactions

    [Fact]
    public async Task Transaction_GetManyAsync_SeesItsOwnUncommittedWrites()
    {
        var store = await CreateStoreWithTableAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertManyAsync(Docs(3));

            var found = await transaction.GetManyAsync<BasicDoc>(["doc-000000", "doc-000002"]);

            Assert.Equal(2, found.Count);
            Assert.Equal("Name 2", found["doc-000002"].Name);
            await transaction.CommitAsync();
        }

        Assert.Equal(3, await store.CountAsync<BasicDoc>());
    }

    [Fact]
    public async Task Transaction_GetManyAsync_AcrossTheChunkBoundary_SeesEveryUncommittedRow()
    {
        const int count = ChunkSize + 1;
        var store = await CreateStoreWithTableAsync();

        await store.ExecuteInTransactionAsync(async transaction =>
        {
            await transaction.UpsertManyAsync(Docs(count));

            var found = await transaction.GetManyAsync<BasicDoc>(Ids(count));

            Assert.Equal(count, found.Count);
        });

        Assert.Equal(count, await store.CountAsync<BasicDoc>());
    }

    [Fact]
    public async Task Transaction_DeleteAllAsync_SeesItsOwnWritesAndCommitsWithIt()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(3));

        await store.ExecuteInTransactionAsync(async transaction =>
        {
            // Rows written inside the transaction are deleted by its own DeleteAllAsync too, so the
            // returned count covers the uncommitted rows as well as the committed ones.
            await transaction.UpsertAsync("extra-1", new BasicDoc("Extra 1", 101, "e1@example.com"));
            await transaction.UpsertAsync("extra-2", new BasicDoc("Extra 2", 102, "e2@example.com"));

            var deleted = await transaction.DeleteAllAsync<BasicDoc>();

            Assert.Equal(5, deleted);
            Assert.Equal(0, await transaction.CountAsync<BasicDoc>());
            Assert.Empty(await transaction.GetManyAsync<BasicDoc>(["doc-000000", "extra-1"]));
        });

        Assert.Equal(0, await store.CountAsync<BasicDoc>());
    }

    [Fact]
    public async Task Transaction_RolledBackDeleteAllAsync_LeavesTheRowsIntact()
    {
        // A file database: a shared-cache in-memory database locks at table level, so transaction
        // tests that read outside the transaction want a real file.
        var store = await CreateFileStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(4));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            var deleted = await transaction.DeleteAllAsync<BasicDoc>();
            Assert.Equal(4, deleted);

            await transaction.RollbackAsync();
        }

        Assert.Equal(4, await store.CountAsync<BasicDoc>());
        var found = await store.GetManyAsync<BasicDoc>(Ids(4));
        Assert.Equal(4, found.Count);
        Assert.Equal("Name 3", found["doc-000003"].Name);
    }

    [Fact]
    public async Task Transaction_RolledBackDropTableAsync_LeavesTheTableIntact()
    {
        var store = await CreateFileStoreWithTableAsync();
        await store.UpsertManyAsync(Docs(2));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.DropTableAsync<BasicDoc>();
            await transaction.RollbackAsync();
        }

        // SQLite DDL is transactional, so the rolled-back drop left the table and its rows.
        Assert.Equal(2, await store.CountAsync<BasicDoc>());
    }

    private sealed record BasicDoc(string Name, int Index, string Email);
}
