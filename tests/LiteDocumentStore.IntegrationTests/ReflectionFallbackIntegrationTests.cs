using System.Runtime.CompilerServices;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The default path: <c>SerializerOptions</c> left null, so the store builds its own
/// reflection-based fallback. <see cref="DocumentStoreOptions.Validate"/> now refuses that
/// combination when dynamic code is unsupported (Native AOT); on a JIT runtime it must keep
/// working end to end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReflectionFallbackIntegrationTests
{
    private sealed class Doc
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    [Fact]
    public async Task Store_WithNullSerializerOptions_RoundTripsThroughTheReflectionFallback()
    {
        Assert.True(RuntimeFeature.IsDynamicCodeSupported);

        var options = DocumentStoreOptions.ForInMemory();
        Assert.Null(options.SerializerOptions);

        await using var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc { Name = "Ada", Age = 36 });

        var read = await store.GetAsync<Doc>("a");

        Assert.NotNull(read);
        Assert.Equal("Ada", read.Name);
        Assert.Equal(36, read.Age);
    }
}
