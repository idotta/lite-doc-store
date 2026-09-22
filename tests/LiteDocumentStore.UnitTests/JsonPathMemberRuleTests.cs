using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The JSON path <em>member</em> rule, which has exactly one owner — <c>SqlGenerator.MemberFault</c>
/// for what a member may contain, and <c>MemberNeedsQuoting</c>/<c>AppendCanonicalMember</c> beside
/// it for how one is written — and two entry points that word its refusal differently on purpose:
/// <c>SqlGenerator.ValidateJsonPath</c>, which tokenizes each member out of a whole path and blames
/// the path parameter, and <c>JsonPathResolver</c>, which is handed one already-resolved serialized
/// name and blames the member that produced it.
///
/// <para>
/// This file is the pin on that consolidation. <see cref="Members" /> is the single table of member
/// strings; every assertion below is driven from it, so the two entry points cannot drift into
/// disagreeing about what a member may contain <em>or about how it is rendered</em> — which is the
/// stronger of the two, because an index created over one rendering and queried through another is
/// silently never used. The message-preservation tests below pin the two wordings byte for byte, so
/// a future consolidation of the <em>messages</em> fails the build: the reporting split is
/// deliberate and survives.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class JsonPathMemberRuleTests
{
    /// <param name="Member">The isolated member string under test.</param>
    /// <param name="Rendered">
    /// The canonical path addressing that one member, which both entry points must produce, or null
    /// when the member rule refuses it outright. U+0000 and an apostrophe are refused wherever they
    /// appear; a '"' is refused only in a member that has no unquoted spelling, because the minimum
    /// supported SQLite does not unescape one inside the quotes. Every other member is rendered,
    /// quoted when it cannot be written unquoted.
    /// </param>
    /// <param name="AcceptedAsThePath">
    /// What <c>ValidateJsonPath</c> makes of the <em>text</em> <c>"$." + Member</c>. It says the
    /// member is renderable except on the rows where that text is re-tokenized into something else,
    /// which is what <paramref name="Divergence" /> records.
    /// </param>
    /// <param name="Divergence">
    /// Empty when <c>"$." + Member</c> really is the single member <paramref name="Member" />;
    /// otherwise why that text means something else. A '.' and a '[' are member <em>delimiters</em>
    /// in a path, a leading '"' opens a quoted member, and the empty member has no unquoted
    /// spelling at all — which is exactly why those members need the quoted form.
    /// </param>
    public sealed record MemberCase(
        string Member,
        string? Rendered,
        bool AcceptedAsThePath,
        string Divergence);

    /// <summary>
    /// The one source of truth. Both entry points are driven through every row.
    /// </summary>
    public static readonly MemberCase[] Members =
    [
        // --- Tier 1: written unquoted, mirroring SQLite's own unquoted path label ---------------
        new("Email", "$.Email", true, ""),
        new("full-name", "$.full-name", true, ""),   // JsonNamingPolicy.KebabCaseLower
        new("a b", "$.a b", true, ""),               // a space
        new("a\nb", "$.a\nb", true, ""),             // a newline — measured, reads back its own key
        new("a\tb", "$.a\tb", true, ""),             // a tab — likewise
        new("café", "$.café", true, ""),        // accented
        new("a\U0001F600b", "$.a\U0001F600b", true, ""),  // an emoji, outside the BMP
        new("2024", "$.2024", true, ""),             // leading digit
        new("a]b", "$.a]b", true, ""),               // ']' is not structural in a path
        new("a\"b", "$.a\"b", true, ""),             // a '"' that is not first is an ordinary char

        // --- Refused outright, and the two entry points agree ----------------------------------
        new("a'b", null, false, ""),                 // the injection boundary
        new("a\0b", null, false, ""),                // truncates the statement; permanently refused

        // --- Tier 2: renderable only as $."quoted" ---------------------------------------------
        new(
            "a.b",
            "$.\"a.b\"",
            true,
            "The path walk reads \"$.a.b\" as the two-member path a -> b, never the single key " +
            "\"a.b\", which is why that key needs the quoted form."),
        new(
            "a[0]",
            "$.\"a[0]\"",
            true,
            "The path walk reads \"$.a[0]\" as the member a followed by an indexer."),
        new(
            "a[b",
            "$.\"a[b\"",
            false,
            "The path walk reads \"$.a[b\" as the member a followed by an indexer that is not " +
            "decimal, and refuses it."),
        new(
            "",
            "$.\"\"",
            false,
            "The path walk refuses \"$.\" as a '.' with nothing after it: the empty key has no " +
            "unquoted spelling, so it must be written '$.\"\"'."),
        new(
            "\"lead",
            null,
            false,
            "A '\"' straight after the '.' opens a quoted member, so \"$.\\\"lead\" is an " +
            "unterminated one — measured, SQLite answers 'bad JSON path' for it too."),
        new(
            "a.\"b",
            null,
            false,
            "The path walk reads \"$.a.\\\"b\" as the member a followed by an unterminated quoted " +
            "member."),

        // --- Refused because the quoted form is the only spelling and cannot carry a '"' -------
        //
        // Measured on 3.45.1 (the floor SqliteVersionGuard enforces): json_extract answers NULL for
        // '$."a.b\"c"' and jsonb_set answers 'bad JSON path', while the bundled 3.53.3 resolves
        // both. An index over such a key would be vacuous on the declared minimum, silently, so the
        // library refuses it on every engine. The backslash row beside it is the control: measured,
        // '$."a.b\\c"' reads and writes its own key on *both* builds, so escapes are not refused in
        // general — only the '"'.
        new(
            "a.b\"c",
            null,
            true,
            "The path walk reads \"$.a.b\\\"c\" as the two-member path a -> b\\\"c, never the " +
            "single key."),
        new(
            "a.b\\c",
            "$.\"a.b\\\\c\"",
            true,
            "The path walk reads \"$.a.b\\\\c\" as the two-member path a -> b\\\\c, never the " +
            "single key.")
    ];

    public static TheoryData<string> AllMembers()
    {
        var data = new TheoryData<string>();
        foreach (var member in Members)
        {
            data.Add(member.Member);
        }

        return data;
    }

    private static MemberCase Case(string member) =>
        Members.Single(m => string.Equals(m.Member, member, StringComparison.Ordinal));

    // --- The table's own consistency -----------------------------------------------------------

    // A row that claims no divergence must not carry one, and a row that claims one must be one of
    // the shapes the asymmetry is known to cover. Without this, a later row could opt itself out of
    // the agreement assertion just by filling in a Divergence string.
    [Fact]
    public void TheMemberTable_RecordsDivergenceOnlyWhereThePathWalkReTokenizes()
    {
        foreach (var member in Members)
        {
            if (member.Divergence.Length == 0)
            {
                Assert.Equal(member.Rendered is not null, member.AcceptedAsThePath);
            }
            else
            {
                Assert.True(
                    member.Member.Length == 0
                        || member.Member[0] == '"'
                        || member.Member.Contains('.', StringComparison.Ordinal)
                        || member.Member.Contains('[', StringComparison.Ordinal),
                    $"'{member.Member}' claims a divergence but has no unquoted spelling problem.");
            }
        }

        Assert.Equal(8, Members.Count(m => m.Divergence.Length > 0));
    }

    // Every divergent row is a Tier 2 row and vice versa: the members needing the quoted form are
    // exactly the ones "$." + member cannot present as themselves.
    [Fact]
    public void TheMemberTable_QuotesExactlyTheMembersWithNoUnquotedSpelling()
    {
        foreach (var member in Members.Where(m => m.Rendered is not null))
        {
            Assert.Equal(
                SqlGenerator.MemberNeedsQuoting(member.Member),
                member.Divergence.Length > 0);
        }
    }

    // --- Both entry points, one table ----------------------------------------------------------

    /// <summary>
    /// The resolver entry point: a type whose serialized name is the member under test, resolved
    /// through the same <see cref="JsonTypeInfo" /> metadata the store serializes through.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMembers))]
    public void JsonPathResolver_AppliesTheMemberRule(string member)
    {
        var expected = Case(member);

        if (expected.Rendered is not null)
        {
            Assert.Equal(expected.Rendered, ResolveWithSerializedName(member));
            return;
        }

        Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));
    }

    /// <summary>
    /// The path entry point, over the same table. On the divergent rows this asserts the recorded
    /// path verdict rather than the member verdict — that difference is the path grammar speaking,
    /// not the member rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllMembers))]
    public void ValidateJsonPath_AppliesTheMemberRule(string member)
    {
        var expected = Case(member);
        var path = $"$.{member}";

        if (expected.AcceptedAsThePath)
        {
            Assert.Equal(path, SqlGenerator.ValidateJsonPath(path, "jsonPath"));
            return;
        }

        Assert.Throws<ArgumentException>(() => SqlGenerator.ValidateJsonPath(path, "jsonPath"));
    }

    // The agreement itself, stated once rather than inferred from the two theories passing: on
    // every row the path walk can see as a single member, the two entry points return the same
    // accept/reject verdict.
    [Fact]
    public void BothEntryPoints_AgreeOnEveryMemberThePathWalkSeesAsOne()
    {
        foreach (var member in Members.Where(m => m.Divergence.Length == 0))
        {
            var resolverAccepts = Accepts(() => ResolveWithSerializedName(member.Member));
            var pathAccepts = Accepts(
                () => SqlGenerator.ValidateJsonPath($"$.{member.Member}", "jsonPath"));

            Assert.Equal(member.Rendered is not null, resolverAccepts);
            Assert.Equal(resolverAccepts, pathAccepts);
        }
    }

    // The rendering agreement, which the accept/reject one cannot reach: what the resolver emits
    // for a member and what the path walk makes of that same text must be one string. An index
    // created over one spelling and queried through another is never used, silently — the whole
    // reason the path is interpolated rather than bound.
    [Theory]
    [MemberData(nameof(AllMembers))]
    public void BothEntryPoints_RenderEveryMemberIdentically(string member)
    {
        var expected = Case(member);
        if (expected.Rendered is null)
        {
            return;
        }

        Assert.Equal(expected.Rendered, ResolveWithSerializedName(member));
        Assert.Equal(
            expected.Rendered,
            SqlGenerator.ValidateJsonPath(expected.Rendered, "jsonPath"));
    }

    // --- Canonical rendering: idempotence, and Tier 1 byte-identity -----------------------------

    // The generators validate a path that DocumentQuery and DocumentPatch have already validated,
    // so a rendering that moved on a second pass would emit one path at build time and another at
    // generation time.
    [Theory]
    [InlineData("$.Name", "$.Name")]
    [InlineData("$.a.b", "$.a.b")]                      // two members, not the key "a.b"
    [InlineData("$.Tags[0]", "$.Tags[0]")]
    [InlineData("$", "$")]
    [InlineData("$.a\"b", "$.a\"b")]                    // a '"' that is not first stays unquoted
    [InlineData("$.\"a.b\"", "$.\"a.b\"")]
    [InlineData("$.\"a[0]\"", "$.\"a[0]\"")]
    [InlineData("$.\"\"", "$.\"\"")]
    [InlineData("$.\"a.b\".\"c.d\"", "$.\"a.b\".\"c.d\"")]
    [InlineData("$.\"a.b\"[1].x", "$.\"a.b\"[1].x")]
    [InlineData("$.\"Name\"", "$.Name")]                // quotes dropped where they buy nothing
    [InlineData("$.\"a\\\"b\"", "$.a\"b")]              // likewise, and the escape is decoded
    [InlineData("$.\"a.\\\\b\"", "$.\"a.\\\\b\"")]      // kept, because the '.' still needs them
    [InlineData("$.\"a.b\\\\c\"", "$.\"a.b\\\\c\"")]
    public void ValidateJsonPath_CanonicalizesAndIsIdempotent(string input, string canonical)
    {
        var once = SqlGenerator.ValidateJsonPath(input, "jsonPath");
        Assert.Equal(canonical, once);
        Assert.Equal(canonical, SqlGenerator.ValidateJsonPath(once, "jsonPath"));
    }

    // Tier 1 is not merely equal but the same instance: a default-configured store allocates
    // nothing here and sees byte-identical SQL to what it saw before Tier 2 existed.
    [Theory]
    [InlineData("$.Email")]
    [InlineData("$.Address.City")]
    [InlineData("$.Tags[0]")]
    [InlineData("$")]
    public void ValidateJsonPath_ReturnsTheSameInstanceForATier1Path(string path)
    {
        Assert.Same(path, SqlGenerator.ValidateJsonPath(path, "jsonPath"));
    }

    // The root split the tokenizer has to preserve exactly: the bare root is refused by the
    // projecting DDL and the patch targets, while an indexer at the root and the empty *key* are
    // keys below it and stay legal.
    [Theory]
    [InlineData("$[0]")]
    [InlineData("$.\"\"")]
    [InlineData("$.\"\".x")]
    public void ValidateJsonPath_WithAllowRootFalse_AcceptsEveryPathBelowTheRoot(string path)
    {
        Assert.Equal(path, SqlGenerator.ValidateJsonPath(path, "jsonPath", allowRoot: false));
    }

    // --- Message preservation: the reporting split is deliberate and must survive ---------------
    //
    // SqlGenerator blames the path parameter and quotes the whole path; JsonPathResolver blames the
    // member that produced the name and varies its recovery sentence by reason. Consolidating these
    // messages would be a regression, so each is pinned byte for byte.

    [Fact]
    public void ValidateJsonPath_WithAnApostropheMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.a'b", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.a'b': a member name cannot contain an apostrophe, " +
            "which would close the SQL literal the path is written into. (Parameter 'jsonPath')",
            ex.Message);
    }

    // Reached through the quoted form too, which is the arm that would otherwise let an apostrophe
    // past by spelling it differently.
    [Fact]
    public void ValidateJsonPath_WithAnApostropheInsideAQuotedMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.\"a.b'c\"", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.\"a.b'c\"': a member name cannot contain an apostrophe, " +
            "which would close the SQL literal the path is written into. (Parameter 'jsonPath')",
            ex.Message);
    }

    [Fact]
    public void ValidateJsonPath_WithANulMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.a\0b", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.a\0b': a member name cannot contain U+0000, which " +
            "truncates the SQL statement the path is written into. No quoting form can " +
            "address such a key. (Parameter 'jsonPath')",
            ex.Message);
    }

    // U+0000 is not a Tier 2 shape: quoting does not rescue it, so the quoted form is refused with
    // the same permanent message rather than accepted.
    [Fact]
    public void ValidateJsonPath_WithANulInsideAQuotedMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.\"a\0b\"", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.\"a\0b\"': a member name cannot contain U+0000, which " +
            "truncates the SQL statement the path is written into. No quoting form can " +
            "address such a key. (Parameter 'jsonPath')",
            ex.Message);
    }

    [Fact]
    public void ValidateJsonPath_WithAnEmptyUnquotedMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.': a '.' must be followed by a member name of one " +
            "or more characters, or by a double-quoted member name. The empty key is " +
            "written '$.\"\"'. (Parameter 'jsonPath')",
            ex.Message);
    }

    [Fact]
    public void ValidateJsonPath_WithAnUnterminatedQuotedMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.\"a.b", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.\"a.b': a quoted member name must be closed by an unescaped '\"'. " +
            "(Parameter 'jsonPath')",
            ex.Message);
    }

    // An escape this renderer never emits is refused rather than guessed at: SQLite unescapes a
    // quoted path label JSON-style, so reading "\b" as a backslash and a 'b' would address a
    // different key than SQLite does, silently.
    [Fact]
    public void ValidateJsonPath_WithAnUnsupportedEscape_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.\"a.\\b\"", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.\"a.\\b\"': a quoted member name supports only the '\\\"' and " +
            "'\\\\' escapes. (Parameter 'jsonPath')",
            ex.Message);
    }

    // The version refusal. A member with no unquoted spelling cannot also carry a '"': the
    // minimum supported SQLite does not unescape a quoted member name, so the key resolves on a
    // newer engine and matches nothing on the floor — a vacuous index, silently. Both spellings
    // that reach it are pinned: a member needing the quotes for a '.', and one needing them only
    // because it *starts* with a '"'.
    [Theory]
    [InlineData("$.\"a.b\\\"c\"")]
    [InlineData("$.\"\\\"lead\"")]
    public void ValidateJsonPath_WithAQuoteInAMemberNeedingQuotes_ThrowsTheExactMessage(string path)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath(path, "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            $"Invalid JSON path '{path}': a member name with no unquoted spelling " +
            "cannot also contain a '\"'. SQLite 3.45.x, the minimum this library " +
            "supports, does not unescape a quoted member name, so the key would resolve " +
            "on a newer engine and silently match nothing on the minimum. Address it " +
            "through ExecuteRawAsync. (Parameter 'jsonPath')",
            ex.Message);
    }

    // The same rule at the resolver entry point, blaming the member that produced the name rather
    // than the path parameter. The two wordings are deliberately different and both are pinned.
    [Theory]
    [InlineData("a.b\"c")]
    [InlineData("\"lead")]
    public void JsonPathResolver_WithAQuoteInANameNeedingQuotes_ThrowsTheExactMessage(string member)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            $"'Holder.Field' serializes as '{member}', which has no unquoted " +
            "spelling as a JSON path member and also contains a '\"'. SQLite 3.45.x, the " +
            "minimum this library supports, does not unescape a quoted member name, so the " +
            "key would resolve on a newer engine and silently match nothing on the minimum. " +
            "Index it through ExecuteRawAsync. (Parameter 'jsonPath')",
            ex.Message);
    }

    // The refusal is narrow in both directions, and each half is a measurement rather than a
    // judgement. A '"' in a member that needs no quotes renders unquoted and resolves on both
    // builds; a '\' inside a quoted member resolves on both builds too, so escapes are not refused
    // as a class — only the one the floor cannot decode.
    [Fact]
    public void AQuoteOutsideTheQuotedForm_AndABackslashInsideIt_AreBothStillAccepted()
    {
        Assert.Equal("$.a\"b", SqlGenerator.ValidateJsonPath("$.a\"b", "jsonPath"));
        Assert.Equal("$.a\"b", ResolveWithSerializedName("a\"b"));

        Assert.Equal("$.\"a.b\\\\c\"", SqlGenerator.ValidateJsonPath("$.\"a.b\\\\c\"", "jsonPath"));
        Assert.Equal("$.\"a.b\\\\c\"", ResolveWithSerializedName("a.b\\c"));
    }

    // An apostrophe anywhere outranks the version refusal: it is positional and permanent, and the
    // recovery sentences differ, so a member carrying both must still be blamed on the apostrophe.
    [Fact]
    public void AQuoteAndAnApostropheInOneMember_IsBlamedOnTheApostrophe()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.\"a.b\\\"c'd\"", "jsonPath"));

        Assert.Contains("apostrophe", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3.45", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a'b", "Index it through ExecuteRawAsync.")]
    [InlineData("a\0b", "No JSON path can address it, in this library or through raw SQL.")]
    public void JsonPathResolver_WithAnUnexpressibleName_ThrowsTheExactMessage(
        string member,
        string recovery)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            $"'Holder.Field' serializes as '{member}', which is not expressible as a " +
            $"JSON path member (it may contain neither U+0000 nor an apostrophe). {recovery} " +
            "(Parameter 'jsonPath')",
            ex.Message);
    }

    // --- Precedence: the first offending character by position ---------------------------------
    //
    // The shared predicate reports the first fault by position, which is what both entry points did
    // before they shared it. It is load-bearing on the resolver side, because the recovery sentence
    // splits on the reason: a name faulting on an apostrophe keeps the raw-SQL pointer even when a
    // NUL sits further along, and only a name whose *first* fault is a NUL loses it.

    [Theory]
    [InlineData("$.a'b\0c", "apostrophe")]
    [InlineData("$.a\0b'c", "U+0000")]
    public void ValidateJsonPath_WithTwoFaults_ReportsTheFirstOneByPosition(
        string jsonPath,
        string expected)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonPathResolver_WithANulAfterAnotherFault_KeepsTheRawSqlPointer()
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName("a'b\0c"));

        Assert.Contains("Index it through ExecuteRawAsync", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No JSON path can address it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonPathResolver_WithANulBeforeAnotherFault_DropsTheRawSqlPointer()
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName("a\0b'c"));

        Assert.Contains("No JSON path can address it", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteRawAsync", ex.Message, StringComparison.Ordinal);
    }

    // --- Driving the resolver from an arbitrary serialized name --------------------------------

    private sealed class Holder
    {
        public string Field { get; set; } = "";
    }

    // A resolver modifier rather than a [JsonPropertyName] per row, so the member strings in
    // Members above stay the single source of truth: an attribute would have to restate each one
    // as a compile-time constant, which is the two-tables-that-happen-to-match shape this file
    // exists to prevent. It sets the same JsonPropertyInfo.Name the attribute would, which is what
    // JsonPathResolver reads.
    private static string ResolveWithSerializedName(string name)
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Type != typeof(Holder))
                        {
                            return;
                        }

                        foreach (var property in info.Properties)
                        {
                            if ((property.AttributeProvider as MemberInfo)?.Name == nameof(Holder.Field))
                            {
                                property.Name = name;
                            }
                        }
                    }
                }
            }
        };

        return JsonPathResolver.Resolve<Holder>(x => x.Field, options, "jsonPath");
    }

    private static bool Accepts(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
