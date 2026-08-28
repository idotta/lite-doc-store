using System.Text.Json;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The presets, the builder's mutators and <see cref="DocumentStoreOptions.Clone"/> — the half of
/// the options surface that produces a configuration rather than rejecting one.
/// <c>OptionsValidationTests</c> owns the throws; the PRAGMAs these settings actually produce are
/// asserted against real SQLite in <c>OptionsPragmaIntegrationTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OptionsPresetTests
{
    private sealed class UpperCaseConvention : ITableNamingConvention
    {
        public string GetTableName<T>() => typeof(T).Name.ToUpperInvariant();

        public string GetTableName(Type type) => type.Name.ToUpperInvariant();
    }

    [Fact]
    public void ForFile_UsesTheFilePathAndWal()
    {
        var options = DocumentStoreOptions.ForFile("app.db");

        Assert.Equal("Data Source=app.db", options.ConnectionString);
        Assert.True(options.EnableWalMode);
        Assert.Equal(SynchronousMode.Normal, options.SynchronousMode);
    }

    [Fact]
    public void ForInMemory_IsSharedCacheAndUniquePerCall()
    {
        var first = DocumentStoreOptions.ForInMemory();
        var second = DocumentStoreOptions.ForInMemory();

        // Unique, because a pooled store opens several connections onto one database and two
        // stores built from this preset must not land on the same one.
        Assert.NotEqual(first.ConnectionString, second.ConnectionString);
        Assert.Contains("mode=memory", first.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("cache=shared", first.ConnectionString, StringComparison.Ordinal);
        Assert.False(first.EnableWalMode);
        Assert.Equal(SynchronousMode.Off, first.SynchronousMode);
    }

    [Fact]
    public void ForSharedInMemory_NamesTheCacheSoTwoStoresCanShareIt()
    {
        var first = DocumentStoreOptions.ForSharedInMemory("catalog");
        var second = DocumentStoreOptions.ForSharedInMemory("catalog");

        Assert.Equal(first.ConnectionString, second.ConnectionString);
        Assert.Contains("file:catalog?mode=memory&cache=shared", first.ConnectionString, StringComparison.Ordinal);
        Assert.False(first.EnableWalMode);
    }

    [Fact]
    public void ForSharedInMemory_WithNoName_DefaultsToShared()
    {
        Assert.Contains(
            "file:shared?mode=memory",
            DocumentStoreOptions.ForSharedInMemory().ConnectionString,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPreset_PassesValidation()
    {
        DocumentStoreOptions.ForFile("app.db").Validate();
        DocumentStoreOptions.ForInMemory().Validate();
        DocumentStoreOptions.ForSharedInMemory("named").Validate();
    }

    [Fact]
    public void Clone_CopiesEveryValue()
    {
        var convention = new UpperCaseConvention();
        var serializerOptions = new JsonSerializerOptions();
        var original = new DocumentStoreOptions("Data Source=app.db")
        {
            EnableWalMode = false,
            SynchronousMode = SynchronousMode.Full,
            PageSize = 8192,
            CacheSize = -4000,
            BusyTimeoutMs = 1234,
            EnableForeignKeys = false,
            MaxPoolSize = 7,
            PoolWaitTimeoutMs = 4321,
            TableNamingConvention = convention,
            AdditionalPragmas = ["PRAGMA temp_store = MEMORY"],
            SerializerOptions = serializerOptions
        };

        var clone = original.Clone();

        Assert.Equal(original.ConnectionString, clone.ConnectionString);
        Assert.Equal(original.EnableWalMode, clone.EnableWalMode);
        Assert.Equal(original.SynchronousMode, clone.SynchronousMode);
        Assert.Equal(original.PageSize, clone.PageSize);
        Assert.Equal(original.CacheSize, clone.CacheSize);
        Assert.Equal(original.BusyTimeoutMs, clone.BusyTimeoutMs);
        Assert.Equal(original.EnableForeignKeys, clone.EnableForeignKeys);
        Assert.Equal(original.MaxPoolSize, clone.MaxPoolSize);
        Assert.Equal(original.PoolWaitTimeoutMs, clone.PoolWaitTimeoutMs);
        Assert.Equal(original.AdditionalPragmas, clone.AdditionalPragmas);
    }

    [Fact]
    public void Clone_CopiesThePragmaListButSharesTheReferenceTypedOptions()
    {
        // The pragma list is mutable state a clone must not share; the naming convention and the
        // serializer options are configuration objects the caller owns, and copying them would
        // silently detach a clone from the resolver the caller registered.
        var convention = new UpperCaseConvention();
        var serializerOptions = new JsonSerializerOptions();
        var original = new DocumentStoreOptions("Data Source=app.db")
        {
            AdditionalPragmas = ["PRAGMA temp_store = MEMORY"],
            TableNamingConvention = convention,
            SerializerOptions = serializerOptions
        };

        var clone = original.Clone();
        clone.AdditionalPragmas.Add("PRAGMA mmap_size = 0");

        Assert.Single(original.AdditionalPragmas);
        Assert.Equal(2, clone.AdditionalPragmas.Count);
        Assert.Same(convention, clone.TableNamingConvention);
        Assert.Same(serializerOptions, clone.SerializerOptions);
    }

    [Fact]
    public void Builder_WithEveryMutator_ProducesTheStatedOptions()
    {
        var convention = new UpperCaseConvention();
        var serializerOptions = new JsonSerializerOptions();

        var options = DocumentStoreOptions.Builder()
            .UseFile("app.db")
            .WithWalMode()
            .WithSynchronousMode(SynchronousMode.Full)
            .WithPageSize(8192)
            .WithCacheSizeMb(4)
            .WithBusyTimeout(2500)
            .WithForeignKeys(false)
            .WithMaxPoolSize(5)
            .WithPoolWaitTimeout(7500)
            .WithTableNamingConvention(convention)
            .WithSerializerOptions(serializerOptions)
            .AddPragma("PRAGMA temp_store = MEMORY")
            .Build();

        Assert.Equal("Data Source=app.db", options.ConnectionString);
        Assert.True(options.EnableWalMode);
        Assert.Equal(SynchronousMode.Full, options.SynchronousMode);
        Assert.Equal(8192, options.PageSize);
        Assert.Equal(-4096, options.CacheSize);
        Assert.Equal(2500, options.BusyTimeoutMs);
        Assert.False(options.EnableForeignKeys);
        Assert.Equal(5, options.MaxPoolSize);
        Assert.Equal(7500, options.PoolWaitTimeoutMs);
        Assert.Same(convention, options.TableNamingConvention);
        Assert.Same(serializerOptions, options.SerializerOptions);
        Assert.Equal(["PRAGMA temp_store = MEMORY"], options.AdditionalPragmas);
    }

    [Fact]
    public void Builder_WithConnectionString_OverridesTheConstructorArgument()
    {
        var options = DocumentStoreOptions.Builder("Data Source=first.db")
            .WithConnectionString("Data Source=second.db")
            .Build();

        Assert.Equal("Data Source=second.db", options.ConnectionString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddPragma_WithABlankValue_IsIgnoredRatherThanStored(string? pragma)
    {
        // Validate() rejects a blank pragma, so dropping it here is what keeps Build() from
        // producing options the factory would then refuse.
        var options = DocumentStoreOptions.Builder("Data Source=app.db").AddPragma(pragma!).Build();

        Assert.Empty(options.AdditionalPragmas);
    }

    [Fact]
    public void AddPragma_CalledTwice_KeepsBothInOrder()
    {
        var options = DocumentStoreOptions.Builder("Data Source=app.db")
            .AddPragma("PRAGMA temp_store = MEMORY")
            .AddPragma("PRAGMA mmap_size = 268435456")
            .Build();

        Assert.Equal(["PRAGMA temp_store = MEMORY", "PRAGMA mmap_size = 268435456"], options.AdditionalPragmas);
    }

    [Fact]
    public void OptimizeForPerformance_SetsWalNormalAndTheLargerPageAndCache()
    {
        var options = DocumentStoreOptions.Builder("Data Source=app.db").OptimizeForPerformance().Build();

        Assert.True(options.EnableWalMode);
        Assert.Equal(SynchronousMode.Normal, options.SynchronousMode);
        Assert.Equal(8192, options.PageSize);
        Assert.Equal(-4000, options.CacheSize);
    }

    [Fact]
    public void OptimizeForSafety_SetsWalFullAndForeignKeys()
    {
        var options = DocumentStoreOptions.Builder("Data Source=app.db")
            .WithForeignKeys(false)
            .OptimizeForSafety()
            .Build();

        Assert.True(options.EnableWalMode);
        Assert.Equal(SynchronousMode.Full, options.SynchronousMode);
        Assert.True(options.EnableForeignKeys);
    }

    [Fact]
    public void OptimizeForTesting_ProducesAUniqueInMemoryDatabaseWithoutWal()
    {
        var first = DocumentStoreOptions.Builder().OptimizeForTesting().Build();
        var second = DocumentStoreOptions.Builder().OptimizeForTesting().Build();

        Assert.NotEqual(first.ConnectionString, second.ConnectionString);
        Assert.Contains("mode=memory", first.ConnectionString, StringComparison.Ordinal);
        Assert.False(first.EnableWalMode);
        Assert.Equal(SynchronousMode.Off, first.SynchronousMode);
        first.Validate();
    }

    [Fact]
    public void UseInMemory_AndUseSharedInMemory_TurnWalOffOnAnOptionsObjectThatHadIt()
    {
        var unique = DocumentStoreOptions.Builder("Data Source=app.db").WithWalMode().UseInMemory().Build();
        var shared = DocumentStoreOptions.Builder("Data Source=app.db").WithWalMode().UseSharedInMemory("c").Build();

        Assert.False(unique.EnableWalMode);
        Assert.False(shared.EnableWalMode);
        Assert.Contains("file:c?mode=memory&cache=shared", shared.ConnectionString, StringComparison.Ordinal);
    }
}
