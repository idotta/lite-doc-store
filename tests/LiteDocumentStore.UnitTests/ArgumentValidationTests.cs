using Xunit;

using ArgTable = Xunit.TheoryData<string, System.Func<LiteDocumentStore.IDocumentStore, System.Threading.Tasks.Task>>;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Up-front argument validation on the public store surface, grouped by the family each check
/// belongs to rather than by operation: an id that is null, empty or whitespace; a null document,
/// collection or builder; and a negative version, length or offset.
/// </summary>
/// <remarks>
/// The <c>*Many</c> collection contents (null element, duplicate id) are already pinned by
/// <c>DocumentStoreIntegrationTests</c> and <c>BatchWriteIntegrationTests</c>; what is covered
/// here is the single-argument validation those tests never reach. Operations that visibly share
/// one implementation are listed once per public entry point, because it is the entry point a
/// caller binds against.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ArgumentValidationTests
{
    private sealed record Doc(string Name, int Value);

    private static readonly DocumentPatch<Doc> Patch = DocumentPatch<Doc>.Set("$.Value", 1);

    private static readonly byte[] Payload = [1, 2, 3];

    private static MemoryStream Source() => new([1, 2, 3]);

    private static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    public static TheoryData<string, Func<IDocumentStore, string, Task>> IdBearingOperations()
    {
        var table = new TheoryData<string, Func<IDocumentStore, string, Task>>();
        AddDocumentIdOperations(table);
        AddBlobIdOperations(table);
        return table;
    }

    private static void AddDocumentIdOperations(TheoryData<string, Func<IDocumentStore, string, Task>> table)
    {
        table.Add("UpsertAsync", (s, id) => s.UpsertAsync(id, new Doc("a", 1)));
        table.Add("UpsertWithVersionAsync", (s, id) => s.UpsertWithVersionAsync(id, new Doc("a", 1), 0));
        table.Add("DeleteWithVersionAsync", (s, id) => s.DeleteWithVersionAsync<Doc>(id, 1));
        table.Add("PatchAsync", (s, id) => s.PatchAsync(id, Patch));
        table.Add("PatchWithVersionAsync", (s, id) => s.PatchWithVersionAsync(id, Patch, 1));
        table.Add("GetAsync", (s, id) => s.GetAsync<Doc>(id));
        table.Add("GetWithVersionAsync", (s, id) => s.GetWithVersionAsync<Doc>(id));
        table.Add("DeleteAsync", (s, id) => s.DeleteAsync<Doc>(id));
        table.Add("ExistsAsync", (s, id) => s.ExistsAsync<Doc>(id));
    }

    private static void AddBlobIdOperations(TheoryData<string, Func<IDocumentStore, string, Task>> table)
    {
        table.Add("PutBlobAsync(bytes)", (s, id) => s.PutBlobAsync(id, Payload));
        table.Add("PutBlobAsync(stream)", (s, id) => s.PutBlobAsync(id, Source(), 3));
        table.Add("PutBlobWithVersionAsync(bytes)", (s, id) => s.PutBlobWithVersionAsync(id, Payload, 0));
        table.Add("PutBlobWithVersionAsync(stream)", (s, id) => s.PutBlobWithVersionAsync(id, Source(), 3, 0));
        table.Add("GetBlobAsync", (s, id) => s.GetBlobAsync(id));
        table.Add("DeleteBlobAsync", (s, id) => s.DeleteBlobAsync(id));
        table.Add("DeleteBlobWithVersionAsync", (s, id) => s.DeleteBlobWithVersionAsync(id, 1));
        table.Add("BlobLengthAsync", (s, id) => s.BlobLengthAsync(id));
        table.Add("BlobExistsAsync", (s, id) => s.BlobExistsAsync(id));
        table.Add("GetBlobInfoAsync", (s, id) => s.GetBlobInfoAsync(id));
        table.Add("OpenBlobReadAsync", (s, id) => s.OpenBlobReadAsync(id));
    }

    [Theory]
    [MemberData(nameof(IdBearingOperations))]
    public async Task Operation_WithABlankId_ThrowsNamingTheId(
        string operationName,
        Func<IDocumentStore, string, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        foreach (var id in new[] { null!, "", "   ", "\t" })
        {
            var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() => operation(store, id));

            Assert.Equal("id", exception.ParamName);
            Assert.False(
                exception is ArgumentNullException,
                $"{operationName} reported a null id as ArgumentNullException; the family throws ArgumentException.");
        }
    }

    public static ArgTable NullReferenceArguments()
    {
        var table = new ArgTable();
        table.Add("UpsertAsync(data)", s => s.UpsertAsync<Doc>("a", null!));
        table.Add("UpsertWithVersionAsync(data)", s => s.UpsertWithVersionAsync<Doc>("a", null!, 0));
        table.Add("UpsertManyAsync(items)", s => s.UpsertManyAsync<Doc>(null!));
        table.Add("GetManyAsync(ids)", s => s.GetManyAsync<Doc>(null!));
        table.Add("DeleteManyAsync(ids)", s => s.DeleteManyAsync<Doc>(null!));
        table.Add("PatchAsync(patch)", s => s.PatchAsync<Doc>("a", null!));
        table.Add("PatchWithVersionAsync(patch)", s => s.PatchWithVersionAsync<Doc>("a", null!, 1));
        table.Add("QueryAsync(value)", s => s.QueryAsync<Doc, string>("$.Name", null!));
        table.Add("QueryAsync(query)", s => s.QueryAsync<Doc>((DocumentQuery<Doc>)null!));
        table.Add("CountAsync(query)", s => s.CountAsync<Doc>(null!));
        table.Add("ExistsAsync(query)", s => s.ExistsAsync<Doc>((DocumentQuery<Doc>)null!));
        table.Add(
            "CreateIndexAsync(expression)",
            s => s.CreateIndexAsync<Doc>((System.Linq.Expressions.Expression<System.Func<Doc, object>>)null!));
        table.Add("CreateIndexAsync(jsonPath)", s => s.CreateIndexAsync<Doc>((string)null!));
        table.Add(
            "CreateCompositeIndexAsync(expressions)",
            s => s.CreateCompositeIndexAsync<Doc>((System.Linq.Expressions.Expression<System.Func<Doc, object>>[])null!));
        table.Add("CreateCompositeIndexAsync(jsonPaths)", s => s.CreateCompositeIndexAsync<Doc>((string[])null!));
        table.Add(
            "AddVirtualColumnAsync(expression)",
            s => s.AddVirtualColumnAsync<Doc>((System.Linq.Expressions.Expression<System.Func<Doc, object>>)null!, "col"));
        table.Add("AddVirtualColumnAsync(jsonPath)", s => s.AddVirtualColumnAsync<Doc>((string)null!, "col"));
        table.Add("DropIndexAsync(expression)", s => s.DropIndexAsync<Doc>(null!));
        table.Add("PutBlobAsync(source)", s => s.PutBlobAsync("a", (Stream)null!, 3));
        table.Add("PutBlobWithVersionAsync(source)", s => s.PutBlobWithVersionAsync("a", (Stream)null!, 3, 0));
        table.Add("ExecuteRawAsync<T>(operation)", s => s.ExecuteRawAsync<int>(null!));
        table.Add("ExecuteRawAsync(operation)", s => s.ExecuteRawAsync(null!));
        table.Add("ExecuteInTransactionAsync(action)", s => s.ExecuteInTransactionAsync(null!));
        table.Add("MigrateAsync(migrations)", s => s.MigrateAsync(null!));
        table.Add("MigrateAsync(options)", s => s.MigrateAsync([], null!));
        table.Add("RollbackToVersionAsync(migrations)", s => s.RollbackToVersionAsync(0, null!));
        table.Add("SerializeDocument(value)", s => Task.FromResult(s.SerializeDocument<Doc>(null!)));
        return table;
    }

    [Theory]
    [MemberData(nameof(NullReferenceArguments))]
    public async Task Operation_WithANullArgument_ThrowsArgumentNull(
        string operationName,
        Func<IDocumentStore, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store));

        Assert.True(
            exception is ArgumentNullException,
            $"{operationName} did not reject its null argument; got {exception?.GetType().Name ?? "no exception"}.");
    }

    public static ArgTable OutOfRangeArguments()
    {
        var table = new ArgTable();
        table.Add("UpsertWithVersionAsync(expectedVersion)", s => s.UpsertWithVersionAsync("a", new Doc("a", 1), -1));
        table.Add("DeleteWithVersionAsync(expectedVersion)", s => s.DeleteWithVersionAsync<Doc>("a", -1));
        table.Add("PatchWithVersionAsync(expectedVersion)", s => s.PatchWithVersionAsync("a", Patch, -1));
        table.Add("PutBlobWithVersionAsync(expectedVersion)", s => s.PutBlobWithVersionAsync("a", Payload, -1));
        table.Add(
            "PutBlobWithVersionAsync(stream, expectedVersion)",
            s => s.PutBlobWithVersionAsync("a", Source(), 3, -1));
        table.Add("DeleteBlobWithVersionAsync(expectedVersion)", s => s.DeleteBlobWithVersionAsync("a", -1));
        table.Add("PutBlobAsync(length)", s => s.PutBlobAsync("a", Source(), -1));
        table.Add("ListBlobsAsync(skip)", s => s.ListBlobsAsync(skip: -1));
        table.Add("RollbackToVersionAsync(targetVersion)", s => s.RollbackToVersionAsync(-1, []));
        return table;
    }

    [Theory]
    [MemberData(nameof(OutOfRangeArguments))]
    public async Task Operation_WithANegativeArgument_ThrowsArgumentOutOfRange(
        string operationName,
        Func<IDocumentStore, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store));

        Assert.True(
            exception is ArgumentOutOfRangeException,
            $"{operationName} did not reject its negative argument; got {exception?.GetType().Name ?? "no exception"}.");
    }
}
