using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// End-to-end coverage for the options-bearing <c>CreateIndexAsync</c> overloads against real
/// SQLite: a UNIQUE index actually rejecting a duplicate, a partial index tolerating duplicates
/// among the rows it excludes, <c>NOCASE</c> uniqueness, a descending index still being used,
/// and the name pre-check that skips an existing index options and all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexOptionsIntegrationTests : IAsyncLifetime
{
    private sealed record Member(string Id, string? Email, string? DeletedAt, int Age);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Member>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private Task<string?> IndexDdlAsync(string indexName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

    // --- UNIQUE, the load-bearing option --------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithUnique_RejectsADuplicateValue()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!, null, new IndexOptions { Unique = true });

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", null, 30));

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m2", new Member("m2", "a@b.c", null, 40)));
        Assert.Contains("UNIQUE constraint failed", exception.Message, StringComparison.Ordinal);

        // A different value still writes, so the constraint is on the value and not the table.
        await _store.UpsertAsync("m3", new Member("m3", "d@e.f", null, 50));
        Assert.Equal(2, await _store.CountAsync<Member>());
    }

    [Fact]
    public async Task CreateIndexAsync_WithUniqueAndNoCase_TreatsCaseVariantsAsDuplicates()
    {
        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            null,
            new IndexOptions { Unique = true, Collation = "NOCASE" });

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", null, 30));

        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m2", new Member("m2", "A@B.C", null, 40)));
    }

    [Fact]
    public async Task CreateIndexAsync_WithUniqueAndAFilter_ToleratesDuplicatesAmongTheExcludedRows()
    {
        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            null,
            new IndexOptions { Unique = true, Filter = IndexFilter.IsNotNull("$.Email") });

        // Every row without an email is outside the index, so they cannot collide.
        await _store.UpsertAsync("m1", new Member("m1", null, null, 30));
        await _store.UpsertAsync("m2", new Member("m2", null, null, 40));
        Assert.Equal(2, await _store.CountAsync<Member>());

        await _store.UpsertAsync("m3", new Member("m3", "a@b.c", null, 50));
        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m4", new Member("m4", "a@b.c", null, 60)));
    }

    [Fact]
    public async Task CreateIndexAsync_WithAChainedFilter_AppliesEveryTerm()
    {
        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            "idx_members_live_email",
            new IndexOptions
            {
                Unique = true,
                Filter = IndexFilter.IsNotNull("$.Email").AndIsNull("$.DeletedAt")
            });

        // Soft-deleted rows are outside the index, so the same email may repeat there.
        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "2026-01-01", 30));
        await _store.UpsertAsync("m2", new Member("m2", "a@b.c", "2026-02-01", 40));

        await _store.UpsertAsync("m3", new Member("m3", "a@b.c", null, 50));
        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m4", new Member("m4", "a@b.c", null, 60)));
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WithUnique_ConstrainsThePairAndNotEitherColumn()
    {
        await _store.CreateCompositeIndexAsync<Member>(
            [x => x.Email!, x => x.Age],
            null,
            new IndexOptions { Unique = true });

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", null, 30));

        // The same email at a different age is a different pair.
        await _store.UpsertAsync("m2", new Member("m2", "a@b.c", null, 40));

        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m3", new Member("m3", "a@b.c", null, 30)));
    }

    // --- The DDL that reaches SQLite ------------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithEveryOption_StoresThatDdl()
    {
        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            "idx_members_email_all",
            new IndexOptions
            {
                Unique = true,
                Collation = "NOCASE",
                Descending = true,
                Filter = IndexFilter.IsNotNull("$.Email")
            });

        var ddl = await IndexDdlAsync("idx_members_email_all");
        Assert.Equal(
            "CREATE UNIQUE INDEX [idx_members_email_all] ON [Member] " +
            "(json_extract(data, '$.Email') COLLATE NOCASE DESC) " +
            "WHERE json_extract(data, '$.Email') IS NOT NULL",
            ddl);
    }

    [Fact]
    public async Task CreateIndexAsync_WhenDescending_TheIndexStillServesADescendingOrderBy()
    {
        await _store.CreateIndexAsync<Member>(x => x.Age, "idx_members_age_desc", new IndexOptions { Descending = true });

        var plan = await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "EXPLAIN QUERY PLAN SELECT id FROM [Member] ORDER BY json_extract(data, '$.Age') DESC";

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(3));
            }

            return string.Join(" | ", rows);
        });

        Assert.Contains("idx_members_age_desc", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexAsync_WhenTheNameAlreadyExists_KeepsTheExistingIndexOptionsAndAll()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!, "idx_members_email");

        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            "idx_members_email",
            new IndexOptions { Unique = true });

        var ddl = await IndexDdlAsync("idx_members_email");
        Assert.DoesNotContain("UNIQUE", ddl, StringComparison.Ordinal);

        // Dropping first is what makes the new options take effect.
        await _store.DropIndexAsync("idx_members_email");
        await _store.CreateIndexAsync<Member>(
            x => x.Email!,
            "idx_members_email",
            new IndexOptions { Unique = true });

        Assert.Contains("UNIQUE", await IndexDdlAsync("idx_members_email"), StringComparison.Ordinal);
    }

    // --- Argument validation and the transaction path -------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithNullOptions_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.CreateIndexAsync<Member>(x => x.Email!, null, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.CreateCompositeIndexAsync<Member>([x => x.Email!], null, null!));
    }

    [Fact]
    public async Task CreateIndexAsync_WithAnInvalidCollation_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>(x => x.Email!, null, new IndexOptions { Collation = "NO CASE" }));
    }

    [Fact]
    public async Task CreateIndexAsync_WithAnAlreadyCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.CreateIndexAsync<Member>(x => x.Email!, null, new IndexOptions { Unique = true }, cts.Token));
    }

    [Fact]
    public async Task CreateIndexAsync_OnATransaction_TakesEffectOnCommit()
    {
        await using (var transaction = await _store.BeginTransactionAsync())
        {
            await transaction.CreateIndexAsync<Member>(
                x => x.Email!,
                "idx_members_email_tx",
                new IndexOptions { Unique = true });
            await transaction.CommitAsync();
        }

        Assert.Contains("UNIQUE", await IndexDdlAsync("idx_members_email_tx"), StringComparison.Ordinal);

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", null, 30));
        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m2", new Member("m2", "a@b.c", null, 40)));
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_OnARolledBackTransaction_CreatesNothing()
    {
        await using (var transaction = await _store.BeginTransactionAsync())
        {
            await transaction.CreateCompositeIndexAsync<Member>(
                [x => x.Email!, x => x.Age],
                "idx_members_pair_tx",
                new IndexOptions { Unique = true });
            await transaction.RollbackAsync();
        }

        Assert.Null(await IndexDdlAsync("idx_members_pair_tx"));
    }
}
