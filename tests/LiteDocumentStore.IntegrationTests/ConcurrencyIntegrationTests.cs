using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for optimistic concurrency (versioned upsert / get) against real SQLite.
/// </summary>
[Collection(nameof(LiteDocumentStoreCollection))]
public class ConcurrencyIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public ConcurrencyIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<VersionedPerson>();
        return store;
    }

    [Fact]
    public async Task UpsertWithVersion_ExpectedZeroOnNewDocument_InsertsAtVersionOne()
    {
        var store = await CreateStoreWithTableAsync();

        var newVersion = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        Assert.Equal(1, newVersion);
        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal("Ada", stored.Data.Name);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task UpsertWithVersion_ExpectedZeroOnExistingDocument_ThrowsConcurrencyException()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Grace"), expectedVersion: 0));

        Assert.Equal("p1", ex.DocumentId);
        Assert.Equal(nameof(VersionedPerson), ex.TableName);
    }

    [Fact]
    public async Task UpsertWithVersion_MatchingVersion_UpdatesAndIncrements()
    {
        var store = await CreateStoreWithTableAsync();
        var v1 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        var v2 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada Lovelace"), expectedVersion: v1);

        Assert.Equal(2, v2);
        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal("Ada Lovelace", stored.Data.Name);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task UpsertWithVersion_StaleVersion_ThrowsAndLeavesRowUntouched()
    {
        var store = await CreateStoreWithTableAsync();
        var v1 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada Lovelace"), expectedVersion: v1);

        // A second writer still holding v1 must lose.
        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Imposter"), expectedVersion: v1));

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal("Ada Lovelace", stored.Data.Name);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task UpsertWithVersion_NonZeroExpectedOnMissingDocument_ThrowsConcurrencyException()
    {
        var store = await CreateStoreWithTableAsync();

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("missing", new VersionedPerson("Ghost"), expectedVersion: 3));
    }

    [Fact]
    public async Task UpsertWithVersion_NegativeExpectedVersion_ThrowsArgumentOutOfRange()
    {
        var store = await CreateStoreWithTableAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: -1));
    }

    [Fact]
    public async Task GetWithVersion_MissingDocument_ReturnsNull()
    {
        var store = await CreateStoreWithTableAsync();

        var stored = await store.GetWithVersionAsync<VersionedPerson>("missing");

        Assert.Null(stored);
    }

    [Fact]
    public async Task PlainUpsert_BumpsVersion_SoMixedUsageStaysCoherent()
    {
        var store = await CreateStoreWithTableAsync();
        var v1 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        // Last-writer-wins write in between.
        await store.UpsertAsync("p1", new VersionedPerson("Ada L."));

        // The CAS writer holding v1 must now conflict.
        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Stale"), expectedVersion: v1));

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task UpsertMany_BumpsVersions_SoMixedUsageStaysCoherent()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        await store.UpsertManyAsync([("p1", new VersionedPerson("Ada L.")), ("p2", new VersionedPerson("Grace"))]);

        var p1 = await store.GetWithVersionAsync<VersionedPerson>("p1");
        var p2 = await store.GetWithVersionAsync<VersionedPerson>("p2");
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Equal(2, p1.Version);
        Assert.Equal(1, p2.Version);
    }

    private sealed record VersionedPerson(string Name);
}
