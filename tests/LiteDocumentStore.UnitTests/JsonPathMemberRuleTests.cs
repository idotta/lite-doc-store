using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The JSON path <em>member</em> rule, which has exactly one owner —
/// <c>SqlGenerator.MemberFault</c> — and two entry points that word its refusal differently on
/// purpose: <c>SqlGenerator.ValidateJsonPath</c>, which slices each member out of a whole path and
/// blames the path parameter, and <c>JsonPathResolver</c>, which is handed one already-resolved
/// serialized name and blames the member that produced it.
///
/// <para>
/// This file is the pin on that consolidation. <see cref="Members" /> is the single table of member
/// strings; every assertion below is driven from it, so the two entry points cannot drift into
/// disagreeing about what a member may contain. The message-preservation tests below it pin the
/// two wordings byte for byte, so a future consolidation of the <em>messages</em> fails the build:
/// the reporting split is deliberate and survives.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class JsonPathMemberRuleTests
{
    /// <param name="Member">The isolated member string under test.</param>
    /// <param name="AcceptedAsAMember">
    /// The member rule's own verdict. This is what both entry points must agree on.
    /// </param>
    /// <param name="AcceptedAsThePath">
    /// What <c>ValidateJsonPath</c> makes of <c>"$." + Member</c>. It equals
    /// <paramref name="AcceptedAsAMember" /> except on the rows where the path walk re-tokenizes
    /// the member into something else, which is what <paramref name="Divergence" /> records.
    /// </param>
    /// <param name="Divergence">
    /// Empty when <c>"$." + Member</c> really is the single member <paramref name="Member" />;
    /// otherwise why the two entry points legitimately read that text differently. '.' and '[' are
    /// member <em>delimiters</em> to the path walk and illegal <em>characters</em> to the resolver,
    /// and the empty member is not a member the path walk can even be handed the same way.
    /// </param>
    public sealed record MemberCase(
        string Member,
        bool AcceptedAsAMember,
        bool AcceptedAsThePath,
        string Divergence);

    /// <summary>
    /// The one source of truth. Both entry points are driven through every row.
    /// </summary>
    public static readonly MemberCase[] Members =
    [
        // --- Accepted: the member rule mirrors SQLite's own unquoted path label ---------------
        new("Email", true, true, ""),
        new("full-name", true, true, ""),           // JsonNamingPolicy.KebabCaseLower
        new("a b", true, true, ""),                 // a space
        new("a\nb", true, true, ""),                // a newline — measured, reads back its own key
        new("a\tb", true, true, ""),                // a tab — likewise
        new("café", true, true, ""),           // accented
        new("a\U0001F600b", true, true, ""),        // an emoji, outside the BMP
        new("2024", true, true, ""),                // leading digit
        new("a]b", true, true, ""),                 // ']' is not structural in a path

        // --- Rejected, and the two entry points agree -----------------------------------------
        new("a'b", false, false, ""),               // the injection boundary
        new("a\0b", false, false, ""),              // truncates the statement; permanently rejected
        new("a[b", false, false, ""),               // '[' opens an indexer, which must be decimal

        // --- Rejected as a member; "$." + Member means something else to the path walk ---------
        new(
            "a.b",
            false,
            true,
            "The path walk reads \"$.a.b\" as the two-member path a -> b, never the single key " +
            "\"a.b\". Reaching that key needs the $.\"quoted\" form (C18 Tier 2), unimplemented."),
        new(
            "a[0]",
            false,
            true,
            "The path walk reads \"$.a[0]\" as the member a followed by an indexer, never the " +
            "single key \"a[0]\"."),
        new(
            "",
            false,
            false,
            "Both refuse, but not for the same reason: the resolver refuses an empty serialized " +
            "name, while the path walk refuses \"$.\" as a '.' with no member after it.")
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
    // the three shapes the asymmetry is known to cover. Without this, a later row could opt itself
    // out of the agreement assertion just by filling in a Divergence string.
    [Fact]
    public void TheMemberTable_RecordsDivergenceOnlyWhereThePathWalkReTokenizes()
    {
        foreach (var member in Members)
        {
            if (member.Divergence.Length == 0)
            {
                Assert.Equal(member.AcceptedAsAMember, member.AcceptedAsThePath);
            }
            else
            {
                Assert.True(
                    member.Member.Length == 0
                        || member.Member.Contains('.', StringComparison.Ordinal)
                        || member.Member.Contains('[', StringComparison.Ordinal),
                    $"'{member.Member}' claims a divergence but carries no '.', no '[' and is not empty.");
            }
        }

        Assert.Equal(3, Members.Count(m => m.Divergence.Length > 0));
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

        if (expected.AcceptedAsAMember)
        {
            Assert.Equal($"$.{member}", ResolveWithSerializedName(member));
            return;
        }

        Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));
    }

    /// <summary>
    /// The path entry point, over the same table. On the three divergent rows this asserts the
    /// recorded path verdict rather than the member verdict — that difference is the path grammar
    /// speaking, not the member rule.
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

            Assert.Equal(member.AcceptedAsAMember, resolverAccepts);
            Assert.Equal(resolverAccepts, pathAccepts);
        }
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

    [Fact]
    public void ValidateJsonPath_WithAnEmptyMember_ThrowsTheExactMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$.", "jsonPath"));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            "Invalid JSON path '$.': a '.' must be followed by a member name of one or " +
            "more characters, none of which is U+0000, an apostrophe, a '.' or a '['. " +
            "(Parameter 'jsonPath')",
            ex.Message);
    }

    [Theory]
    [InlineData("a'b", "Index it through ExecuteRawAsync.")]
    [InlineData("a.b", "Index it through ExecuteRawAsync.")]
    [InlineData("a[0]", "Index it through ExecuteRawAsync.")]
    [InlineData("", "Index it through ExecuteRawAsync.")]
    [InlineData("a\0b", "No JSON path can address it, in this library or through raw SQL.")]
    public void JsonPathResolver_WithAnUnexpressibleName_ThrowsTheExactMessage(
        string member,
        string recovery)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));

        Assert.Equal("jsonPath", ex.ParamName);
        Assert.Equal(
            $"'Holder.Field' serializes as '{member}', which is not expressible as a " +
            "JSON path member (it must be one or more characters, none of which is U+0000, an " +
            $"apostrophe, a '.' or a '['). {recovery} (Parameter 'jsonPath')",
            ex.Message);
    }

    // --- Precedence: the first offending character by position ---------------------------------
    //
    // The shared predicate reports the first fault by position, which is what both entry points did
    // before they shared it. It is load-bearing on the resolver side, because the recovery sentence
    // splits on the reason: a name faulting on a '.' or an apostrophe keeps the raw-SQL pointer even
    // when a NUL sits further along, and only a name whose *first* fault is a NUL loses it.

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

    [Theory]
    [InlineData("a.b'c")]      // faults on the '.', so the recovery is the raw-SQL pointer
    [InlineData("a'b\0c")]     // faults on the apostrophe, not the NUL that follows
    public void JsonPathResolver_WithANulAfterAnotherFault_KeepsTheRawSqlPointer(string member)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolveWithSerializedName(member));

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
