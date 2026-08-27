using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Index and virtual-column DDL against a store whose serialized names diverge from the CLR
/// member names — a naming policy, a <c>[JsonPropertyName]</c>, or both. The path a
/// property-access expression produces has to be the key the documents carry: an index over a
/// path no row has is NULL in every row, and SQLite counts each NULL in a unique index as
/// distinct, so the UNIQUE the caller declared would accept every duplicate.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SerializedNameDdlIntegrationTests : IAsyncLifetime
{
    private sealed record Member(
        string Id,
        [property: JsonPropertyName("email_address")] string? Email,
        string? City,
        int Age)
    {
        [JsonExtensionData]
        public Dictionary<string, object>? Extra { get; set; }
    }

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = DocumentStoreOptions.ForInMemory();

        // camelCase for the plain members, and the attribute for Email: both diverge from the
        // member name, which is what the derivation used to emit.
        options.SerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _store = await new DocumentStoreFactory().CreateAsync(options);
        await _store.CreateTableAsync<Member>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private Task<string?> IndexDdlAsync(string indexName) =>
        _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

    // --- The derivation ------------------------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithAJsonPropertyName_IndexesTheSerializedPath()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);

        var ddl = await IndexDdlAsync("idx_Member_email_address");

        Assert.NotNull(ddl);
        Assert.Contains("json_extract(data, '$.email_address')", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexAsync_WithANamingPolicy_IndexesTheSerializedPath()
    {
        await _store.CreateIndexAsync<Member>(x => x.City!);

        var ddl = await IndexDdlAsync("idx_Member_city");

        Assert.NotNull(ddl);
        Assert.Contains("json_extract(data, '$.city')", ddl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The finding this file exists for: the index used to be created over <c>$.Email</c>, which
    /// no document carries, so every duplicate was accepted.
    /// </summary>
    [Fact]
    public async Task CreateIndexAsync_WithUnique_ActuallyRejectsADuplicate()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!, null, new IndexOptions { Unique = true });

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "Boston", 30));

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m2", new Member("m2", "a@b.c", "Denver", 40)));
        Assert.Contains("UNIQUE constraint failed", exception.Message, StringComparison.Ordinal);

        await _store.UpsertAsync("m3", new Member("m3", "d@e.f", "Denver", 50));
        Assert.Equal(2, await _store.CountAsync<Member>());
    }

    [Fact]
    public async Task CreateIndexAsync_IndexesThePathTheQueryApiTakes()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);
        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "Boston", 30));

        var query = DocumentQuery<Member>.Where("$.email_address", QueryOperator.Equal, "a@b.c");
        var generated = SqlGenerator.GenerateQuerySql(
            _store.GetTableName<Member>(),
            query.Predicates,
            query.Orderings,
            query.SkipCount,
            query.TakeCount);

        var plan = await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + generated.Sql;
            for (var i = 0; i < generated.ParameterValues.Count; i++)
            {
                command.Parameters.AddWithValue("@p" + i, generated.ParameterValues[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(3));
            }

            return string.Join(" | ", rows);
        });

        Assert.Contains("idx_Member_email_address", plan, StringComparison.Ordinal);
        Assert.Single(await _store.QueryAsync(query));
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_IndexesTheSerializedPaths()
    {
        await _store.CreateCompositeIndexAsync<Member>([x => x.City!, x => x.Age]);

        var ddl = await IndexDdlAsync("idx_Member_composite_city_age");

        Assert.NotNull(ddl);
        Assert.Contains("json_extract(data, '$.city')", ddl, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.age')", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_ProjectsAColumnThatIsNotNull()
    {
        await _store.AddVirtualColumnAsync<Member>(x => x.Email!, "email_col");
        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "Boston", 30));

        var projected = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT email_col FROM [Member] WHERE id = @Id",
            ct,
            ("Id", "m1")));

        Assert.Equal("a@b.c", projected);
    }

    [Fact]
    public async Task DropIndexAsync_DropsTheIndexCreateIndexAsyncCreated()
    {
        await _store.CreateIndexAsync<Member>(x => x.Email!);
        Assert.NotNull(await IndexDdlAsync("idx_Member_email_address"));

        await _store.DropIndexAsync<Member>(x => x.Email!);

        Assert.Null(await IndexDdlAsync("idx_Member_email_address"));
    }

    /// <summary>
    /// Extension data is the second shape of the same failure: the member has a getter and a
    /// name, but its entries serialize into the containing object, so an index over the member's
    /// own name is NULL in every row and a UNIQUE over it enforces nothing.
    /// </summary>
    [Fact]
    public async Task CreateIndexAsync_OverExtensionData_ThrowsInsteadOfIndexingNothing()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>(x => x.Extra!, null, new IndexOptions { Unique = true }));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("JsonExtensionData", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_OverExtensionData_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Member>(x => x.Extra!, "extra_col"));
    }

    [Fact]
    public async Task ExtensionDataKeysAreReachableAsStringPaths()
    {
        // The rejection is not a dead end: the key itself is an ordinary path.
        await _store.CreateIndexAsync<Member>("$.nickname", "idx_member_nickname");
        await _store.UpsertAsync(
            "m1",
            new Member("m1", "a@b.c", "Boston", 30) { Extra = new Dictionary<string, object> { ["nickname"] = "ace" } });

        var found = await _store.QueryAsync<Member, string>("$.nickname", "ace");

        Assert.Single(found);
        Assert.NotNull(await IndexDdlAsync("idx_member_nickname"));
    }

    // --- Derived index names --------------------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithAnArrayPathAndNoIndexName_ThrowsNamingThePath()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$.tags[0]"));

        // The derived name would carry the brackets and be rejected as an identifier, which
        // reported the failure against an index name the caller never passed.
        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("explicit index name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WithAnArrayPathAndNoIndexName_ThrowsNamingThePaths()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Member>(["$.city", "$.tags[0]"]));

        Assert.Equal("jsonPaths", exception.ParamName);
    }

    [Fact]
    public async Task CreateIndexAsync_WithAnArrayPathAndAnExplicitName_Creates()
    {
        await _store.CreateIndexAsync<Member>("$.tags[0]", "idx_member_first_tag");

        var ddl = await IndexDdlAsync("idx_member_first_tag");

        Assert.NotNull(ddl);
        Assert.Contains("json_extract(data, '$.tags[0]')", ddl, StringComparison.Ordinal);
    }

    // --- The string-path overloads --------------------------------------------------------

    [Fact]
    public async Task CreateIndexAsync_WithAStringPath_UsesItVerbatim()
    {
        await _store.CreateIndexAsync<Member>("$.email_address", null, new IndexOptions { Unique = true });

        var ddl = await IndexDdlAsync("idx_Member_email_address");
        Assert.NotNull(ddl);
        Assert.Contains("CREATE UNIQUE INDEX", ddl, StringComparison.Ordinal);

        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "Boston", 30));
        await Assert.ThrowsAsync<SqliteException>(
            () => _store.UpsertAsync("m2", new Member("m2", "a@b.c", "Denver", 40)));
    }

    [Fact]
    public async Task CreateIndexAsync_WithAStringPath_ValidatesIt()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Member>("$.email'; DROP TABLE [Member]; --"));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    [Fact]
    public async Task CreateCompositeIndexAsync_WithStringPaths_IndexesThemInOrder()
    {
        await _store.CreateCompositeIndexAsync<Member>(["$.city", "$.age"], "idx_member_city_age_strings");

        var ddl = await IndexDdlAsync("idx_member_city_age_strings");

        Assert.NotNull(ddl);
        Assert.Contains(
            "(json_extract(data, '$.city'), json_extract(data, '$.age'))",
            ddl,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithAStringPath_ProjectsThatPath()
    {
        await _store.AddVirtualColumnAsync<Member>("$.city", "city_col", createIndex: true);
        await _store.UpsertAsync("m1", new Member("m1", "a@b.c", "Boston", 30));

        var projected = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT city_col FROM [Member] WHERE id = @Id",
            ct,
            ("Id", "m1")));

        Assert.Equal("Boston", projected);
        Assert.NotNull(await IndexDdlAsync("idx_Member_city_col"));
    }

    [Fact]
    public async Task StringPathDdl_IsAvailableOnATransaction()
    {
        await using (var transaction = await _store.BeginTransactionAsync())
        {
            await transaction.CreateIndexAsync<Member>("$.city", "idx_member_city_tx");
            await transaction.CreateCompositeIndexAsync<Member>(["$.city", "$.age"], "idx_member_pair_tx");
            await transaction.AddVirtualColumnAsync<Member>("$.age", "age_col");
            await transaction.CommitAsync();
        }

        Assert.NotNull(await IndexDdlAsync("idx_member_city_tx"));
        Assert.NotNull(await IndexDdlAsync("idx_member_pair_tx"));
    }
}
