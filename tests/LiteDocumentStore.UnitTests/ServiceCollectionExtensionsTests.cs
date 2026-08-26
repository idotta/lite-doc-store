using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The DI registration resolved from a real <see cref="ServiceProvider"/>: the singleton
/// lifetime, keyed stores, the <c>TryAdd</c> idempotence that makes a second registration a
/// no-op, and the null-argument throws.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServiceCollectionExtensionsTests
{
    private sealed record Doc(string Name, int Value);

    /// <summary>A distinct shared-cache in-memory database per call, so two stores never collide.</summary>
    private static DocumentStoreOptions Options() => DocumentStoreOptions.ForInMemory();

    [Fact]
    public void AddLiteDocumentStore_RegistersTheStoreAsASingleton()
    {
        var services = new ServiceCollection();

        services.AddLiteDocumentStore(Options());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IDocumentStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddLiteDocumentStore_ResolvesTheSameInstanceEveryTime()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(Options());

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<IDocumentStore>(),
            provider.GetRequiredService<IDocumentStore>());
    }

    [Fact]
    public void AddLiteDocumentStore_AlsoRegistersTheCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(Options());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IConnectionFactory>());
        Assert.NotNull(provider.GetRequiredService<ITableNamingConvention>());
        Assert.NotNull(provider.GetRequiredService<IDocumentStoreFactory>());
    }

    [Fact]
    public async Task AddLiteDocumentStore_WithAConfigureCallback_AppliesIt()
    {
        var services = new ServiceCollection();
        var template = Options();

        services.AddLiteDocumentStore(options =>
        {
            options.ConnectionString = template.ConnectionString;
            options.EnableWalMode = false;
            options.MaxPoolSize = 3;
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Doc>();
        await store.UpsertAsync("a", new Doc("a", 1));
        Assert.Equal(1, await store.CountAsync<Doc>());
    }

    [Fact]
    public void AddLiteDocumentStore_CalledTwice_KeepsTheFirstRegistration()
    {
        // TryAddSingleton, so the second call must not replace the first store — asserting the
        // resolved options rather than the descriptor count, because a replaced registration
        // would silently point every consumer at the second database.
        var services = new ServiceCollection();
        var firstName = $"lds-first-{Guid.NewGuid():N}";
        var secondName = $"lds-second-{Guid.NewGuid():N}";

        services.AddLiteDocumentStore(DocumentStoreOptions.ForSharedInMemory(firstName));
        services.AddLiteDocumentStore(DocumentStoreOptions.ForSharedInMemory(secondName));

        using var provider = services.BuildServiceProvider();
        var connectionString = ConnectionStringOf(provider.GetRequiredService<IDocumentStore>());

        Assert.Single(services, d => d.ServiceType == typeof(IDocumentStore));
        Assert.Contains(firstName, connectionString, StringComparison.Ordinal);
        Assert.DoesNotContain(secondName, connectionString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddKeyedLiteDocumentStore_ResolvesTwoIndependentStores()
    {
        var services = new ServiceCollection();
        services.AddKeyedLiteDocumentStore("left", Options());
        services.AddKeyedLiteDocumentStore("right", Options());

        await using var provider = services.BuildServiceProvider();
        var left = provider.GetRequiredKeyedService<IDocumentStore>("left");
        var right = provider.GetRequiredKeyedService<IDocumentStore>("right");

        Assert.NotSame(left, right);

        await left.CreateTableAsync<Doc>();
        await right.CreateTableAsync<Doc>();
        await left.UpsertAsync("only-on-the-left", new Doc("a", 1));

        Assert.Equal(1, await left.CountAsync<Doc>());
        Assert.Equal(0, await right.CountAsync<Doc>());
    }

    [Fact]
    public void AddKeyedLiteDocumentStore_LeavesTheUnkeyedRegistrationUnresolvable()
    {
        var services = new ServiceCollection();
        services.AddKeyedLiteDocumentStore("only-keyed", Options());

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IDocumentStore>());
    }

    [Fact]
    public void AddKeyedLiteDocumentStore_WithAConfigureCallback_Registers()
    {
        var services = new ServiceCollection();
        var template = Options();

        // The callback starts from a default DocumentStoreOptions, which asks for WAL — so an
        // in-memory connection string has to turn it off, exactly as the presets do.
        services.AddKeyedLiteDocumentStore("keyed", options =>
        {
            options.ConnectionString = template.ConnectionString;
            options.EnableWalMode = false;
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<IDocumentStore>("keyed"));
    }

    [Fact]
    public async Task BuildServiceProvider_WhenDisposed_DisposesTheSingletonStore()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(Options());

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();
        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CountAsync<Doc>());
    }

    [Fact]
    public void AddLiteDocumentStore_WithNullArguments_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddLiteDocumentStore(Options()));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddLiteDocumentStore((DocumentStoreOptions)null!));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddLiteDocumentStore((Action<DocumentStoreOptions>)null!));
    }

    [Fact]
    public void AddKeyedLiteDocumentStore_WithNullArguments_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddKeyedLiteDocumentStore("k", Options()));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddKeyedLiteDocumentStore(null!, Options()));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddKeyedLiteDocumentStore("k", (DocumentStoreOptions)null!));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddKeyedLiteDocumentStore("k", (Action<DocumentStoreOptions>)null!));
    }

    private static string ConnectionStringOf(IDocumentStore store) =>
        store.ExecuteRawAsync((connection, _) => Task.FromResult(connection.ConnectionString))
            .GetAwaiter()
            .GetResult();
}
