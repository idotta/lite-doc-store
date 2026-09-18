using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// U+0000 in a JSON path member, against real SQLite. Widening the member rule to SQLite's own
/// unquoted label (C18 Tier 1) admitted a NUL, which the old identifier-shaped rule had rejected.
///
/// <para>
/// <c>sqlite3_prepare</c> reads a NUL-terminated string, so a NUL in an interpolated path truncates
/// the whole statement at that byte. The path always sits immediately after an opening apostrophe,
/// so the truncated prefix always ends inside an unterminated literal and SQLite always answers
/// <c>SQLITE_ERROR: unrecognized token</c> — measured across every generator that interpolates a
/// path. The failure is therefore loud, not a wrong result, but it is a raw
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/> leaked from a typed API, carrying a
/// truncated and misleading message, for an argument the validator should refuse up front.
/// </para>
///
/// <para>
/// What this test pins is the exception <em>type</em>: no provider error may reach the caller.
/// It is not a C18 Tier 2 shape — quoting cannot rescue a NUL, measured both interpolated
/// (<c>unrecognized token</c>) and bound (<c>bad JSON path</c>).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class NulPathIntegrationTests : IAsyncLifetime
{
    private const string NulPath = "$.Items.a\0b";

    private sealed class Bag
    {
        public string Id { get; set; } = "";

        [JsonPropertyName("nul\0name")]
        public string NulName { get; set; } = "";

        [JsonPropertyName("line\nbreak")]
        public string LineBreak { get; set; } = "";

        [JsonPropertyName("tab\tbed")]
        public string Tabbed { get; set; } = "";
    }

    private static readonly string TableName = DefaultTableNamingConvention.Instance.GetTableName<Bag>();

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.SerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        _store = await new DocumentStoreFactory().CreateAsync(options);
        await _store.CreateTableAsync<Bag>();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    // --- The string-path route -----------------------------------------------------------------

    [Fact]
    public async Task ANulInAStringPath_ThrowsArgumentException_NotSqliteException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.QueryAsync<Bag, string>(NulPath, "x"));
        Assert.Throws<ArgumentException>(
            () => DocumentQuery<Bag>.Where(NulPath, QueryOperator.Equal, "x"));
        Assert.Throws<ArgumentException>(
            () => DocumentPatch<Bag>.Set(NulPath, "x"));
        Assert.Throws<ArgumentException>(
            () => DocumentPatch<Bag>.Remove(NulPath));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Bag>(NulPath, "idx_nul"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateCompositeIndexAsync<Bag>(["$.Id", NulPath], "idx_nul_composite"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Bag>(NulPath, "vc_nul"));

        // And nothing was created by any of them. The two indexes are schema objects, so
        // sqlite_master names them — but a generated column is not, so sqlite_master can never
        // hold 'vc_nul' whether or not AddVirtualColumnAsync added it (measured: 0 rows for a
        // column that exists). The column needs its own probe, or the virtual-column half of this
        // assertion tests nothing. It should genuinely be absent: AddVirtualColumnAsync preflights
        // the index definition ahead of the ALTER precisely so a refusal leaves nothing behind.
        var createdObjects = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT count(*) FROM sqlite_master WHERE name IN " +
            "('idx_nul', 'idx_nul_composite', 'vc_nul')", ct));
        Assert.Equal("0", createdObjects);

        var createdColumn = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT count(*) FROM pragma_table_xinfo('{TableName}') WHERE name = 'vc_nul'", ct));
        Assert.Equal("0", createdColumn);
    }

    // An explicit index name is the shape that used to slip past: auto-naming already refused a NUL
    // path by accident, because IsValidIdentifier rejects the derived name — but it blamed the name
    // derivation, and naming the index explicitly reached the SQL error instead.
    [Fact]
    public async Task ANulPathWithAnExplicitIndexName_IsRefusedAgainstThePath()
    {
        var explicitly = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Bag>(NulPath, "idx_explicit_nul"));
        Assert.Equal("jsonPath", explicitly.ParamName);
        Assert.Contains("U+0000", explicitly.Message, StringComparison.Ordinal);

        var auto = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Bag>(NulPath));
        Assert.Equal("jsonPath", auto.ParamName);
        Assert.Contains("U+0000", auto.Message, StringComparison.Ordinal);
    }

    // --- The expression route ------------------------------------------------------------------

    [Fact]
    public async Task ANulInASerializedName_ThrowsArgumentException_NamingTheMember()
    {
        var index = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateIndexAsync<Bag>(x => x.NulName, "idx_expr_nul"));
        Assert.Contains("NulName", index.Message, StringComparison.Ordinal);
        Assert.Contains("U+0000", index.Message, StringComparison.Ordinal);

        var column = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.AddVirtualColumnAsync<Bag>(x => x.NulName, "vc_expr_nul"));
        Assert.Contains("U+0000", column.Message, StringComparison.Ordinal);

        var drop = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.DropIndexAsync<Bag>(x => x.NulName));
        Assert.Contains("U+0000", drop.Message, StringComparison.Ordinal);
    }

    // --- Positive control: a newline and a tab stay accepted -----------------------------------
    //
    // Neither terminates the SQL string, and measured, each resolves to its own key. A later
    // "tighten the rule" edit must not quietly take them out with the NUL.

    [Fact]
    public async Task ANewlineAndATabInAPath_StayAccepted_AndResolveTheirOwnKey()
    {
        await _store.UpsertAsync("b1", new Bag { Id = "b1", LineBreak = "L", Tabbed = "T" });

        Assert.Equal("b1", Assert.Single(await _store.QueryAsync<Bag, string>("$.line\nbreak", "L")).Id);
        Assert.Equal("b1", Assert.Single(await _store.QueryAsync<Bag, string>("$.tab\tbed", "T")).Id);

        await _store.PatchAsync("b1", DocumentPatch<Bag>.Set("$.line\nbreak", "L2"));
        Assert.Equal("L2", (await _store.GetAsync<Bag>("b1"))!.LineBreak);

        await _store.CreateIndexAsync<Bag>(x => x.LineBreak, "idx_linebreak");
        await _store.AddVirtualColumnAsync<Bag>(x => x.Tabbed, "vc_tabbed");

        var column = await _store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            $"SELECT [vc_tabbed] FROM [{TableName}] WHERE id = @Id", ct, ("Id", "b1")));
        Assert.Equal("T", column);
    }
}
