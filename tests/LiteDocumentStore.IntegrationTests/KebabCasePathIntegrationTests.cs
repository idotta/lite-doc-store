using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// A store configured with the BCL's own <see cref="JsonNamingPolicy.KebabCaseLower" />, against
/// real SQLite. <c>ConvertName("FullName")</c> is <c>full-name</c>, and the old identifier-shaped
/// member rule rejected that path in every typed API — query, index, patch, virtual column — so
/// the store was unusable through its own surface under a stock policy. (<c>SnakeCaseLower</c>
/// gives <c>full_name</c> and was never affected.)
///
/// The member rule now mirrors SQLite's unquoted path label: one or more characters, none of which
/// is an apostrophe, a <c>.</c> or a <c>[</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KebabCasePathIntegrationTests : IAsyncLifetime
{
    private sealed class Person
    {
        public string Id { get; set; } = "";

        public string FullName { get; set; } = "";

        public int Age { get; set; }
    }

    private static readonly string TableName = DefaultTableNamingConvention.Instance.GetTableName<Person>();

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.SerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower
        };

        _store = await new DocumentStoreFactory().CreateAsync(options);
        await _store.CreateTableAsync<Person>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    // --- The policy really does produce a path the old rule refused ----------------------------

    [Fact]
    public void KebabCaseLower_TurnsFullNameIntoAHyphenatedKey()
    {
        Assert.Equal("full-name", JsonNamingPolicy.KebabCaseLower.ConvertName("FullName"));
        Assert.Equal("full_name", JsonNamingPolicy.SnakeCaseLower.ConvertName("FullName"));
    }

    [Fact]
    public async Task TheStoredDocument_CarriesTheHyphenatedKey()
    {
        await _store.UpsertAsync("p1", new Person { Id = "p1", FullName = "Ada", Age = 36 });

        var json = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT json(data) FROM [{TableName}] WHERE id = @Id", ct, ("Id", "p1")));

        Assert.Contains("\"full-name\":\"Ada\"", json, StringComparison.Ordinal);
    }

    // --- Every typed path API now works over it ------------------------------------------------

    [Fact]
    public async Task EveryTypedPathApi_WorksOverAKebabCasedKey()
    {
        await _store.UpsertAsync("p1", new Person { Id = "p1", FullName = "Ada", Age = 36 });
        await _store.UpsertAsync("p2", new Person { Id = "p2", FullName = "Grace", Age = 45 });

        // The string-path query overload.
        var byPath = await _store.QueryAsync<Person, string>("$.full-name", "Ada");
        Assert.Equal("p1", Assert.Single(byPath).Id);

        // The composable builder, including an ordering over the same path.
        var built = await _store.QueryAsync(
            DocumentQuery<Person>.Where("$.full-name", QueryOperator.Like, "%a%")
                                 .OrderBy("$.full-name"));
        Assert.Equal(["Ada", "Grace"], built.Select(p => p.FullName));
        Assert.Equal(2, await _store.CountAsync(DocumentQuery<Person>.Where("$.full-name", QueryOperator.Like, "%a%")));
        Assert.True(await _store.ExistsAsync(DocumentQuery<Person>.Where("$.full-name", QueryOperator.Equal, "Ada")));

        // A patch: one statement, one version bump, jsonb_set over the hyphenated path.
        await _store.PatchAsync("p1", DocumentPatch<Person>.Set("$.full-name", "Ada L"));
        Assert.Equal("Ada L", (await _store.GetAsync<Person>("p1"))!.FullName);

        // The DDL, both from an expression and from the string path.
        await _store.CreateIndexAsync<Person>(x => x.FullName, "idx_full_name");
        await _store.CreateIndexAsync<Person>("$.full-name", "idx_full_name_string");
        await _store.CreateCompositeIndexAsync<Person>(["$.full-name", "$.age"], "idx_full_name_age");
        await _store.AddVirtualColumnAsync<Person>(x => x.FullName, "vc_full_name");

        var column = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT [vc_full_name] FROM [{TableName}] WHERE id = @Id", ct, ("Id", "p1")));
        Assert.Equal("Ada L", column);
    }

    // The whole point of widening rather than rewriting: SQLite matches a query against an
    // expression index only when the indexed expression appears literally, so a path that
    // validates but defeats the index would be a silent regression, not a visible one.
    [Fact]
    public async Task AnIndexOverAWidenedPath_IsStillUsedByTheQuery()
    {
        await _store.UpsertAsync("p1", new Person { Id = "p1", FullName = "Ada", Age = 36 });
        await _store.CreateIndexAsync<Person>(x => x.FullName, "idx_full_name");

        var plan = await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"EXPLAIN QUERY PLAN SELECT json(data) FROM [{TableName}] " +
                "WHERE json_extract(data, '$.full-name') = 'Ada'";

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(3));
            }

            return string.Join(" | ", rows);
        });

        Assert.Contains("USING INDEX idx_full_name", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN", plan, StringComparison.Ordinal);
    }

    // --- The apostrophe stays the injection boundary -------------------------------------------

    [Fact]
    public async Task AnApostropheInAPath_IsStillRefused_OnEveryTypedApi()
    {
        const string Injected = "$.full-name') = 'Ada' OR 1=1 --";

        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.QueryAsync<Person, string>(Injected, "x"));
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<Person>.Where(Injected, QueryOperator.Equal, "x"));
        Assert.Throws<ArgumentException>(
            () => DocumentPatch<Person>.Set(Injected, "x"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Person>(Injected, "idx_injected"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Person>(Injected, "vc_injected"));

        // And nothing was created by any of them.
        var indexes = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name LIKE '%injected%'", ct));
        Assert.Equal("0", indexes);
    }

    // --- Auto-naming is refused against the path, not the derived name -------------------------
    //
    // The path grammar is wider than a SQL identifier, so "$.full-name" derives "idx_T_full-name",
    // which ValidateIdentifier rejects — against an indexName the caller never passed. That is the
    // mis-attribution RequireDerivableName exists to prevent, and widening the grammar reopened it.

    [Fact]
    public async Task AutoNamingAKebabCasedPath_IsRefusedAgainstThePath()
    {
        var single = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Person>(x => x.FullName));
        Assert.Equal("jsonPath", single.ParamName);
        Assert.Contains("$.full-name", single.Message, StringComparison.Ordinal);
        Assert.Contains("explicit index name", single.Message, StringComparison.Ordinal);

        var composite = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Person>(["$.full-name", "$.age"]));
        Assert.Equal("jsonPaths", composite.ParamName);
        Assert.Contains("$.full-name", composite.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitIndexName_IsAcceptedForTheSamePath()
    {
        await _store.CreateIndexAsync<Person>(x => x.FullName, "idx_full_name");
        await _store.CreateCompositeIndexAsync<Person>(["$.full-name", "$.age"], "idx_full_name_age");

        var count = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN " +
            "('idx_full_name', 'idx_full_name_age')",
            ct));
        Assert.Equal("2", count);
    }

    // An identifier-shaped path still auto-names, so the guard did not widen into a refusal of the
    // ordinary case.
    [Fact]
    public async Task AnIdentifierShapedPath_StillAutoNames()
    {
        await _store.CreateIndexAsync<Person>(x => x.Age);

        var sql = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", $"idx_{TableName}_age")));
        Assert.Contains("json_extract(data, '$.age')", sql, StringComparison.Ordinal);
    }

    // The fourth derivation site: DropIndexAsync<T>(expression) derives the same name and had no
    // screen at all, so it reported a kebab-cased path against an indexName the caller never
    // passed. It has no explicit-name escape of its own — the string overload is that — so the
    // refusal is reported against the expression parameter.
    [Fact]
    public async Task DroppingAKebabCasedPathByExpression_IsRefusedAgainstTheExpression()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.DropIndexAsync<Person>(x => x.FullName));

        Assert.Equal("expression", ex.ParamName);
        Assert.Contains("$.full-name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppingAnIdentifierShapedPathByExpression_StillWorks()
    {
        await _store.CreateIndexAsync<Person>(x => x.Age);
        await _store.DropIndexAsync<Person>(x => x.Age);

        var count = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", $"idx_{TableName}_age")));
        Assert.Equal("0", count);
    }
}
