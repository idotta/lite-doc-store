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
        Assert.Equal(store.GetTableName<VersionedPerson>(), ex.TableName);
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

    // ------------------------------------------------- version column default / legacy rows

    [Fact]
    public async Task RawInsert_WithoutAVersion_LandsAtVersionOne()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertRawAsync(store, "p1", new VersionedPerson("Ada"), version: null);

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");

        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task UpsertWithVersion_ExpectedZeroOnALegacyVersionZeroRow_UpdatesItToOne()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertRawAsync(store, "p1", new VersionedPerson("Ada"), version: 0);

        var newVersion = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada L."), expectedVersion: 0);

        Assert.Equal(1, newVersion);
        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal("Ada L.", stored.Data.Name);
        Assert.Equal(1, stored.Version);
    }

    // ------------------------------------------------------------------- conflict detail

    [Fact]
    public async Task UpsertWithVersion_ExpectedZeroOnExistingDocument_ReportsAlreadyExists()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Grace"), expectedVersion: 0));

        Assert.Equal(ConcurrencyConflictKind.AlreadyExists, exception.Kind);
        Assert.Equal(0, exception.ExpectedVersion);
        Assert.Equal(1, exception.ActualVersion);
        Assert.Equal("p1", exception.DocumentId);
    }

    [Fact]
    public async Task UpsertWithVersion_StaleVersion_ReportsBothVersionsAndAMismatch()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);
        await store.UpsertAsync("p1", new VersionedPerson("Ada L."));

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("p1", new VersionedPerson("Stale"), expectedVersion: 1));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, exception.Kind);
        Assert.Equal(1, exception.ExpectedVersion);
        Assert.Equal(2, exception.ActualVersion);
    }

    [Fact]
    public async Task UpsertWithVersion_OnAMissingDocument_ReportsDocumentNotFound()
    {
        var store = await CreateStoreWithTableAsync();

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.UpsertWithVersionAsync("missing", new VersionedPerson("Ada"), expectedVersion: 3));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, exception.Kind);
        Assert.Equal(3, exception.ExpectedVersion);
        Assert.Null(exception.ActualVersion);
    }

    [Fact]
    public async Task UpsertWithVersion_ReturnsTheStoredVersion_NotAComputedOne()
    {
        var store = await CreateStoreWithTableAsync();

        var v1 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);
        var v2 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada L."), expectedVersion: v1);
        var v3 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada Lovelace"), expectedVersion: v2);

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal(stored.Version, v3);
        Assert.Equal([1L, 2L, 3L], new[] { v1, v2, v3 });
    }

    // --------------------------------------------------------------- versioned delete

    [Fact]
    public async Task DeleteWithVersion_MatchingVersion_DeletesTheDocument()
    {
        var store = await CreateStoreWithTableAsync();
        var version = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        await store.DeleteWithVersionAsync<VersionedPerson>("p1", version);

        Assert.Null(await store.GetWithVersionAsync<VersionedPerson>("p1"));
    }

    [Fact]
    public async Task DeleteWithVersion_StaleVersion_ThrowsAndLeavesTheRow()
    {
        var store = await CreateStoreWithTableAsync();
        var v1 = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);
        await store.UpsertAsync("p1", new VersionedPerson("Ada L."));

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteWithVersionAsync<VersionedPerson>("p1", v1));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, exception.Kind);
        Assert.Equal(1, exception.ExpectedVersion);
        Assert.Equal(2, exception.ActualVersion);

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal("Ada L.", stored.Data.Name);
    }

    [Fact]
    public async Task DeleteWithVersion_OnAMissingDocument_ReportsDocumentNotFound()
    {
        var store = await CreateStoreWithTableAsync();

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteWithVersionAsync<VersionedPerson>("missing", 1));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, exception.Kind);
        Assert.Null(exception.ActualVersion);
    }

    [Fact]
    public async Task DeleteWithVersion_OnALegacyVersionZeroRow_Deletes()
    {
        var store = await CreateStoreWithTableAsync();
        await InsertRawAsync(store, "p1", new VersionedPerson("Ada"), version: 0);

        await store.DeleteWithVersionAsync<VersionedPerson>("p1", expectedVersion: 0);

        Assert.Null(await store.GetWithVersionAsync<VersionedPerson>("p1"));
    }

    [Fact]
    public async Task DeleteWithVersion_ExpectedZeroOnANonZeroRow_ReportsVersionMismatch()
    {
        // Expected version 0 means "insert" only for a write; for a delete it targets a legacy
        // row still at 0, so a row at version 1 is a mismatch, not AlreadyExists.
        var store = await CreateStoreWithTableAsync();
        await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.DeleteWithVersionAsync<VersionedPerson>("p1", expectedVersion: 0));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, exception.Kind);
        Assert.Equal(0, exception.ExpectedVersion);
        Assert.Equal(1, exception.ActualVersion);

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task DeleteWithVersion_NegativeExpectedVersion_ThrowsArgumentOutOfRange()
    {
        var store = await CreateStoreWithTableAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.DeleteWithVersionAsync<VersionedPerson>("p1", expectedVersion: -1));
    }

    [Fact]
    public async Task DeleteWithVersion_InsideARolledBackTransaction_KeepsTheDocument()
    {
        var store = await CreateStoreWithTableAsync();
        var version = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.DeleteWithVersionAsync<VersionedPerson>("p1", version);
            await transaction.RollbackAsync();
        }

        var stored = await store.GetWithVersionAsync<VersionedPerson>("p1");
        Assert.NotNull(stored);
        Assert.Equal(version, stored.Version);
    }

    [Fact]
    public async Task DeleteWithVersion_InsideACommittedTransaction_DeletesTheDocument()
    {
        var store = await CreateStoreWithTableAsync();
        var version = await store.UpsertWithVersionAsync("p1", new VersionedPerson("Ada"), expectedVersion: 0);

        await store.ExecuteInTransactionAsync(
            transaction => transaction.DeleteWithVersionAsync<VersionedPerson>("p1", version));

        Assert.Null(await store.GetWithVersionAsync<VersionedPerson>("p1"));
    }

    /// <summary>
    /// Inserts a row the way a consumer's own SQL would, optionally without naming the version
    /// column so the table default applies.
    /// </summary>
    private static Task InsertRawAsync(
        IDocumentStore store,
        string id,
        VersionedPerson person,
        long? version)
    {
        var table = store.GetTableName<VersionedPerson>();

        return store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = version is null
                ? $"INSERT INTO [{table}] (id, data) VALUES (@Id, jsonb(@Data))"
                : $"INSERT INTO [{table}] (id, data, version) VALUES (@Id, jsonb(@Data), @Version)";
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@Data", store.SerializeDocument(person));
            if (version is { } value)
            {
                command.Parameters.AddWithValue("@Version", value);
            }

            await command.ExecuteNonQueryAsync(ct);
        });
    }

    private sealed record VersionedPerson(string Name);
}
