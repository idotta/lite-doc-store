using System.Reflection;
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
    // i.e. everything except the three re-tokenized shapes pinned separately below.
    public static TheoryData<string> RejectedSingleMember() => Data(
        Partition(accepted: false)
            .Where(m => !m.Contains('.', StringComparison.Ordinal)
                     && !m.Contains('[', StringComparison.Ordinal))
            .ToArray());

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
            ""
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

        // The string-path query reaches it. No accepted member carries a '.' or a '[', so
        // "$." + member is the single member itself.
        Assert.Equal("b1", Assert.Single(await store.QueryAsync<Bag, string>($"$.{member}", "v")).Id);

        // The patch targets it, and the DDL projects it. Both index names are explicit: the derived
        // name is an identifier, which the widened member rule deliberately is not.
        await store.PatchAsync("b1", DocumentPatch<Bag>.Set($"$.{member}", "v2"));
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

        // The expression route, which is the one that sees the member in isolation: all five
        // rejected shapes reach it, including the '.' and the '[' the path walk can never observe.
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

    // The asymmetry, stated rather than dropped. Three members are refused by the member rule and
    // yet "$." + member is text the path walk reads as something else entirely, because '.' and '['
    // are member *delimiters* there and illegal *characters* to the resolver. Two of the three are
    // therefore valid paths — they simply address a different key than the caller meant, which is
    // why reaching the real key needs the $."quoted" form (C18 Tier 2, unimplemented).
    [Theory]
    [InlineData("a.b", "$.a.b", true)]      // the two-member path a -> b
    [InlineData("a[0]", "$.a[0]", true)]    // the member a, then an indexer
    [InlineData("a[b", "$.a[b", false)]     // an indexer that is not decimal: rejected, not re-read
    public async Task AReTokenizedMember_IsRefusedAsAMemberButThePathMeansSomethingElse(
        string member,
        string path,
        bool pathIsValid)
    {
        Assert.NotEqual(SqlGenerator.PathMemberFault.None, SqlGenerator.MemberFault(member));

        await using var store = await CreateStoreAsync(member);

        // As a serialized name it is refused, on every expression entry point.
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateIndexAsync<Bag>(x => x.Field, "idx_retokenized"));

        if (!pathIsValid)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.QueryAsync<Bag, string>(path, "v"));
            return;
        }

        // As a path it is accepted and runs against real SQLite — and matches nothing, because no
        // document carries the key the caller meant.
        await store.UpsertAsync("b1", new Bag { Id = "b1", Field = "v" });
        Assert.Empty(await store.QueryAsync<Bag, string>(path, "v"));
        await store.CreateIndexAsync<Bag>(path, "idx_retokenized_string");

        var created = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name = 'idx_retokenized_string'", ct));
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
