using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// A query or patch value is bound in the shape the store's serializer wrote at its path, so a
/// converter — a string enum, a custom date format — no longer makes a query silently match
/// nothing or a patch write a shape the serializer never would.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValueBindingIntegrationTests
{
    private static async Task<IDocumentStore> CreateStoreAsync(Action<JsonSerializerOptions>? configure = null)
    {
        var serializerOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        configure?.Invoke(serializerOptions);

        var options = DocumentStoreOptions.ForInMemory();
        options.SerializerOptions = serializerOptions;
        var store = await new DocumentStoreFactory().CreateAsync(options);

        await store.CreateTableAsync<Shipment>();
        await store.UpsertAsync("s1", new Shipment { Id = "s1", Status = ShipmentStatus.Pending, Tags = [ShipmentStatus.Pending] });
        await store.UpsertAsync("s2", new Shipment { Id = "s2", Status = ShipmentStatus.Active, Tags = [ShipmentStatus.Active] });
        await store.UpsertAsync("s3", new Shipment { Id = "s3", Status = ShipmentStatus.Closed, Tags = [ShipmentStatus.Active, ShipmentStatus.Closed] });
        return store;
    }

    private static Task<IDocumentStore> CreateStringEnumStoreAsync() =>
        CreateStoreAsync(o => o.Converters.Add(new JsonStringEnumConverter()));

    private static async Task<string> RawStatusAsync(IDocumentStore store, string id)
    {
        var table = store.GetTableName<Shipment>();
        return await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT json_extract(data, '$.Status') FROM [{table}] WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToString(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture)!;
        });
    }

    private static string[] Ids(IEnumerable<Shipment> shipments) =>
        [.. shipments.Select(s => s.Id).Order()];

    [Fact]
    public async Task QueryAsync_WithAStringEnumOnTheSimpleOverload_MatchesTheStoredName()
    {
        await using var store = await CreateStringEnumStoreAsync();

        Assert.Equal(["s2"], Ids(await store.QueryAsync<Shipment, ShipmentStatus>("$.Status", ShipmentStatus.Active)));
    }

    [Fact]
    public async Task DocumentQuery_WithStringEnums_MatchesEqualInAndArrayContains()
    {
        await using var store = await CreateStringEnumStoreAsync();

        var equal = DocumentQuery<Shipment>.Where("$.Status", QueryOperator.Equal, ShipmentStatus.Active);
        var notEqual = DocumentQuery<Shipment>.Where("$.Status", QueryOperator.NotEqual, ShipmentStatus.Active);
        var any = DocumentQuery<Shipment>.WhereIn("$.Status", [ShipmentStatus.Pending, ShipmentStatus.Closed]);
        var contains = DocumentQuery<Shipment>.WhereArrayContains("$.Tags", ShipmentStatus.Active);

        Assert.Equal(["s2"], Ids(await store.QueryAsync(equal)));
        Assert.Equal(["s1", "s3"], Ids(await store.QueryAsync(notEqual)));
        Assert.Equal(["s1", "s3"], Ids(await store.QueryAsync(any)));
        Assert.Equal(["s2", "s3"], Ids(await store.QueryAsync(contains)));
        Assert.Equal(1, await store.CountAsync(equal));
        Assert.True(await store.ExistsAsync(equal));
        Assert.Equal(2, await store.DeleteAsync(any));
        Assert.Equal(1, await store.CountAsync<Shipment>());
    }

    [Fact]
    public async Task DocumentQuery_WithDefaultOptions_MatchesAndRangesOverNumericEnums()
    {
        await using var store = await CreateStoreAsync();

        var equal = DocumentQuery<Shipment>.Where("$.Status", QueryOperator.Equal, ShipmentStatus.Closed);
        var atLeast = DocumentQuery<Shipment>.Where("$.Status", QueryOperator.GreaterThanOrEqual, ShipmentStatus.Active);

        Assert.Equal(["s3"], Ids(await store.QueryAsync(equal)));
        Assert.Equal(["s2", "s3"], Ids(await store.QueryAsync(atLeast)));
    }

    [Fact]
    public async Task DocumentQuery_RangingOverAStringEnum_ThrowsAgainstTheQuery()
    {
        await using var store = await CreateStringEnumStoreAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(
            DocumentQuery<Shipment>.Where("$.Status", QueryOperator.GreaterThan, ShipmentStatus.Pending)));

        Assert.Equal("query", exception.ParamName);
    }

    [Fact]
    public async Task PatchAsync_WithAStringEnum_WritesTheNameAndStillMatchesAQuery()
    {
        await using var store = await CreateStringEnumStoreAsync();

        await store.PatchAsync("s1", DocumentPatch<Shipment>.Set("$.Status", ShipmentStatus.Closed));

        Assert.Equal("Closed", await RawStatusAsync(store, "s1"));
        Assert.Equal(ShipmentStatus.Closed, (await store.GetAsync<Shipment>("s1"))!.Status);
        Assert.Equal(["s1", "s3"], Ids(await store.QueryAsync<Shipment, ShipmentStatus>("$.Status", ShipmentStatus.Closed)));
    }

    [Fact]
    public async Task QueryAndPatch_ThroughAPropertyLevelConverter_UseTheConvertersShape()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        await using var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Delivery>();

        var at = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        await store.UpsertAsync("d1", new Delivery { Status = ShipmentStatus.Active, At = at });

        Assert.Single(await store.QueryAsync<Delivery, ShipmentStatus>("$.Status", ShipmentStatus.Active));
        Assert.Single(await store.QueryAsync<Delivery, DateTime>("$.At", at));

        var later = at.AddHours(1);
        await store.PatchAsync("d1", DocumentPatch<Delivery>.Set("$.At", later).AndSet("$.Status", ShipmentStatus.Closed));

        var patched = (await store.GetAsync<Delivery>("d1"))!;
        Assert.Equal(later, patched.At);
        Assert.Equal(ShipmentStatus.Closed, patched.Status);
        Assert.Single(await store.QueryAsync(DocumentQuery<Delivery>.Where("$.At", QueryOperator.Equal, later)));
    }

    [Fact]
    public async Task QueryAsync_UnderACamelCasePolicy_MatchesOnTheSerializedPath()
    {
        await using var store = await CreateStoreAsync(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        Assert.Equal(["s2"], Ids(await store.QueryAsync<Shipment, ShipmentStatus>("$.status", ShipmentStatus.Active)));
    }

    [Fact]
    public async Task EnumOnAnUnresolvablePath_IsRefusedAgainstEachCallersParameter()
    {
        await using var store = await CreateStringEnumStoreAsync();

        var simple = await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync<Shipment, ShipmentStatus>("$.Missing", ShipmentStatus.Active));
        var query = await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(
            DocumentQuery<Shipment>.Where("$.Missing", QueryOperator.Equal, ShipmentStatus.Active)));
        var patch = await Assert.ThrowsAsync<ArgumentException>(
            () => store.PatchAsync("s1", DocumentPatch<Shipment>.Set("$.Missing", ShipmentStatus.Active)));

        Assert.Equal("value", simple.ParamName);
        Assert.Equal("query", query.ParamName);
        Assert.Equal("patch", patch.ParamName);
        Assert.Equal("Pending", await RawStatusAsync(store, "s1"));
    }

    [Fact]
    public async Task Transaction_BindsThroughTheSameMetadataAsTheStore()
    {
        await using var store = await CreateStringEnumStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();

        await transaction.PatchAsync("s1", DocumentPatch<Shipment>.Set("$.Status", ShipmentStatus.Active));
        var active = await transaction.QueryAsync(
            DocumentQuery<Shipment>.Where("$.Status", QueryOperator.Equal, ShipmentStatus.Active));
        await transaction.CommitAsync();

        Assert.Equal(["s1", "s2"], Ids(active));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task QueryAsync_WithANonFiniteValue_ThrowsOnStoreAndTransaction(double value)
    {
        await using var store = await CreateStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();

        var onStore = await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync<Shipment, double>("$.Weight", value));
        var onTransaction = await Assert.ThrowsAsync<ArgumentException>(
            () => transaction.QueryAsync<Shipment, double>("$.Weight", value));

        Assert.Equal("value", onStore.ParamName);
        Assert.Equal(onStore.Message, onTransaction.Message);
    }

    private enum ShipmentStatus { Pending, Active, Closed }

    private sealed class Shipment
    {
        public string Id { get; set; } = "";
        public ShipmentStatus Status { get; set; }
        public ShipmentStatus[] Tags { get; set; } = [];
        public double Weight { get; set; }
    }

    private sealed class Delivery
    {
        [JsonConverter(typeof(JsonStringEnumConverter<ShipmentStatus>))]
        public ShipmentStatus Status { get; set; }

        [JsonConverter(typeof(EpochMillisConverter))]
        public DateTime At { get; set; }
    }

    private sealed class EpochMillisConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64()).UtcDateTime;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds());
    }
}
