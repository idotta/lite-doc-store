using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Round-tripping a document whose property is a collection of <em>complex</em> elements. The
/// suite covered the two halves separately — a <c>string[]</c> of scalars and a single nested
/// object — but never an array of objects: every element field, in order, the empty and
/// single-element cases, and an element's field reached by JSON path.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public sealed class NestedCollectionIntegrationTests
{
    private sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice, bool BackOrdered);

    private sealed record Order(string Id, string Customer, List<OrderLine> Lines);

    private static readonly OrderLine[] ThreeLines =
    [
        new("SKU-1", 2, 19.99m, false),
        new("SKU-2", 1, 0.05m, true),
        new("SKU-3", 7, 1234567.89m, false)
    ];

    private readonly LiteDocumentStoreTestFixture _fixture;

    public NestedCollectionIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<Order>();
        return store;
    }

    private static async Task<Order> RoundTripAsync(IDocumentStore store, Order order)
    {
        await store.UpsertAsync(order.Id, order);
        var found = await store.GetAsync<Order>(order.Id);

        Assert.NotNull(found);
        return found;
    }

    [Fact]
    public async Task GetAsync_WithACollectionOfComplexElements_ReturnsEveryElementFieldInOrder()
    {
        var store = await CreateStoreWithTableAsync();
        var order = new Order("o1", "Ada", [.. ThreeLines]);

        var found = await RoundTripAsync(store, order);

        // A record with a List<T> member compares that member by reference, so the document's
        // scalars and the element sequence are asserted separately — each element is still
        // compared as a whole value, and the sequence comparison pins the order.
        Assert.Equal(order.Id, found.Id);
        Assert.Equal(order.Customer, found.Customer);
        Assert.Equal(ThreeLines, found.Lines);
    }

    [Fact]
    public async Task GetAsync_WithAnEmptyCollection_ReturnsAnEmptyListRatherThanNull()
    {
        var store = await CreateStoreWithTableAsync();

        var found = await RoundTripAsync(store, new Order("o-empty", "Bob", []));

        Assert.NotNull(found.Lines);
        Assert.Empty(found.Lines);
    }

    [Fact]
    public async Task GetAsync_WithASingleElementCollection_ReturnsThatElement()
    {
        var store = await CreateStoreWithTableAsync();

        var found = await RoundTripAsync(store, new Order("o-one", "Cleo", [ThreeLines[1]]));

        Assert.Equal(new[] { ThreeLines[1] }, found.Lines);
    }

    [Fact]
    public async Task GetAsync_WithADecimalElementField_ReturnsTheExactStoredValue()
    {
        var store = await CreateStoreWithTableAsync();

        var found = await RoundTripAsync(store, new Order("o1", "Ada", [.. ThreeLines]));

        // Exact, not approximate: a decimal that lost precision would still be "close".
        Assert.Equal(0.05m, found.Lines[1].UnitPrice);
        Assert.Equal(1234567.89m, found.Lines[2].UnitPrice);
    }

    [Fact]
    public async Task QueryAsync_ByAPathIntoTheNestedArray_MatchesTheElementField()
    {
        var store = await CreateStoreWithTableAsync();
        await store.UpsertAsync("o1", new Order("o1", "Ada", [.. ThreeLines]));
        await store.UpsertAsync("o2", new Order("o2", "Bob", [new OrderLine("SKU-9", 1, 5m, false)]));

        var bySku = await store.QueryAsync<Order, string>("$.Lines[0].Sku", "SKU-1");
        var byPrice = await store.QueryAsync(
            DocumentQuery<Order>.Where("$.Lines[2].UnitPrice", QueryOperator.Equal, 1234567.89m));

        Assert.Equal("o1", Assert.Single(bySku).Id);
        Assert.Equal("o1", Assert.Single(byPrice).Id);
    }
}
