using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The connectionless half of a nested collection of complex elements: what
/// <c>SerializeDocument</c> writes for an array of objects, and that
/// <c>DeserializeDocument</c> gives every element field back in order — including the empty
/// list, which must come back empty rather than null, and a <see cref="decimal"/> field, which
/// must come back exact.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NestedCollectionSerializationTests
{
    private sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice, bool BackOrdered);

    private sealed record Order(string Id, string Customer, List<OrderLine> Lines);

    private static readonly OrderLine[] ThreeLines =
    [
        new("SKU-1", 2, 19.99m, false),
        new("SKU-2", 1, 0.05m, true),
        new("SKU-3", 7, 1234567.89m, false)
    ];

    private static Task<IDocumentStore> CreateStoreAsync() =>
        new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());

    private static async Task<Order> RoundTripAsync(IDocumentStore store, Order order)
    {
        var json = System.Text.Encoding.UTF8.GetString(store.SerializeDocument(order));
        var roundTripped = store.DeserializeDocument<Order>(json);

        Assert.NotNull(roundTripped);
        return roundTripped;
    }

    [Fact]
    public async Task SerializeDocument_WithACollectionOfComplexElements_WritesAnArrayOfObjects()
    {
        await using var store = await CreateStoreAsync();
        var order = new Order("o1", "Ada", [ThreeLines[0]]);

        var json = System.Text.Encoding.UTF8.GetString(store.SerializeDocument(order));

        Assert.Equal(
            """{"Id":"o1","Customer":"Ada","Lines":[{"Sku":"SKU-1","Quantity":2,"UnitPrice":19.99,"BackOrdered":false}]}""",
            json);
    }

    [Fact]
    public async Task DeserializeDocument_WithACollectionOfComplexElements_ReturnsEveryElementFieldInOrder()
    {
        await using var store = await CreateStoreAsync();

        var roundTripped = await RoundTripAsync(store, new Order("o1", "Ada", [.. ThreeLines]));

        // The element records compare as whole values; the sequence comparison pins the order.
        Assert.Equal(ThreeLines, roundTripped.Lines);
    }

    [Fact]
    public async Task DeserializeDocument_WithAnEmptyCollection_ReturnsAnEmptyListRatherThanNull()
    {
        await using var store = await CreateStoreAsync();

        var roundTripped = await RoundTripAsync(store, new Order("o-empty", "Bob", []));

        Assert.NotNull(roundTripped.Lines);
        Assert.Empty(roundTripped.Lines);
    }

    [Fact]
    public async Task DeserializeDocument_WithASingleElementCollection_ReturnsThatElement()
    {
        await using var store = await CreateStoreAsync();

        var roundTripped = await RoundTripAsync(store, new Order("o-one", "Cleo", [ThreeLines[1]]));

        Assert.Equal(new[] { ThreeLines[1] }, roundTripped.Lines);
    }

    [Fact]
    public async Task DeserializeDocument_WithADecimalElementField_ReturnsTheExactValue()
    {
        await using var store = await CreateStoreAsync();

        var roundTripped = await RoundTripAsync(store, new Order("o1", "Ada", [.. ThreeLines]));

        Assert.Equal(0.05m, roundTripped.Lines[1].UnitPrice);
        Assert.Equal(1234567.89m, roundTripped.Lines[2].UnitPrice);
    }
}
