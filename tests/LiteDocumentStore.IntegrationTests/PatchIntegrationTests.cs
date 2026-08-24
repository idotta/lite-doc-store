using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// End-to-end coverage for <c>PatchAsync</c> against real SQLite: the concurrent-writer
/// regression it exists to close, the version bump, each conflict kind, the stored shape of
/// every scalar type, and the transaction paths.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PatchIntegrationTests : IAsyncLifetime
{
    private sealed record Gadget(string Id, string Name, string? Nickname, int Quantity, bool Active);

    private static readonly Gadget Seed = new("g1", "Anvil", "Andy", 10, Active: false);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Gadget>();
        await _store.UpsertAsync(Seed.Id, Seed);
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    // Reads the document as stored, so a test can assert the JSON shape a patch wrote rather
    // than what deserialization makes of it.
    private Task<string?> RawJsonAsync(string id = "g1") =>
        _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT json(data) FROM [{_store.GetTableName<Gadget>()}] WHERE id = @Id", ct, ("Id", id)));

    // --- The regression this closes ------------------------------------------------------

    [Fact]
    public async Task PatchAsync_LeavesAConcurrentWritersEditsToOtherFieldsIntact()
    {
        // The snapshot a read-modify-write would carry into its upsert.
        var stale = (await _store.GetAsync<Gadget>("g1"))!;

        // A concurrent writer changes a field this caller never looked at.
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Quantity", 99));

        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"));
        var patched = await _store.GetAsync<Gadget>("g1");
        Assert.Equal("Bolt", patched!.Name);
        Assert.Equal(99, patched.Quantity);

        // The same edit as a read-modify-write puts the stale quantity back — the bug patching exists to avoid.
        await _store.UpsertAsync("g1", stale with { Name = "Bolt" });
        Assert.Equal(10, (await _store.GetAsync<Gadget>("g1"))!.Quantity);
    }

    [Fact]
    public async Task PatchAsync_WithSeveralOperations_AppliesThemAllAndBumpsTheVersionOnce()
    {
        var version = await _store.PatchAsync(
            "g1",
            DocumentPatch<Gadget>.Set("$.Name", "Bolt")
                .AndSet("$.Quantity", 20)
                .AndRemove("$.Nickname"));

        Assert.Equal(2, version);

        var stored = await _store.GetWithVersionAsync<Gadget>("g1");
        Assert.Equal(2, stored!.Version);
        Assert.Equal("Bolt", stored.Data.Name);
        Assert.Equal(20, stored.Data.Quantity);
        Assert.Null(stored.Data.Nickname);
    }

    [Fact]
    public async Task PatchAsync_ReturnsTheVersionSqliteStored()
    {
        var first = await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Quantity", 11));
        var second = await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Quantity", 12));

        Assert.Equal(2, first);
        Assert.Equal(3, second);
    }

    // --- Stored shape --------------------------------------------------------------------

    [Fact]
    public async Task PatchAsync_Remove_DropsTheFieldRatherThanNullingIt()
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Remove("$.Nickname"));

        var json = await RawJsonAsync();
        Assert.DoesNotContain("Nickname", json!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchAsync_SetToNull_KeepsTheFieldWithAJsonNull()
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Nickname", null));

        Assert.Contains("\"Nickname\":null", (await RawJsonAsync())!, StringComparison.Ordinal);
    }

    // SQLite has no boolean: bound directly, true would store the number 1 and the document
    // would no longer deserialize into a bool.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PatchAsync_SetABool_WritesJsonTrueOrFalseRatherThanOneOrZero(bool value)
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Active", value));

        var json = await RawJsonAsync();
        Assert.Contains($"\"Active\":{(value ? "true" : "false")}", json!, StringComparison.Ordinal);
        Assert.Equal(value, (await _store.GetAsync<Gadget>("g1"))!.Active);
    }

    public static TheoryData<object?, string> StoredShapes => new()
    {
        { "Ada", "\"Field\":\"Ada\"" },
        { 42, "\"Field\":42" },
        { 42L, "\"Field\":42" },
        { 0.5d, "\"Field\":0.5" },
        { 0.1f, "\"Field\":0.1" },
        { 10.05m, "\"Field\":10.05" },
        { ulong.MaxValue, "\"Field\":18446744073709551615" },
        { new DateTime(2024, 3, 1), "\"Field\":\"2024-03-01T00:00:00\"" },
        { new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), "\"Field\":\"2024-03-01T00:00:00Z\"" },
        { new byte[] { 1, 2, 3 }, "\"Field\":\"AQID\"" },
        { null, "\"Field\":null" }
    };

    [Theory]
    [MemberData(nameof(StoredShapes))]
    public async Task PatchAsync_StoresEachScalarInTheShapeSystemTextJsonWrites(object? value, string expected)
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Field", value));

        Assert.Contains(expected, (await RawJsonAsync())!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchAsync_AGuid_StoresItsTextSoAQueryOverItStillMatches()
    {
        var id = Guid.NewGuid();
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Owner", id));

        var found = await _store.QueryAsync<Gadget, Guid>("$.Owner", id);
        Assert.Equal("g1", Assert.Single(found).Id);
    }

    [Fact]
    public async Task PatchAsync_KeepsTheColumnInJsonbSoTheDocumentStillReadsBack()
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"));

        // A json_* function would have left text in the column; jsonb() output starts with a
        // header byte, and typeof() sees a blob either way, so read it back through the store.
        var stored = await _store.GetAsync<Gadget>("g1");
        Assert.Equal("Bolt", stored!.Name);

        var columnType = await _store.ExecuteRawAsync((connection, ct) =>
            connection.QueryFirstStringAsync(
                $"SELECT typeof(data) FROM [{_store.GetTableName<Gadget>()}] WHERE id = 'g1'", ct));
        Assert.Equal("blob", columnType);
    }

    // --- Operation caps ------------------------------------------------------------------

    // The caps come from SQLITE_MAX_FUNCTION_ARG, so they are only right if real SQLite accepts
    // exactly that many. One more fails there with "too many arguments on function jsonb_set",
    // which is what the generator's ArgumentException replaces.
    [Fact]
    public async Task PatchAsync_AtTheSetCap_IsAcceptedBySqlite()
    {
        var patch = DocumentPatch<Gadget>.Set("$.F0", 0);
        for (var i = 1; i < SqlGenerator.MaxPatchSetOperations; i++)
        {
            patch = patch.AndSet($"$.F{i}", i);
        }

        Assert.Equal(2, await _store.PatchAsync("g1", patch));
    }

    [Fact]
    public async Task PatchAsync_AtTheRemoveCap_IsAcceptedBySqlite()
    {
        var patch = DocumentPatch<Gadget>.Remove("$.F0");
        for (var i = 1; i < SqlGenerator.MaxPatchRemoveOperations; i++)
        {
            patch = patch.AndRemove($"$.F{i}");
        }

        Assert.Equal(2, await _store.PatchAsync("g1", patch));
    }

    [Fact]
    public async Task PatchAsync_OneSetBeyondTheCap_ThrowsBeforeReachingSqlite()
    {
        var patch = DocumentPatch<Gadget>.Set("$.F0", 0);
        for (var i = 1; i <= SqlGenerator.MaxPatchSetOperations; i++)
        {
            patch = patch.AndSet($"$.F{i}", i);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => _store.PatchAsync("g1", patch));
    }

    // A remove binds no parameter, so nothing but this cap stands between it and SQLite.
    [Fact]
    public async Task PatchAsync_OneRemoveBeyondTheCap_ThrowsBeforeReachingSqlite()
    {
        var patch = DocumentPatch<Gadget>.Remove("$.F0");
        for (var i = 1; i <= SqlGenerator.MaxPatchRemoveOperations; i++)
        {
            patch = patch.AndRemove($"$.F{i}");
        }

        await Assert.ThrowsAsync<ArgumentException>(() => _store.PatchAsync("g1", patch));
    }

    // --- Conflicts -----------------------------------------------------------------------

    [Fact]
    public async Task PatchAsync_OnAMissingDocument_ThrowsDocumentNotFound()
    {
        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => _store.PatchAsync("nope", DocumentPatch<Gadget>.Set("$.Name", "Bolt")));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, exception.Kind);
        Assert.Equal("nope", exception.DocumentId);
        Assert.Null(exception.ExpectedVersion);
    }

    [Fact]
    public async Task PatchWithVersionAsync_WithTheStoredVersion_AppliesThePatch()
    {
        var version = await _store.PatchWithVersionAsync(
            "g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"), expectedVersion: 1);

        Assert.Equal(2, version);
        Assert.Equal("Bolt", (await _store.GetAsync<Gadget>("g1"))!.Name);
    }

    [Fact]
    public async Task PatchWithVersionAsync_WithAStaleVersion_ThrowsVersionMismatch()
    {
        await _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Quantity", 11));

        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => _store.PatchWithVersionAsync(
                "g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"), expectedVersion: 1));

        Assert.Equal(ConcurrencyConflictKind.VersionMismatch, exception.Kind);
        Assert.Equal(1, exception.ExpectedVersion);
        Assert.Equal(2, exception.ActualVersion);
    }

    [Fact]
    public async Task PatchWithVersionAsync_OnAMissingDocument_ThrowsDocumentNotFound()
    {
        var exception = await Assert.ThrowsAsync<ConcurrencyException>(
            () => _store.PatchWithVersionAsync(
                "nope", DocumentPatch<Gadget>.Set("$.Name", "Bolt"), expectedVersion: 1));

        Assert.Equal(ConcurrencyConflictKind.DocumentNotFound, exception.Kind);
        Assert.Null(exception.ActualVersion);
    }

    [Fact]
    public async Task PatchWithVersionAsync_WithANegativeVersion_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.PatchWithVersionAsync(
                "g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"), expectedVersion: -1));

    [Fact]
    public async Task PatchAsync_WithANullPatch_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.PatchAsync<Gadget>("g1", null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PatchAsync_WithABlankId_Throws(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.PatchAsync(id, DocumentPatch<Gadget>.Set("$.Name", "Bolt")));

    // --- Transactions --------------------------------------------------------------------

    [Fact]
    public async Task PatchAsync_InsideATransaction_CommitsWithIt()
    {
        await _store.ExecuteInTransactionAsync(async transaction =>
        {
            await transaction.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"));
            await transaction.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Quantity", 20));
        });

        var stored = await _store.GetAsync<Gadget>("g1");
        Assert.Equal("Bolt", stored!.Name);
        Assert.Equal(20, stored.Quantity);
    }

    [Fact]
    public async Task PatchAsync_InARolledBackTransaction_LeavesTheDocumentUnchanged()
    {
        await using (var transaction = await _store.BeginTransactionAsync())
        {
            await transaction.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"));
            await transaction.RollbackAsync();
        }

        var stored = await _store.GetWithVersionAsync<Gadget>("g1");
        Assert.Equal("Anvil", stored!.Data.Name);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task PatchAsync_AfterTheTransactionCommitted_Throws()
    {
        var transaction = await _store.BeginTransactionAsync();
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt")));

        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task PatchAsync_WithAnAlreadyCancelledToken_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.PatchAsync("g1", DocumentPatch<Gadget>.Set("$.Name", "Bolt"), cancellation.Token));
    }
}
