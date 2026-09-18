using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Validation of the three inputs <see cref="SqlGenerator"/> must interpolate rather than
/// bind: identifiers, JSON paths and column types.
/// </summary>
[Trait("Category", "Unit")]
public class SqlGeneratorValidationTests
{
    [Theory]
    [InlineData("Person")]
    [InlineData("_private")]
    [InlineData("__store_blobs")]
    [InlineData("Order2")]
    public void ValidIdentifiers_AreAccepted(string tableName)
    {
        var sql = SqlGenerator.GenerateGetByIdSql(tableName);

        Assert.Contains($"[{tableName}]", sql);
    }

    [Theory]
    [InlineData("Person]; DROP TABLE Person; --")]  // ] closes the bracket quoting
    [InlineData("Person Two")]
    [InlineData("2Fast")]
    [InlineData("Person\"")]
    [InlineData("")]
    public void InvalidIdentifiers_ThrowArgumentException(string tableName)
    {
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateGetByIdSql(tableName));
    }

    [Fact]
    public void InvalidIdentifier_IsRejectedOnEveryInterpolatingGenerator()
    {
        const string Injected = "x] ON [Person] (id); --";

        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateCreateTableSql(Injected));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateUpsertSql(Injected));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkUpsertSql(Injected, 2));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateBulkDeleteSql(Injected, 2));
        Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateCountSql(Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", Injected, "$.Email"));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateColumnIndexSql("Person", "idx", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", Injected, "$.Email"));
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$.Email")]
    [InlineData("$.Address.City")]
    [InlineData("$.Tags[0]")]
    [InlineData("$.Orders[12].Total")]
    [InlineData("$._internal")]
    public void ValidJsonPaths_AreAccepted(string jsonPath)
    {
        var sql = SqlGenerator.GenerateQueryByJsonPathSql("Person", jsonPath);

        Assert.Contains($"json_extract(data, '{jsonPath}')", sql);
    }

    [Theory]
    [InlineData("$.a') = 1 OR 1=1 --")]  // the historical injection: ' closes the literal
    [InlineData("$.Email'")]
    [InlineData("Email")]                // no leading $
    [InlineData("$.")]
    [InlineData("$.Email.")]
    [InlineData("$.Tags[]")]
    [InlineData("$.Tags[a]")]
    [InlineData("$.Tags[0")]
    [InlineData("$..Email")]
    [InlineData("$ .Email")]
    [InlineData("")]
    public void InvalidJsonPaths_ThrowArgumentException(string jsonPath)
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQueryByJsonPathSql("Person", jsonPath));
    }

    [Fact]
    public void InvalidJsonPath_IsRejectedByTheIndexAndVirtualColumnGenerators()
    {
        const string Injected = "$.a') = 1 OR 1=1 --";

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx", ["$.Email", Injected]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", Injected));
    }

    [Theory]
    [InlineData("TEXT", "TEXT")]
    [InlineData("integer", "INTEGER")]
    [InlineData("Real", "REAL")]
    [InlineData("blob", "BLOB")]
    [InlineData("NUMERIC", "NUMERIC")]
    public void ColumnTypes_AreWhitelistedAndCanonicalized(string requested, string expected)
    {
        var sql = SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", "$.Email", requested);

        Assert.Contains($"[email] {expected} GENERATED ALWAYS AS", sql);
    }

    [Theory]
    [InlineData("TEXT DEFAULT 'x'")]
    [InlineData("TEXT, dropped INTEGER")]
    [InlineData("VARCHAR(255)")]
    [InlineData("")]
    public void UnsupportedColumnTypes_ThrowArgumentException(string columnType)
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "email", "$.Email", columnType));
    }

    // The bare root is grammatically valid, and the split is by what the caller does with the
    // value: a read path (query predicate, ordering, partial-index filter) only extracts the
    // whole document through it and keeps accepting it, while a patch rewrites or deletes the
    // document and the projecting DDL duplicates or keys on it — both opt out.

    [Fact]
    public void TheDocumentRoot_IsAcceptedWhenRootIsAllowed()
    {
        Assert.Equal("$", SqlGenerator.ValidateJsonPath("$", "jsonPath"));
    }

    [Fact]
    public void TheDocumentRoot_IsRejectedWhenRootIsNotAllowed()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath("$", "jsonPath", allowRoot: false));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    [Theory]
    [InlineData("$[0]")]
    [InlineData("$.Email")]
    public void APathBelowTheRoot_IsAcceptedEitherWay(string jsonPath)
    {
        Assert.Equal(jsonPath, SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));
        Assert.Equal(jsonPath, SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath), allowRoot: false));
    }

    // --- The document root in the projecting DDL -----------------------------------------

    [Fact]
    public void GenerateCreateJsonIndexSql_WithTheDocumentRoot_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx_whole_document", "$"));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    [Fact]
    public void GenerateCreateCompositeJsonIndexSql_WithTheDocumentRoot_Throws()
    {
        // The root sits second, so this fails only if every component is validated.
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx", ["$.Email", "$"]));

        Assert.Equal("jsonPaths", exception.ParamName);
    }

    [Fact]
    public void GenerateAddVirtualColumnSql_WithTheDocumentRoot_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "whole", "$"));

        Assert.Equal("jsonPath", exception.ParamName);
    }

    [Fact]
    public void TheProjectingDdl_StillAcceptsAnIndexerAtTheRoot()
    {
        Assert.Contains(
            "json_extract(data, '$[0]')",
            SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx_first", "$[0]"),
            StringComparison.Ordinal);
        Assert.Contains(
            "json_extract(data, '$[0]')",
            SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx_first", ["$.Email", "$[0]"]),
            StringComparison.Ordinal);
        Assert.Contains(
            "json_extract(data, '$[0]')",
            SqlGenerator.GenerateAddVirtualColumnSql("Person", "first", "$[0]"),
            StringComparison.Ordinal);
    }

    // The reading paths keep the root: they extract the whole serialized document and compare
    // or order by it, which is blunt but not destructive and not a duplicated projection.

    [Fact]
    public void TheReadingPaths_StillAcceptTheDocumentRoot()
    {
        Assert.Contains(
            "json_extract(data, '$')",
            SqlGenerator.GenerateQueryByJsonPathSql("Person", "$"),
            StringComparison.Ordinal);

        var query = SqlGenerator.GenerateQuerySql(
            "Person",
            [new QueryPredicate("$", QueryOperator.Equal, "x", [])],
            [new QueryOrdering("$", false)],
            null,
            null);
        Assert.Contains("json_extract(data, '$')", query.Sql, StringComparison.Ordinal);

        var filtered = SqlGenerator.GenerateCreateJsonIndexSql(
            "Person",
            "idx_email",
            "$.Email",
            new IndexOptions { Filter = IndexFilter.IsNotNull("$") });
        Assert.Contains("json_extract(data, '$') IS NOT NULL", filtered, StringComparison.Ordinal);
    }

    // --- The widened member rule (C18 Tier 1) --------------------------------------------------
    //
    // A member is one or more characters, none of which is an apostrophe, a '.' or a '['. That
    // mirrors SQLite's own unquoted path label: measured against 3.53.3, every shape below resolves
    // unquoted in json_extract, jsonb_set, jsonb_remove and json_each. The old identifier-shaped
    // rule made a store on JsonNamingPolicy.KebabCaseLower ("FullName" -> "full-name") unable to use
    // any typed path API at all.

    [Theory]
    [InlineData("$.full-name")]          // JsonNamingPolicy.KebabCaseLower
    [InlineData("$.a b")]                // a space
    [InlineData("$.caf\u00e9")]              // non-ASCII
    [InlineData("$.a\U0001F600b")]           // outside the BMP
    [InlineData("$.2024")]               // leading digit
    [InlineData("$.a$b")]                // the path's own root marker, mid-member
    [InlineData("$.a]b")]                // a ']' is not structural in a path
    // A newline and a tab are ACCEPTED and must stay so: measured, neither terminates the SQL
    // string and each reads back its own key. Only U+0000 truncates the statement.
    [InlineData("$.a\nb")]               // a newline
    [InlineData("$.a\tb")]               // a tab
    [InlineData("$.full-name.a b")]      // chained widened members
    [InlineData("$.a-b[3].c d")]         // widened members either side of an indexer
    public void WidenedJsonPathMembers_AreAccepted(string jsonPath)
    {
        Assert.Equal(jsonPath, SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));
        Assert.Contains(
            $"json_extract(data, '{jsonPath}')",
            SqlGenerator.GenerateQueryByJsonPathSql("Person", jsonPath),
            StringComparison.Ordinal);
    }

    [Theory]
    // A '.' and a '[' stay structural, so a member cannot carry one: "$.a.b" is unambiguously the
    // nested path a -> b, never the single key "a.b", and "$.a[b" is read as an indexer. Reaching a
    // key that really contains one needs the $."quoted" form (C18 Tier 2), which is not implemented;
    // what is pinned here is that widening the member rule did not make these ambiguous or legal.
    [InlineData("$..Email")]             // an empty member between two dots
    [InlineData("$.a.")]                 // an empty trailing member
    [InlineData("$.")]                   // the empty member: SQLite errors on it
    [InlineData("$.a[b")]                // a '[' opens an indexer, which must be decimal
    [InlineData("$.a[b].c")]
    public void StructuralCharactersAndTheEmptyMember_StayRejected(string jsonPath)
    {
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));
    }

    // THE INJECTION BOUNDARY. The path is interpolated into a single-quoted SQL literal
    // (json_extract(data, '...')), so an apostrophe is the one character that can escape it and is
    // the only reason this validator exists. It is deliberately not supported by doubling: SQLite
    // matches a query against an expression index only when the indexed expression appears
    // literally, so rewriting the emitted text would silently disable every index CreateIndexAsync
    // creates. Widening the member rule must not widen this.
    [Theory]
    [InlineData("$.a'b")]
    [InlineData("$.Email'")]
    [InlineData("$.a') = 1 OR 1=1 --")]
    [InlineData("$.full-name') UNION SELECT 1 --")]
    [InlineData("$.a'b.c")]
    [InlineData("$.a[0].b'c")]
    public void AnApostropheInAMember_StaysRejected_SoTheSqlLiteralCannotBeClosed(string jsonPath)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));

        Assert.Contains("apostrophe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApostropheInAMember_IsRejectedByEveryPathGenerator()
    {
        const string Injected = "$.a') = 1 OR 1=1 --";

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQueryByJsonPathSql("Person", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx_x", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx_x", ["$.Email", Injected]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "vc", Injected));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GeneratePatchSql("Person", [new PatchOperation(Injected, PatchOperationKind.Set, 1, false)], false));
    }

    // U+0000 is the one character the widened rule would otherwise have admitted that the old
    // identifier-shaped rule rejected. sqlite3_prepare reads a NUL-terminated string, so a NUL in
    // an interpolated path truncates the whole statement at that byte; the path always sits
    // immediately after an opening apostrophe, so the prefix always ends inside an unterminated
    // literal and SQLite always answers SQLITE_ERROR "unrecognized token" — measured across every
    // generator that interpolates a path. Loud, but a raw SqliteException carrying a truncated,
    // misleading message, leaked from a typed API for an argument this validator should refuse.
    //
    // It is NOT a C18 Tier 2 shape. Quoting cannot rescue it, measured both interpolated and bound:
    // $."a\0b" and $['a\0b'] fail too, so such a key is unaddressable by every form.
    [Theory]
    [InlineData("$.\0ab")]               // at the start of a member
    [InlineData("$.a\0b")]               // in the middle
    [InlineData("$.ab\0")]               // at the end
    [InlineData("$.a\0b.c")]             // in a leading member of a chain
    [InlineData("$.a[0].b\0c")]          // after an indexer
    public void ANulInAMember_IsRejected_BecauseItTruncatesTheStatement(string jsonPath)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)));

        Assert.Contains("U+0000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANulInAMember_IsRejectedByEveryPathGenerator()
    {
        const string Nul = "$.a\0b";

        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQueryByJsonPathSql("Person", Nul));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql("Person", "idx_x", Nul));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateCompositeJsonIndexSql("Person", "idx_x", ["$.Email", Nul]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateAddVirtualColumnSql("Person", "vc", Nul));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GeneratePatchSql("Person", [new PatchOperation(Nul, PatchOperationKind.Set, 1, false)], false));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GeneratePatchSql("Person", [new PatchOperation(Nul, PatchOperationKind.Remove, null, false)], false));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQuerySql("Person", [new QueryPredicate(Nul, QueryOperator.Equal, 1, [])], [], null, null));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateQuerySql("Person", [], [new QueryOrdering(Nul, false)], null, null));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateFilteredCountSql("Person", [new QueryPredicate(Nul, QueryOperator.Equal, 1, [])]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateFilteredExistsSql("Person", [new QueryPredicate(Nul, QueryOperator.Equal, 1, [])]));
        Assert.Throws<ArgumentException>(
            () => SqlGenerator.GenerateCreateJsonIndexSql(
                "Person", "idx_x", "$.Email", new IndexOptions { Filter = IndexFilter.IsNotNull(Nul) }));
    }

    // The identifier rule stays narrow while the path rule widens, and the non-throwing form
    // RequireDerivableName screens a derived index name through must agree with the throwing one.
    [Theory]
    [InlineData("Person", true)]
    [InlineData("_private", true)]
    [InlineData("Order2", true)]
    [InlineData("idx_Person_full-name", false)]
    [InlineData("idx_Person_a b", false)]
    [InlineData("2Fast", false)]
    [InlineData("", false)]
    public void IsValidIdentifier_AgreesWithValidateIdentifier(string identifier, bool expected)
    {
        Assert.Equal(expected, SqlGenerator.IsValidIdentifier(identifier));

        if (expected)
        {
            Assert.Contains($"[{identifier}]", SqlGenerator.GenerateGetByIdSql(identifier), StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => SqlGenerator.GenerateGetByIdSql(identifier));
        }
    }
}
