using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the null-document guard: a stored row whose JSON deserializes to null
/// used to be dropped from read results, so a query returned fewer documents than the table
/// held with no error anywhere.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public class DeserializationIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public DeserializationIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<NullableDoc>();
        return store;
    }

    /// <summary>
    /// Writes a row holding the JSON literal <c>null</c> — reachable only through raw SQL, since
    /// every store write rejects a null document.
    /// </summary>
    private static Task InsertNullDocumentAsync(IDocumentStore store, string id)
    {
        var table = store.GetTableName<NullableDoc>();

        return store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO [{table}] (id, data, version) VALUES (@Id, jsonb('null'), 1)";
            command.Parameters.AddWithValue("@Id", id);

            await command.ExecuteNonQueryAsync(ct);
        });
    }

    [Fact]
    public async Task GetAllAsync_WithANullDocument_ThrowsNamingTheId()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("good", new NullableDoc("Ada", "Boston"));
        await InsertNullDocumentAsync(store, "broken");

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => store.GetAllAsync<NullableDoc>());

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(NullableDoc), exception.TargetType);

        // Identifiable without parsing the message.
        Assert.Equal("broken", exception.Id);
        Assert.Equal(store.GetTableName<NullableDoc>(), exception.TableName);

        // A document read projects json(data), never typeof(data), so the storage class is not
        // observed on this path.
        Assert.Null(exception.StoredTypeName);
    }

    [Fact]
    public async Task GetAllAsync_WithoutANullDocument_ReturnsEveryRow()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertManyAsync(
            [("a", new NullableDoc("Ada", "Boston")), ("b", new NullableDoc("Grace", "Denver"))]);

        var all = await store.GetAllAsync<NullableDoc>();

        Assert.Equal(2, all.Count());
    }

    [Fact]
    public async Task QueryAsync_Structured_WithANullDocument_Throws()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertNullDocumentAsync(store, "broken");

        // json_extract of a JSON null yields NULL, which no '= @Value' predicate matches, so the
        // broken row has to be reached by a query that selects everything.
        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => store.QueryAsync<NullableDoc>(DocumentQuery<NullableDoc>.All()));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_ByJsonPath_WithValidDocuments_StillMatches()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("good", new NullableDoc("Ada", "Boston"));

        var found = await store.QueryAsync<NullableDoc, string>("$.City", "Boston");

        Assert.Single(found);
    }

    [Fact]
    public async Task GetAsync_WithANullDocument_ThrowsInsteadOfLookingMissing()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertNullDocumentAsync(store, "broken");

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => store.GetAsync<NullableDoc>("broken"));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
        Assert.Equal("broken", exception.Id);
        Assert.Equal(store.GetTableName<NullableDoc>(), exception.TableName);
    }

    [Fact]
    public async Task GetWithVersionAsync_WithANullDocument_ThrowsInsteadOfReturningNull()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertNullDocumentAsync(store, "broken");

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => store.GetWithVersionAsync<NullableDoc>("broken"));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetManyAsync_WithANullDocument_Throws()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("good", new NullableDoc("Ada", "Boston"));
        await InsertNullDocumentAsync(store, "broken");

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => store.GetManyAsync<NullableDoc>(["good", "broken"]));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetManyAsync_WithAMissingId_StillOmitsItSilently()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("good", new NullableDoc("Ada", "Boston"));

        var found = await store.GetManyAsync<NullableDoc>(["good", "missing"]);

        Assert.Single(found);
        Assert.True(found.ContainsKey("good"));
    }

    [Fact]
    public async Task GetAsync_WithAMissingId_StillReturnsNull()
    {
        var store = await CreateStoreWithTableAsync();

        Assert.Null(await store.GetAsync<NullableDoc>("missing"));
    }

    private sealed record NullableDoc(string Name, string City);
}
