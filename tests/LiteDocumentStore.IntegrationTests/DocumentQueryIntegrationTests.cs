using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// End-to-end coverage for the structured <see cref="DocumentQuery{T}"/> API against real
/// SQLite: every operator, ordering, paging, counting, transactions, and the two contracts the
/// generator must not break — the interpolated path still hits its expression index, and a
/// malicious path is still rejected.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DocumentQueryIntegrationTests : IAsyncLifetime
{
    private sealed record WidgetAddress(string City);

    private sealed record Widget(
        string Id,
        string Name,
        int Quantity,
        string? Nickname,
        DateTime CreatedAt,
        string[] Tags,
        WidgetAddress Address);

    // w5 is inserted as raw JSON below because it has no Nickname property at all — the CLR
    // type always serializes one, so the "missing" case cannot be produced through UpsertAsync.
    private static readonly Widget[] Seed =
    [
        new("w1", "Anvil", 10, "Andy", new DateTime(2024, 1, 1), ["heavy", "metal"], new WidgetAddress("Berlin")),
        new("w2", "Bolt", 20, null, new DateTime(2024, 2, 1), ["metal", "small"], new WidgetAddress("Athens")),
        new("w3", "Cog", 30, "Cee", new DateTime(2024, 3, 1), ["small"], new WidgetAddress("Cairo")),
        new("w4", "anvil", 40, "Andy", new DateTime(2024, 4, 1), ["heavy"], new WidgetAddress("Berlin"))
    ];

    private const string MissingNicknameJson =
        """
        {"Id":"w5","Name":"Dowel","Quantity":50,"CreatedAt":"2024-05-01T00:00:00","Tags":["wood"],"Address":{"City":"Delhi"}}
        """;

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Widget>();

        foreach (var widget in Seed)
        {
            await _store.UpsertAsync(widget.Id, widget);
        }

        await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO [{_store.GetTableName<Widget>()}] (id, data, version) VALUES ('w5', jsonb(@Data), 1)";
            command.Parameters.AddWithValue("@Data", MissingNicknameJson);
            await command.ExecuteNonQueryAsync(ct);
        });
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    // Ids are compared as one comma-joined string, so an assertion reads as a sequence.
    private static string Join(IEnumerable<Widget> widgets) => string.Join(',', widgets.Select(w => w.Id));

    private async Task<string> IdsAsync(DocumentQuery<Widget> query) =>
        Join(await _store.QueryAsync(query));

    private async Task<string> SortedIdsAsync(DocumentQuery<Widget> query) =>
        string.Join(',', (await IdsAsync(query))
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(QueryOperator.Equal, "w3")]
    [InlineData(QueryOperator.NotEqual, "w1,w2,w4,w5")]
    [InlineData(QueryOperator.GreaterThan, "w4,w5")]
    [InlineData(QueryOperator.GreaterThanOrEqual, "w3,w4,w5")]
    [InlineData(QueryOperator.LessThan, "w1,w2")]
    [InlineData(QueryOperator.LessThanOrEqual, "w1,w2,w3")]
    public async Task QueryAsync_WithEachComparisonOperator_ReturnsTheMatchingDocuments(
        QueryOperator op,
        string expectedIds)
    {
        var matches = await SortedIdsAsync(DocumentQuery<Widget>.Where("$.Quantity", op, 30));

        Assert.Equal(expectedIds, matches);
    }

    [Fact]
    public async Task QueryAsync_WithLike_IsCaseInsensitiveForAscii()
    {
        var matches = await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Name", QueryOperator.Like, "anvil%"));

        Assert.Equal("w1,w4", matches);
    }

    [Fact]
    public async Task QueryAsync_WithGlob_IsCaseSensitive()
    {
        var lowercase = await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Name", QueryOperator.Glob, "anvil*"));
        var capitalized = await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Name", QueryOperator.Glob, "Anvil*"));

        Assert.Equal("w4", lowercase);
        Assert.Equal("w1", capitalized);
    }

    [Fact]
    public async Task QueryAsync_WithIn_ReturnsEveryListedMatch()
    {
        var matches = await SortedIdsAsync(
            DocumentQuery<Widget>.WhereIn("$.Name", ["Bolt", "Cog", "Nonexistent"]));

        Assert.Equal("w2,w3", matches);
    }

    [Fact]
    public async Task QueryAsync_WithInMatchingNothing_ReturnsEmpty()
    {
        var matches = await IdsAsync(DocumentQuery<Widget>.WhereIn("$.Name", ["Nope", "Also nope"]));

        Assert.Equal(string.Empty, matches);
    }

    [Fact]
    public async Task QueryAsync_WithIsNull_MatchesAJsonNullAndAMissingProperty()
    {
        // json_extract yields SQL NULL both for a JSON null (w2) and for an absent path (w5), so
        // IS NULL cannot tell the two apart — the old path+value API could express neither.
        var matches = await SortedIdsAsync(DocumentQuery<Widget>.WhereIsNull("$.Nickname"));

        Assert.Equal("w2,w5", matches);
    }

    [Fact]
    public async Task QueryAsync_WithIsNotNull_ExcludesAJsonNullAndAMissingProperty()
    {
        var matches = await SortedIdsAsync(DocumentQuery<Widget>.WhereIsNotNull("$.Nickname"));

        Assert.Equal("w1,w3,w4", matches);
    }

    [Fact]
    public async Task QueryAsync_WithArrayContains_ReturnsDocumentsWhoseArrayHoldsTheValue()
    {
        var matches = await SortedIdsAsync(
            DocumentQuery<Widget>.WhereArrayContains("$.Tags", "metal"));

        Assert.Equal("w1,w2", matches);
    }

    [Fact]
    public async Task QueryAsync_WithArrayContainsAnAbsentElement_ReturnsEmpty()
    {
        var matches = await IdsAsync(DocumentQuery<Widget>.WhereArrayContains("$.Tags", "plastic"));

        Assert.Equal(string.Empty, matches);
    }

    [Fact]
    public async Task QueryAsync_WithSeveralPredicates_AndsThemTogether()
    {
        var matches = await SortedIdsAsync(DocumentQuery<Widget>
            .Where("$.Address.City", QueryOperator.Equal, "Berlin")
            .And("$.Quantity", QueryOperator.GreaterThan, 10)
            .AndArrayContains("$.Tags", "heavy"));

        Assert.Equal("w4", matches);
    }

    [Fact]
    public async Task QueryAsync_WithOrderByAscending_ReturnsTheDocumentsInOrder()
    {
        var ordered = await IdsAsync(DocumentQuery<Widget>.All().OrderBy("$.Quantity"));

        Assert.Equal("w1,w2,w3,w4,w5", ordered);
    }

    [Fact]
    public async Task QueryAsync_WithOrderByDescending_ReturnsTheDocumentsInOrder()
    {
        var ordered = await IdsAsync(
            DocumentQuery<Widget>.All().OrderBy("$.CreatedAt", descending: true));

        Assert.Equal("w5,w4,w3,w2,w1", ordered);
    }

    [Fact]
    public async Task QueryAsync_WithTwoOrderings_UsesTheSecondAsTiebreaker()
    {
        var ordered = await IdsAsync(DocumentQuery<Widget>.All()
            .OrderBy("$.Address.City")
            .OrderBy("$.Quantity", descending: true));

        // Athens, then the two Berlins by descending quantity, then Cairo, then Delhi.
        Assert.Equal("w2,w4,w1,w3,w5", ordered);
    }

    [Fact]
    public async Task QueryAsync_WithTake_ReturnsTheFirstPage()
    {
        var page = await IdsAsync(DocumentQuery<Widget>.All().OrderBy("$.Quantity").Take(2));

        Assert.Equal("w1,w2", page);
    }

    [Fact]
    public async Task QueryAsync_WithSkipAndTake_ReturnsTheRequestedPage()
    {
        var page = await IdsAsync(DocumentQuery<Widget>.All().OrderBy("$.Quantity").Skip(1).Take(2));

        Assert.Equal("w2,w3", page);
    }

    [Fact]
    public async Task QueryAsync_WithSkipAndNoTake_ReturnsTheRemainder()
    {
        // Exercises the "LIMIT -1 OFFSET n" path — SQLite rejects a bare OFFSET.
        var page = await IdsAsync(DocumentQuery<Widget>.All().OrderBy("$.Quantity").Skip(3));

        Assert.Equal("w4,w5", page);
    }

    [Fact]
    public async Task QueryAsync_WithAll_ReturnsEveryDocument()
    {
        var matches = await SortedIdsAsync(DocumentQuery<Widget>.All());

        Assert.Equal("w1,w2,w3,w4,w5", matches);
    }

    [Fact]
    public async Task CountAsync_WithAFilter_MatchesTheNumberOfQueriedDocuments()
    {
        DocumentQuery<Widget>[] queries =
        [
            DocumentQuery<Widget>.All(),
            DocumentQuery<Widget>.Where("$.Quantity", QueryOperator.GreaterThan, 20),
            DocumentQuery<Widget>.WhereIsNull("$.Nickname"),
            DocumentQuery<Widget>.WhereArrayContains("$.Tags", "metal"),
            DocumentQuery<Widget>.Where("$.Name", QueryOperator.Equal, "Nothing at all")
        ];

        foreach (var query in queries)
        {
            var documents = await _store.QueryAsync(query);
            Assert.Equal(documents.Count(), await _store.CountAsync(query));
        }

        Assert.Equal(0, await _store.CountAsync(queries[^1]));
    }

    [Fact]
    public async Task QueryAsync_WithANestedPath_MatchesTheNestedValue()
    {
        var matches = await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Address.City", QueryOperator.Equal, "Cairo"));

        Assert.Equal("w3", matches);
    }

    [Fact]
    public async Task QueryAsync_WithAnArrayIndexPath_MatchesTheIndexedElement()
    {
        var matches = await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Tags[0]", QueryOperator.Equal, "metal"));

        // Only w2 has "metal" first; w1 has it at index 1.
        Assert.Equal("w2", matches);
    }

    [Fact]
    public async Task QueryAsync_ComparingADateTime_MatchesTheSameDocumentAsItsSerializedText()
    {
        var asDateTime = DocumentQuery<Widget>.Where(
            "$.CreatedAt", QueryOperator.Equal, new DateTime(2024, 3, 1));
        var asText = DocumentQuery<Widget>.Where(
            "$.CreatedAt", QueryOperator.Equal, "2024-03-01T00:00:00");

        // Microsoft.Data.Sqlite would bind the DateTime as "2024-03-01 00:00:00" while
        // System.Text.Json wrote "2024-03-01T00:00:00"; the builder normalizes it to the latter.
        Assert.Equal("w3", await IdsAsync(asDateTime));
        Assert.Equal("w3", await IdsAsync(asText));
    }

    [Fact]
    public async Task QueryAsync_RangingOverDateTimes_OrdersAndFiltersOnTheIsoText()
    {
        // ISO-8601 sorts lexicographically, so > and < over the normalized text are real ranges.
        var after = DocumentQuery<Widget>
            .Where("$.CreatedAt", QueryOperator.GreaterThan, new DateTime(2024, 2, 1))
            .OrderBy("$.CreatedAt");
        var window = DocumentQuery<Widget>
            .Where("$.CreatedAt", QueryOperator.GreaterThanOrEqual, new DateTime(2024, 2, 1))
            .And("$.CreatedAt", QueryOperator.LessThan, new DateTime(2024, 4, 1))
            .OrderBy("$.CreatedAt", descending: true);

        Assert.Equal("w3,w4,w5", await IdsAsync(after));
        Assert.Equal("w3,w2", await IdsAsync(window));
        Assert.Equal(3, await _store.CountAsync(after));
    }

    [Fact]
    public async Task QueryAsync_InsideATransaction_SeesUncommittedWritesUntilRollback()
    {
        var query = DocumentQuery<Widget>.Where("$.Name", QueryOperator.Equal, "Zeta");
        var pending = new Widget(
            "w9", "Zeta", 90, "Zed", new DateTime(2024, 9, 1), ["new"], new WidgetAddress("Zurich"));

        await using (var transaction = await _store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync(pending.Id, pending);

            Assert.Equal("w9", Join(await transaction.QueryAsync(query)));
            Assert.Equal(1, await transaction.CountAsync(query));

            await transaction.RollbackAsync();
        }

        Assert.Equal(string.Empty, await IdsAsync(query));
        Assert.Equal(0, await _store.CountAsync(query));
    }

    [Fact]
    public async Task QueryAsync_WithAnAlreadyCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.QueryAsync(DocumentQuery<Widget>.All(), cts.Token));
    }

    [Fact]
    public async Task CountAsync_WithAnAlreadyCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.CountAsync(DocumentQuery<Widget>.All(), cts.Token));
    }

    [Fact]
    public async Task QueryAsync_WithAnIndexedPath_StillMatchesOnTheIndexedExpression()
    {
        await _store.CreateIndexAsync<Widget>(w => w.Name);

        var query = DocumentQuery<Widget>.Where("$.Name", QueryOperator.Equal, "Anvil");
        var generated = SqlGenerator.GenerateQuerySql(
            _store.GetTableName<Widget>(),
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

        // A bound path would not match the index expression and would scan instead.
        Assert.Contains("idx_", plan, StringComparison.Ordinal);
        Assert.Equal("w1", await IdsAsync(query));
    }

    [Fact]
    public async Task DocumentQuery_WithAnInjectedJsonPath_ThrowsAndLeavesTheStoreIntact()
    {
        Assert.Throws<ArgumentException>(() =>
            DocumentQuery<Widget>.Where("$.Name') = 'x' OR 1=1 --", QueryOperator.Equal, "x"));
        Assert.Throws<ArgumentException>(() =>
            DocumentQuery<Widget>.All().OrderBy("$.Name'); DROP TABLE [Widget]; --"));

        Assert.Equal(5, await _store.CountAsync<Widget>());
        Assert.Equal("w3", await SortedIdsAsync(
            DocumentQuery<Widget>.Where("$.Name", QueryOperator.Equal, "Cog")));
    }
}

/// <summary>
/// One real <see cref="QueryOperator.Equal"/> round trip per CLR type whose ADO binding differs
/// from what System.Text.Json wrote into the document. Each of these silently matched nothing
/// before the builder normalized the bound value.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DocumentQueryValueBindingIntegrationTests : IAsyncLifetime
{
    private sealed record Reading(
        string Id,
        decimal Price,
        float Ratio,
        ulong Huge,
        DateTime Unspecified,
        DateTime Utc,
        DateTime Local,
        DateTimeOffset Occurred,
        Guid Owner,
        byte[] Payload);

    private static readonly Reading Row = new(
        "r1",
        3.75m,
        1f / 3f,
        ulong.MaxValue,
        new DateTime(2024, 3, 1, 4, 5, 6, DateTimeKind.Unspecified),
        new DateTime(2024, 3, 1, 4, 5, 6, 123, DateTimeKind.Utc),
        new DateTime(2024, 3, 1, 4, 5, 6, DateTimeKind.Local),
        new DateTimeOffset(2024, 3, 1, 4, 5, 6, 123, TimeSpan.FromHours(-3)),
        Guid.Parse("11112222-3333-4444-5555-666677778888"),
        [1, 2, 3, 250]);

    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await _store.CreateTableAsync<Reading>();
        await _store.UpsertAsync(Row.Id, Row);
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private async Task AssertMatchesAsync(string jsonPath, object value)
    {
        var structured = await _store.QueryAsync(
            DocumentQuery<Reading>.Where(jsonPath, QueryOperator.Equal, value));
        var byPathAndValue = await _store.QueryAsync<Reading, object>(jsonPath, value);

        Assert.Equal("r1", Assert.Single(structured).Id);
        Assert.Equal("r1", Assert.Single(byPathAndValue).Id);
    }

    [Fact]
    public Task QueryAsync_ComparingADecimal_MatchesTheStoredNumber() =>
        AssertMatchesAsync("$.Price", Row.Price);

    [Fact]
    public Task QueryAsync_ComparingAFloat_MatchesTheStoredNumber() =>
        AssertMatchesAsync("$.Ratio", Row.Ratio);

    [Fact]
    public Task QueryAsync_ComparingAULongAboveLongMaxValue_MatchesTheStoredNumber() =>
        AssertMatchesAsync("$.Huge", Row.Huge);

    [Fact]
    public Task QueryAsync_ComparingAnUnspecifiedDateTime_MatchesTheStoredText() =>
        AssertMatchesAsync("$.Unspecified", Row.Unspecified);

    [Fact]
    public Task QueryAsync_ComparingAUtcDateTime_MatchesTheStoredText() =>
        AssertMatchesAsync("$.Utc", Row.Utc);

    [Fact]
    public Task QueryAsync_ComparingALocalDateTime_MatchesTheStoredText() =>
        AssertMatchesAsync("$.Local", Row.Local);

    [Fact]
    public Task QueryAsync_ComparingADateTimeOffset_MatchesTheStoredText() =>
        AssertMatchesAsync("$.Occurred", Row.Occurred);

    [Fact]
    public Task QueryAsync_ComparingAGuid_MatchesTheStoredText() =>
        AssertMatchesAsync("$.Owner", Row.Owner);

    [Fact]
    public Task QueryAsync_ComparingAByteArray_MatchesTheStoredBase64() =>
        AssertMatchesAsync("$.Payload", Row.Payload);

    [Fact]
    public async Task QueryAsync_WithAnInListOfDateTimes_NormalizesEveryElement()
    {
        var matches = await _store.QueryAsync(DocumentQuery<Reading>.WhereIn(
            "$.Unspecified",
            [new DateTime(2020, 1, 1), Row.Unspecified]));

        Assert.Equal("r1", Assert.Single(matches).Id);
    }
}
