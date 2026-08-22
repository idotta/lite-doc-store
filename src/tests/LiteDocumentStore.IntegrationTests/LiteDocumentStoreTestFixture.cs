using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Test fixture for easily setting up DocumentStore instances in tests.
/// Implements IAsyncLifetime for proper async setup and cleanup.
/// </summary>
public class LiteDocumentStoreTestFixture : IAsyncLifetime
{
    private readonly List<string> _testDbPaths = new();
    private readonly List<IDocumentStore> _stores = new();

    /// <summary>
    /// Gets the default DocumentStore instance for tests.
    /// </summary>
    public IDocumentStore Store { get; private set; } = null!;

    /// <summary>
    /// Gets the connection string used for the default store.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the file path of the default test database.
    /// </summary>
    public string TestDbPath { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes the test fixture with a default in-memory database.
    /// </summary>
    public async Task InitializeAsync()
    {
        Store = await CreateInMemoryStoreAsync();
    }

    /// <summary>
    /// Cleans up all resources created during tests.
    /// </summary>
    public async Task DisposeAsync()
    {
        // Dispose all stores
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
        _stores.Clear();

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Delete all test database files
        foreach (var path in _testDbPaths)
        {
            var files = new[] { path, $"{path}-wal", $"{path}-shm" };
            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // Sometimes files are still locked, ignore
                    }
                }
            }
        }
        _testDbPaths.Clear();
    }

    /// <summary>
    /// Creates a new in-memory DocumentStore instance.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="DocumentStoreOptions.ForInMemory"/>: a uniquely named shared-cache
    /// in-memory database, private to this store but reachable from every connection in its
    /// pool. A plain <c>Data Source=:memory:</c> is rejected by the store.
    /// </remarks>
    public async Task<IDocumentStore> CreateInMemoryStoreAsync()
    {
        return await CreateStoreAsync(DocumentStoreOptions.ForInMemory());
    }

    /// <summary>
    /// Creates a new file-based DocumentStore instance with optional WAL mode.
    /// </summary>
    public async Task<IDocumentStore> CreateFileStoreAsync(bool enableWal = false)
    {
        var testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _testDbPaths.Add(testDbPath);

        var options = DocumentStoreOptions.ForFile(testDbPath);
        options.EnableWalMode = enableWal;

        return await CreateStoreAsync(options);
    }

    /// <summary>
    /// Creates a new shared in-memory DocumentStore instance.
    /// Useful for testing multiple connections to the same in-memory database.
    /// </summary>
    public async Task<IDocumentStore> CreateSharedInMemoryStoreAsync(string sharedName = "testdb")
    {
        return await CreateStoreAsync(DocumentStoreOptions.ForSharedInMemory(sharedName));
    }

    /// <summary>
    /// Creates a DocumentStore with custom options.
    /// </summary>
    public async Task<IDocumentStore> CreateStoreAsync(DocumentStoreOptions options)
    {
        var factory = new DocumentStoreFactory();
        var store = await factory.CreateAsync(options);
        _stores.Add(store);

        return store;
    }

    /// <summary>
    /// Creates a DocumentStore with custom configuration via builder.
    /// </summary>
    public async Task<IDocumentStore> CreateStoreAsync(Action<DocumentStoreOptionsBuilder> configure)
    {
        var builder = new DocumentStoreOptionsBuilder();
        configure(builder);
        var options = builder.Build();

        return await CreateStoreAsync(options);
    }
}

/// <summary>
/// Collection fixture for sharing a single test fixture across multiple test classes.
/// </summary>
[CollectionDefinition(nameof(LiteDocumentStoreCollection))]
public class LiteDocumentStoreCollection : ICollectionFixture<LiteDocumentStoreTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

/// <summary>
/// Example test class demonstrating fixture usage.
/// </summary>
[Collection(nameof(LiteDocumentStoreCollection))]
public class ExampleTestsUsingFixture
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public ExampleTestsUsingFixture(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExampleTest_UsingDefaultStore()
    {
        // Arrange
        var store = _fixture.Store;
        await store.CreateTableAsync<TestDocument>();

        // Act
        await store.UpsertAsync("doc-1", new TestDocument { Title = "Test", Content = "Example" });
        var retrieved = await store.GetAsync<TestDocument>("doc-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test", retrieved.Title);
    }

    [Fact]
    public async Task ExampleTest_UsingCustomStore()
    {
        // Arrange - Create a file-based store with WAL enabled
        var store = await _fixture.CreateFileStoreAsync(enableWal: true);
        await store.CreateTableAsync<TestDocument>();

        // Act
        await store.UpsertAsync("doc-1", new TestDocument { Title = "Test", Content = "Example" });
        var retrieved = await store.GetAsync<TestDocument>("doc-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test", retrieved.Title);
    }
}

public class TestDocument
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
