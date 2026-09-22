using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The readable half of an auto-derived index name is a fold, and a fold collides: <c>$.A.B</c>
/// and <c>$.A_B</c> flatten alike, a composite of <c>["$.A","$.B"]</c> flattens onto the same
/// spelling as <c>["$.A.B"]</c>, and the index on a generated column reads the same as the
/// expression index for the member it was generated from. Creation-time collisions were already
/// loud, but <c>DropIndexAsync&lt;T&gt;(expression)</c> is handed a path, derives a name and drops
/// whatever holds it — so the close is distinct names, and these tests pin that the digest each
/// derived name carries separates all four shapes while leaving the name a legal identifier.
/// </summary>
[Trait("Category", "Unit")]
public class IndexNameDerivationTests
{
    private const string Table = "Member";

    [Fact]
    public void GenerateIndexName_ForADottedAndAnUnderscoredPath_DiffersInTheDigest()
    {
        var dotted = DocumentOperations.GenerateIndexName(Table, "$.A.B");
        var underscored = DocumentOperations.GenerateIndexName(Table, "$.A_B");

        Assert.Equal("idx_Member_A_B", ReadableHalf(dotted));
        Assert.Equal("idx_Member_A_B", ReadableHalf(underscored));
        Assert.NotEqual(dotted, underscored);
    }

    [Fact]
    public void GenerateCompositeIndexName_AgainstOneJoinedPath_DiffersInTheDigest()
    {
        var pair = DocumentOperations.GenerateCompositeIndexName(Table, ["$.A", "$.B"]);
        var single = DocumentOperations.GenerateCompositeIndexName(Table, ["$.A.B"]);

        Assert.Equal("idx_Member_composite_A_B", ReadableHalf(pair));
        Assert.Equal("idx_Member_composite_A_B", ReadableHalf(single));
        Assert.NotEqual(pair, single);
    }

    [Fact]
    public void GenerateColumnIndexName_KeepsTheTableAndColumnAsSeparateDigestFields()
    {
        // The digest input is U+0000-delimited fields, not one concatenation. A column index is
        // the shape where that matters: nothing marks the boundary the way a path's leading "$."
        // does, so table "A" with column "_B" and table "A_" with column "B" read the same
        // ("idx_A__B") and would digest the same bytes if the fields ran together. U+0000 is the
        // one character neither a table name nor a validated path can carry, so no input forges
        // the boundary.
        var onA = DocumentOperations.GenerateColumnIndexName("A", "_B");
        var onAUnderscore = DocumentOperations.GenerateColumnIndexName("A_", "B");

        Assert.Equal(ReadableHalf(onA), ReadableHalf(onAUnderscore));
        Assert.NotEqual(onA, onAUnderscore);
    }

    [Fact]
    public void GenerateIndexName_AgainstTheCompositeOfTheSamePath_Differs()
    {
        // The single-path fold of "$.composite_A_B" is exactly the composite prefix plus the
        // pair's fold, so the readable halves agree and only the digest separates them.
        Assert.NotEqual(
            DocumentOperations.GenerateIndexName(Table, "$.composite_A_B"),
            DocumentOperations.GenerateCompositeIndexName(Table, ["$.A", "$.B"]));
    }

    [Fact]
    public void GenerateColumnIndexName_AgainstTheExpressionIndexForTheSameMember_Differs()
    {
        var column = DocumentOperations.GenerateColumnIndexName(Table, "Email");
        var expression = DocumentOperations.GenerateIndexName(Table, "$.Email");

        // The readable halves are identical — the column is named after the member — so only the
        // digest separates them: the path field holds "$.Email" for one and "Email" for the
        // other. (The "column" kind states the same distinction but is not what carries it; see
        // IndexNameDigest.)
        Assert.Equal(ReadableHalf(column), ReadableHalf(expression));
        Assert.NotEqual(column, expression);
    }

    [Fact]
    public void GenerateIndexName_StripsOnlyALeadingDollarDot()
    {
        // A global Replace("$.", "") folded "$.a$.b" onto "ab" and so onto "$.ab"'s name. The
        // digest separates them either way; the readable half should not lie about the path.
        var embedded = DocumentOperations.GenerateIndexName(Table, "$.a$.b");
        var plain = DocumentOperations.GenerateIndexName(Table, "$.ab");

        Assert.Equal("idx_Member_a$_b", ReadableHalf(embedded));
        Assert.Equal("idx_Member_ab", ReadableHalf(plain));
        Assert.NotEqual(embedded, plain);
    }

    [Fact]
    public void GenerateIndexName_ForOnePathOnTwoTables_Differs()
    {
        // The table is a digest field of its own, not just a readable prefix: "idx_" joined to
        // ("A", "$.B_C") and to ("A_B", "$.C") reads identically, and only the digest separates
        // the two indexes — which sit on different tables and so are genuinely different.
        var onA = DocumentOperations.GenerateIndexName("A", "$.B_C");
        var onAB = DocumentOperations.GenerateIndexName("A_B", "$.C");

        Assert.Equal(ReadableHalf(onA), ReadableHalf(onAB));
        Assert.NotEqual(onA, onAB);
    }

    [Fact]
    public void EveryDerivation_MatchesItsGoldenName()
    {
        // The algorithm itself is pinned by nothing else here: every other test asserts shape,
        // determinism or distinctness, all of which survive swapping SHA-256 for another hash or
        // taking hash[3..6] instead of hash[..3]. The names are a contract now - README documents
        // the scheme and prints the Customer pair below, and a change to the algorithm silently
        // renames every auto-derived index on every existing database. So these literals are
        // computed from the spec (SHA-256 over kind \0 table \0 path[ \0 path...], first three
        // bytes as lowercase hex) rather than read back from the derivation.
        Assert.Equal("idx_Member_A_B_45f9b8", DocumentOperations.GenerateIndexName(Table, "$.A.B"));
        Assert.Equal("idx_Member_Email_17a3b8", DocumentOperations.GenerateColumnIndexName(Table, "Email"));
        Assert.Equal(
            "idx_Member_composite_A_B_07549d",
            DocumentOperations.GenerateCompositeIndexName(Table, ["$.A", "$.B"]));

        // The pair README prints, whose readable halves coincide.
        Assert.Equal("idx_Customer_Email_3cf60a", DocumentOperations.GenerateIndexName("Customer", "$.Email"));
        Assert.Equal("idx_Customer_Email_4ee840", DocumentOperations.GenerateColumnIndexName("Customer", "Email"));
    }

    [Theory]
    [InlineData("$.Email")]
    [InlineData("$.A.B")]
    [InlineData("$.a_b_c")]
    public void GenerateIndexName_IsDeterministicAndAnIdentifier(string jsonPath)
    {
        var name = DocumentOperations.GenerateIndexName(Table, jsonPath);

        Assert.Equal(name, DocumentOperations.GenerateIndexName(Table, jsonPath));
        Assert.True(SqlGenerator.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("$.Email")]
    [InlineData("$.A.B")]
    public void EveryDerivation_EndsInSixLowercaseHexCharacters(string jsonPath)
    {
        // Six hex characters is what keeps the suffix inside [A-Za-z_][A-Za-z0-9_]*, so the
        // digest can never be what makes a derived name fail RequireDerivableName's screen.
        AssertDigestShape(DocumentOperations.GenerateIndexName(Table, jsonPath));
        AssertDigestShape(DocumentOperations.GenerateCompositeIndexName(Table, [jsonPath, "$.Age"]));
        AssertDigestShape(DocumentOperations.GenerateColumnIndexName(Table, "col"));
    }

    private static void AssertDigestShape(string derivedName)
    {
        var digest = DigestOf(derivedName);

        Assert.Equal(6, digest.Length);
        Assert.All(digest, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'), $"'{c}' is not lowercase hex"));
    }

    private static string DigestOf(string derivedName) =>
        derivedName[(derivedName.LastIndexOf('_') + 1)..];

    private static string ReadableHalf(string derivedName) =>
        derivedName[..derivedName.LastIndexOf('_')];
}
