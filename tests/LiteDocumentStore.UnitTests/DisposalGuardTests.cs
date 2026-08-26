using Xunit;

using GuardTable = Xunit.TheoryData<string, System.Func<LiteDocumentStore.IDocumentStore, System.Threading.Tasks.Task>>;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the disposal guard on every member of the public store surface.
/// </summary>
/// <remarks>
/// The table below is the whole surface rather than a sample of the guard routes, so a member
/// added without a guard fails here instead of running against a pool that has already handed
/// its connections back. <see cref="IDocumentStore.IsHealthyAsync"/> is the one deliberate
/// exception and is covered separately: it reports <c>false</c> so a health endpoint can call it
/// on a disposed store.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DisposalGuardTests
{
    private sealed record Doc(string Name, int Value);

    private static readonly Migration SampleMigration =
        new(1, "sample", "CREATE TABLE IF NOT EXISTS sample (id TEXT)", "DROP TABLE IF EXISTS sample");

    private static readonly DocumentQuery<Doc> Query =
        DocumentQuery<Doc>.Where("$.Name", QueryOperator.Equal, "a");

    private static readonly DocumentPatch<Doc> Patch = DocumentPatch<Doc>.Set("$.Value", 1);

    private static readonly IndexOptions IndexOpts = new() { Unique = true };

    private static readonly BlobWriteOptions BlobOpts = new() { ContentType = "application/octet-stream" };

    private static readonly byte[] Payload = [1, 2, 3];

    /// <summary>A fresh stream per call — every entry is invoked once per theory.</summary>
    private static MemoryStream Source() => new([1, 2, 3]);

    private static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    public static GuardTable GuardedOperations()
    {
        var table = new GuardTable();
        AddDocumentOperations(table);
        AddSchemaOperations(table);
        AddBlobOperations(table);
        AddStoreOperations(table);
        return table;
    }

    private static void AddDocumentOperations(GuardTable table)
    {
        table.Add("CreateTableAsync", s => s.CreateTableAsync<Doc>());
        table.Add("UpsertAsync", s => s.UpsertAsync("a", new Doc("a", 1)));
        table.Add("UpsertManyAsync", s => s.UpsertManyAsync([("a", new Doc("a", 1))]));
        table.Add("UpsertWithVersionAsync", s => s.UpsertWithVersionAsync("a", new Doc("a", 1), 0));
        table.Add("DeleteWithVersionAsync", s => s.DeleteWithVersionAsync<Doc>("a", 1));
        table.Add("PatchAsync", s => s.PatchAsync("a", Patch));
        table.Add("PatchWithVersionAsync", s => s.PatchWithVersionAsync("a", Patch, 1));
        table.Add("GetWithVersionAsync", s => s.GetWithVersionAsync<Doc>("a"));
        table.Add("GetAsync", s => s.GetAsync<Doc>("a"));
        table.Add("GetAllAsync", s => s.GetAllAsync<Doc>());
        table.Add("GetManyAsync", s => s.GetManyAsync<Doc>(["a"]));
        table.Add("DeleteAsync", s => s.DeleteAsync<Doc>("a"));
        table.Add("DeleteManyAsync", s => s.DeleteManyAsync<Doc>(["a"]));
        table.Add("DeleteAllAsync", s => s.DeleteAllAsync<Doc>());
        table.Add("ExistsAsync(id)", s => s.ExistsAsync<Doc>("a"));
        table.Add("CountAsync", s => s.CountAsync<Doc>());
        table.Add("QueryAsync(path)", s => s.QueryAsync<Doc, string>("$.Name", "a"));
        table.Add("QueryAsync(query)", s => s.QueryAsync(Query));
        table.Add("CountAsync(query)", s => s.CountAsync(Query));
        table.Add("ExistsAsync(query)", s => s.ExistsAsync(Query));
    }

    private static void AddSchemaOperations(GuardTable table)
    {
        table.Add("CreateIndexAsync", s => s.CreateIndexAsync<Doc>(d => d.Name));
        table.Add("CreateIndexAsync(options)", s => s.CreateIndexAsync<Doc>(d => d.Name, null, IndexOpts));
        table.Add("CreateCompositeIndexAsync", s => s.CreateCompositeIndexAsync<Doc>([d => d.Name]));
        table.Add(
            "CreateCompositeIndexAsync(options)",
            s => s.CreateCompositeIndexAsync<Doc>([d => d.Name], null, IndexOpts));
        table.Add("AddVirtualColumnAsync", s => s.AddVirtualColumnAsync<Doc>(d => d.Name, "name_col"));
        table.Add("DropTableAsync", s => s.DropTableAsync<Doc>());
        table.Add("DropIndexAsync(name)", s => s.DropIndexAsync("idx_doc_name"));
        table.Add("DropIndexAsync(expression)", s => s.DropIndexAsync<Doc>(d => d.Name));
    }

    private static void AddBlobOperations(GuardTable table)
    {
        table.Add("CreateBlobTableAsync", s => s.CreateBlobTableAsync());
        table.Add("RebuildBlobTableAsync", s => s.RebuildBlobTableAsync());
        table.Add("PutBlobAsync(bytes)", s => s.PutBlobAsync("a", Payload));
        table.Add("PutBlobAsync(bytes, options)", s => s.PutBlobAsync("a", Payload, BlobOpts));
        table.Add("PutBlobAsync(stream)", s => s.PutBlobAsync("a", Source(), 3));
        table.Add("PutBlobAsync(stream, options)", s => s.PutBlobAsync("a", Source(), 3, BlobOpts));
        table.Add("PutBlobWithVersionAsync(bytes)", s => s.PutBlobWithVersionAsync("a", Payload, 0));
        table.Add(
            "PutBlobWithVersionAsync(bytes, options)",
            s => s.PutBlobWithVersionAsync("a", Payload, 0, BlobOpts));
        table.Add("PutBlobWithVersionAsync(stream)", s => s.PutBlobWithVersionAsync("a", Source(), 3, 0));
        table.Add(
            "PutBlobWithVersionAsync(stream, options)",
            s => s.PutBlobWithVersionAsync("a", Source(), 3, 0, BlobOpts));
        table.Add("BlobLengthAsync", s => s.BlobLengthAsync("a"));
        table.Add("GetBlobAsync", s => s.GetBlobAsync("a"));
        table.Add("DeleteBlobAsync", s => s.DeleteBlobAsync("a"));
        table.Add("DeleteBlobWithVersionAsync", s => s.DeleteBlobWithVersionAsync("a", 1));
        table.Add("GetBlobInfoAsync", s => s.GetBlobInfoAsync("a"));
        table.Add("ListBlobsAsync", s => s.ListBlobsAsync());
        table.Add("BlobExistsAsync", s => s.BlobExistsAsync("a"));
        table.Add("OpenBlobReadAsync", s => s.OpenBlobReadAsync("a"));
    }

    private static void AddStoreOperations(GuardTable table)
    {
        table.Add("ExecuteRawAsync<T>", s => s.ExecuteRawAsync((c, _) => Task.FromResult(c.State)));
        table.Add("ExecuteRawAsync", s => s.ExecuteRawAsync((_, _) => Task.CompletedTask));
        table.Add("BeginTransactionAsync", s => s.BeginTransactionAsync());
        table.Add("BeginTransactionAsync(mode)", s => s.BeginTransactionAsync(TransactionMode.Immediate));
        table.Add("ExecuteInTransactionAsync", s => s.ExecuteInTransactionAsync(_ => Task.CompletedTask));
        table.Add(
            "ExecuteInTransactionAsync(mode)",
            s => s.ExecuteInTransactionAsync(_ => Task.CompletedTask, TransactionMode.Immediate));
        table.Add("MigrateAsync", s => s.MigrateAsync([SampleMigration]));
        table.Add("MigrateAsync(options)", s => s.MigrateAsync([SampleMigration], MigrationOptions.Default));
        table.Add("GetAppliedMigrationsAsync", s => s.GetAppliedMigrationsAsync());
        table.Add("GetCurrentMigrationVersionAsync", s => s.GetCurrentMigrationVersionAsync());
        table.Add("RollbackToVersionAsync", s => s.RollbackToVersionAsync(0, [SampleMigration]));

        // The three synchronous helpers need no connection but are guarded all the same.
        table.Add("GetTableName", s => Task.FromResult(s.GetTableName<Doc>()));
        table.Add("SerializeDocument", s => Task.FromResult(s.SerializeDocument(new Doc("a", 1))));
        table.Add("DeserializeDocument", s => Task.FromResult(s.DeserializeDocument<Doc>("{}")));
    }

    [Theory]
    [MemberData(nameof(GuardedOperations))]
    public async Task Operation_OnADisposedStore_ThrowsObjectDisposed(
        string operationName,
        Func<IDocumentStore, Task> operation)
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => operation(store));

        Assert.True(
            exception is ObjectDisposedException,
            $"{operationName} did not guard against disposal; got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(GuardedOperations))]
    public async Task Operation_OnALiveStore_IsNotGuarded(
        string operationName,
        Func<IDocumentStore, Task> operation)
    {
        // The mirror of the theory above: it is the guard that must throw, not the operation
        // itself, so a table entry that always fails cannot pass the disposed case vacuously.
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store));

        Assert.False(
            exception is ObjectDisposedException,
            $"{operationName} reported disposal on a live store.");
    }

    [Fact]
    public async Task IsHealthyAsync_OnADisposedStore_ReportsFalseInsteadOfThrowing()
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        Assert.False(await store.IsHealthyAsync());
    }

    [Fact]
    public async Task Dispose_ThenDisposeAsync_IsIdempotent()
    {
        var store = await CreateStoreAsync();

        store.Dispose();
        store.Dispose();
        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ThenDispose_IsIdempotent()
    {
        var store = await CreateStoreAsync();

        await store.DisposeAsync();
        store.Dispose();
    }
}
