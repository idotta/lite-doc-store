using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The JSON path member rule against real SQLite, member by member. The rule has one owner —
/// <c>SqlGenerator.MemberFault</c> — and this file is the check that its verdict is the truth about
/// SQLite rather than a claim about it: every member the rule admits round-trips through a real
/// store under that key, and every member it refuses is refused before any SQL is issued.
///
/// <para>
/// The accept/reject expectation is not restated here. It is read off the rule's owner, so this
/// file cannot drift into a second, disagreeing table — the expectations live once, in the unit
/// project's <c>JsonPathMemberRuleTests.Members</c>. What is duplicated is only the member strings,
/// because the two suites are separate assemblies.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class JsonPathMemberRuleIntegrationTests
{
    private sealed class Bag
    {
        public string Id { get; set; } = "";

        public string Field { get; set; } = "";
    }

    private static readonly string TableName = DefaultTableNamingConvention.Instance.GetTableName<Bag>();

    // Every member the rule admits, and every member it refuses. Which is which is asserted from
    // the rule itself below, never asserted here.
    public static TheoryData<string> Accepted() => Data(Partition(accepted: true));

    public static TheoryData<string> Rejected() => Data(Partition(accepted: false));

    // The rejected members that "$." + member still presents to the path walk as that one member,
    // i.e. everything except the re-tokenized shapes pinned separately below.
    public static TheoryData<string> RejectedSingleMember() => Data(
        Partition(accepted: false)
            .Where(m => !SqlGenerator.MemberNeedsQuoting(m))
            .ToArray());

    // The canonical path addressing one member, built by the renderer that owns it rather than by
    // string concatenation here — "$." + member is only right for a member needing no quotes, which
    // is exactly what the Tier 2 rows are not.
    private static string PathFor(string member)
    {
        var path = new StringBuilder("$.");
        SqlGenerator.AppendCanonicalMember(path, member);
        return path.ToString();
    }

    private static string[] Partition(bool accepted)
    {
        string[] members =
        [
            "Email",
            "full-name",
            "a b",
            "a\nb",
            "a\tb",
            "caf\u00e9",
            "a\U0001F600b",
            "2024",
            "a]b",
            "a'b",
            "a\0b",
            "a[b",
            "a.b",
            "a[0]",
            "",
            "a\"b",
            "\"lead",
            "a.\"b",
            "a.b\"c",
            "a.b\\c"
        ];

        return members
            .Where(m => (SqlGenerator.MemberFault(m) == SqlGenerator.PathMemberFault.None) == accepted)
            .ToArray();
    }

    private static TheoryData<string> Data(string[] members)
    {
        var data = new TheoryData<string>();
        foreach (var member in members)
        {
            data.Add(member);
        }

        return data;
    }

    // --- Accepted members really do work over real SQLite --------------------------------------
    //
    // Not a claim about SQLite but a measurement of it: a newline, a tab, an emoji, an accent, a
    // leading digit, a ']' and a kebab-cased hyphen each store, read back and index under their own
    // key. The rule mirrors SQLite's unquoted path label precisely so this holds.

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AnAcceptedMember_RoundTripsThroughARealStore(string member)
    {
        await using var store = await CreateStoreAsync(member);
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        // The document really carries that key.
        var json = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT json(data) FROM [{TableName}] WHERE id = @Id", ct, ("Id", "b1")));
        using var document = JsonDocument.Parse(json!);
        Assert.True(document.RootElement.TryGetProperty(member, out _));

        // The string-path query reaches it, through whichever spelling the member needs.
        Assert.Equal("b1", Assert.Single(await store.QueryAsync<Bag, string>(PathFor(member), "v")).Id);

        // The patch targets it, and the DDL projects it. Both index names are explicit: the derived
        // name is an identifier, which the widened member rule deliberately is not.
        await store.PatchAsync("b1", DocumentPatch<Bag>.Set(PathFor(member), "v2"));
        Assert.Equal("v2", (await store.GetAsync<Bag>("b1"))!.Field);

        await store.CreateIndexAsync<Bag>(x => x.Field, "idx_member");
        await store.AddVirtualColumnAsync<Bag>(x => x.Field, "vc_member");

        var column = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT [vc_member] FROM [{TableName}] WHERE id = @Id", ct, ("Id", "b1")));
        Assert.Equal("v2", column);
    }

    // --- Rejected members are refused before any statement is prepared -------------------------

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task ARejectedMember_IsRefusedBeforeAnySqlIsIssued(string member)
    {
        await using var store = await CreateStoreAsync(member);

        // The expression route, which is the one that sees the member in isolation, so every
        // rejected shape reaches it — including the ones "$." + member re-tokenizes into something
        // else and the string route therefore never presents as a single member.
        var index = await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateIndexAsync<Bag>(x => x.Field, "idx_rejected"));
        Assert.Equal("jsonPath", index.ParamName);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AddVirtualColumnAsync<Bag>(x => x.Field, "vc_rejected"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DropIndexAsync<Bag>(x => x.Field));

        // Nothing was created, and no provider error leaked: a NUL member would otherwise truncate
        // the statement and surface a raw SqliteException.
        var objects = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name IN ('idx_rejected', 'vc_rejected')", ct));
        Assert.Equal("0", objects);

        var column = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT count(*) FROM pragma_table_xinfo('{TableName}') WHERE name = 'vc_rejected'", ct));
        Assert.Equal("0", column);
    }

    // The string-path route, for every rejected member that "$." + member presents as one member.
    [Theory]
    [MemberData(nameof(RejectedSingleMember))]
    public async Task ARejectedMember_IsAlsoRefusedOnTheStringPathRoute(string member)
    {
        await using var store = await CreateStoreAsync(member);
        var path = $"$.{member}";

        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync<Bag, string>(path, "v"));
        Assert.Throws<ArgumentException>(() => DocumentQuery<Bag>.Where(path, QueryOperator.Equal, "v"));
        Assert.Throws<ArgumentException>(() => DocumentPatch<Bag>.Set(path, "v"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateIndexAsync<Bag>(path, "idx_rejected_string"));

        var objects = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name = 'idx_rejected_string'", ct));
        Assert.Equal("0", objects);
    }

    // --- Tier 2: the quoted form, against real SQLite ------------------------------------------
    //
    // The motivating defect is a silent wrong answer rather than an error, so every assertion here
    // is positive: the document really changed, and it changed under the key the caller meant.

    [Fact]
    public async Task ADottedPath_AddressesTheNestedPathWhileTheQuotedFormAddressesTheKey()
    {
        await using var store = await CreateStoreAsync("a.b");
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        // Measured: json_extract returns NULL for the unquoted spelling, and jsonb_set/jsonb_remove
        // do not fail on it — the set writes a *nested* a -> b instead, the remove does nothing at
        // all, and both report success and bump the version. That is what makes it worth a form of
        // its own rather than a documented limitation.
        Assert.Empty(await store.QueryAsync<Bag, string>("$.a.b", "v"));
        await store.PatchAsync("b1", DocumentPatch<Bag>.Set("$.a.b", "wrong"));
        await store.PatchAsync("b1", DocumentPatch<Bag>.Remove("$.a.b"));
        Assert.Equal("v", (await store.GetAsync<Bag>("b1"))!.Field);

        // The quoted form addresses the key itself, through all three functions.
        Assert.Equal("b1", Assert.Single(await store.QueryAsync<Bag, string>("$.\"a.b\"", "v")).Id);

        await store.PatchAsync("b1", DocumentPatch<Bag>.Set("$.\"a.b\"", "v2"));
        Assert.Equal("v2", (await store.GetAsync<Bag>("b1"))!.Field);

        await store.PatchAsync("b1", DocumentPatch<Bag>.Remove("$.\"a.b\""));
        var json = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT json(data) FROM [{TableName}] WHERE id = @Id", ct, ("Id", "b1")));
        using var document = JsonDocument.Parse(json!);
        Assert.False(document.RootElement.TryGetProperty("a.b", out _));
    }

    // The version refusal, against the engine that does *not* need it. A member with no unquoted
    // spelling that also carries a '"' is refused on every route, although the bundled 3.53.3
    // resolves such a key perfectly — which is the point: on 3.45.1, the floor SqliteVersionGuard
    // enforces, json_extract answers NULL for that path and jsonb_set answers 'bad JSON path', so an
    // index over the key would be vacuous and silent there. Nothing below asserts 3.45.1 behaviour;
    // the last block instead proves the recovery the message names is truthful on this engine.
    [Fact]
    public async Task AQuotedMemberCarryingAQuote_IsRefusedOnEveryTypedRoute()
    {
        const string member = "a.b\"c";
        await using var store = await CreateStoreAsync(member);
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        // The expression route, which sees the serialized name in isolation and blames the member.
        var resolved = await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateIndexAsync<Bag>(x => x.Field, "idx_refused"));
        Assert.Equal("jsonPath", resolved.ParamName);
        Assert.Contains("Bag.Field", resolved.Message, StringComparison.Ordinal);
        Assert.Contains("3.45.x", resolved.Message, StringComparison.Ordinal);

        // The string route, which blames the path. The path it refuses is the one the renderer
        // would have emitted for that member, so the two routes refuse one spelling.
        var path = "$.\"a.b\\\"c\"";
        var written = Assert.Throws<ArgumentException>(
            () => DocumentPatch<Bag>.Set(path, "v2"));
        Assert.Contains("no unquoted spelling", written.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync<Bag, string>(path, "v"));

        // SQL doubling is refused rather than emitted: measured, '$."a""b"' silently resolves the
        // wrong key, so the grammar has no spelling for it at all.
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync<Bag, string>("$.\"a.\"\"b\"", "v"));

        // Nothing was created, and the recovery the message names really does reach the key on this
        // engine: a bound path through ExecuteRawAsync reads it.
        var objects = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name = 'idx_refused'", ct));
        Assert.Equal("0", objects);

        var value = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT json_extract(data, @Path) FROM [{TableName}] WHERE id = @Id",
            ct,
            ("Path", path),
            ("Id", "b1")));
        Assert.Equal("v", value);
    }

    // The control for the refusal above, and a measurement rather than a judgement: a '\' inside a
    // quoted member is *not* refused, because it resolves on 3.45.1 as well as on 3.53.3 through
    // json_extract and jsonb_set alike. The round-trip itself runs in the Accepted theory; what is
    // pinned here is that the renderer still emits the escaped form and that SQLite reads it as the
    // one key rather than as two path segments.
    [Fact]
    public async Task AQuotedMemberCarryingABackslash_IsAcceptedAndAddressesItsOwnKey()
    {
        const string member = "a.b\\c";
        await using var store = await CreateStoreAsync(member);
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        Assert.Equal("$.\"a.b\\\\c\"", PathFor(member));
        Assert.Equal("b1", Assert.Single(await store.QueryAsync<Bag, string>(PathFor(member), "v")).Id);

        await store.PatchAsync("b1", DocumentPatch<Bag>.Set(PathFor(member), "v2"));
        Assert.Equal("v2", (await store.GetAsync<Bag>("b1"))!.Field);

        // The unquoted spelling addresses a nested path instead, which is why the key needs quotes.
        Assert.Empty(await store.QueryAsync<Bag, string>("$.a.b\\c", "v2"));
    }

    // The whole reason the path is interpolated rather than bound: SQLite matches an expression
    // index only when the indexed expression appears literally, so a Tier 2 path has to reach the
    // index through exactly the text CreateIndexAsync wrote. The expression route and the string
    // route are both exercised, because they are two renderers that must agree.
    [Fact]
    public async Task AnExpressionIndexOverATier2Path_IsUsed()
    {
        await using var store = await CreateStoreAsync("a.b");
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        await store.CreateIndexAsync<Bag>(x => x.Field, "idx_tier2");

        // EXPLAIN QUERY PLAN yields a 'detail' column per step; read it by ordinal.
        var plan = await store.ExecuteRawAsync(async (connection, _) =>
        {
            var details = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"EXPLAIN QUERY PLAN SELECT json(data) FROM [{TableName}] " +
                "WHERE json_extract(data, '$.\"a.b\"') = 'v'";
            await using var reader = await command.ExecuteReaderAsync();
            var detail = reader.GetOrdinal("detail");
            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(detail));
            }

            return string.Join(" ", details);
        });

        Assert.Contains("USING INDEX idx_tier2", plan, StringComparison.Ordinal);
        Assert.Equal("b1", Assert.Single(await store.QueryAsync<Bag, string>("$.\"a.b\"", "v")).Id);
    }

    // The same agreement end to end, through the spelling that is *not* canonical: an index created
    // over '$."Name"' has to be matched by a query written '$.Name', because both canonicalize to
    // one text. A generator interpolating its raw argument instead would emit the two spellings and
    // SQLite would quietly plan a table scan.
    [Fact]
    public async Task AnIndexCreatedThroughANonCanonicalSpelling_IsStillUsed()
    {
        await using var store = await CreateStoreAsync("Name");
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });

        await store.CreateIndexAsync<Bag>("$.\"Name\"", "idx_canonical");

        var plan = await store.ExecuteRawAsync(async (connection, _) =>
        {
            var details = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"EXPLAIN QUERY PLAN SELECT json(data) FROM [{TableName}] " +
                "WHERE json_extract(data, '$.Name') = 'v'";
            await using var reader = await command.ExecuteReaderAsync();
            var detail = reader.GetOrdinal("detail");
            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(detail));
            }

            return string.Join(" ", details);
        });

        Assert.Contains("USING INDEX idx_canonical", plan, StringComparison.Ordinal);

        var sql = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE name = 'idx_canonical'", ct));
        Assert.Contains("'$.Name'", sql!, StringComparison.Ordinal);
    }

    // Decision, shipped: a Tier 2 path has no derivable index name, so it needs an explicit one —
    // the same answer a widened member like "$.full-name" already gets, and for the same reason.
    // Making the derivation injective is a separate job.
    [Theory]
    [InlineData("$.\"a.b\"")]
    [InlineData("$.\"a[b\"")]
    [InlineData("$.\"\"")]
    public async Task ATier2Path_HasNoDerivableIndexName(string path)
    {
        await using var store = await CreateStoreAsync("a.b");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateIndexAsync<Bag>(path));

        Assert.Equal("jsonPath", exception.ParamName);
        Assert.Contains("explicit index name", exception.Message, StringComparison.Ordinal);

        // Blamed on the quoted member, not on an array indexer: '$."a[b"' carries a '[' that is no
        // indexer at all, and the older check would have mis-diagnosed it.
        Assert.Contains("quoted member name", exception.Message, StringComparison.Ordinal);

        // And with one, it is created.
        await store.CreateIndexAsync<Bag>(path, "idx_explicit");
        var created = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name = 'idx_explicit'", ct));
        Assert.Equal("1", created);
    }

    // --- A store whose serialized name is the member under test --------------------------------
    //
    // A resolver modifier rather than a [JsonPropertyName] per member, so the member list above
    // stays the only place a member string is written. It sets the same JsonPropertyInfo.Name the
    // attribute would, which is the name JsonHelper serializes through and JsonPathResolver reads.

    private static async Task<IDocumentStore> CreateStoreAsync(string serializedName)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.SerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Type != typeof(Bag))
                        {
                            return;
                        }

                        foreach (var property in info.Properties)
                        {
                            if ((property.AttributeProvider as MemberInfo)?.Name == nameof(Bag.Field))
                            {
                                property.Name = serializedName;
                            }
                        }
                    }
                }
            }
        };

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Bag>();
        return store;
    }
}
