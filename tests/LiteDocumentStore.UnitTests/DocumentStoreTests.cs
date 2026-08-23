using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for store construction, lifetime and health. The store owns an internal
/// connection pool, so it is built through <see cref="DocumentStoreFactory"/> (or the
/// internal constructor plus <c>Initialize</c>) and the underlying connection is reached
/// only through <c>ExecuteRawAsync</c>.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentStoreTests : IDisposable
{
    private readonly string _testDbPath;

    public DocumentStoreTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
    }

    private DocumentStoreOptions FileOptions() => new() { ConnectionString = $"Data Source={_testDbPath}" };

    private static Task<IDocumentStore> CreateStoreAsync(DocumentStoreOptions options) =>
        new DocumentStoreFactory().CreateAsync(options);

    [Fact]
    public void Constructor_WithOptionsAndConnectionFactory_CreatesInitializedStore()
    {
        // Arrange & Act
        using var store = new DocumentStore(FileOptions(), new DefaultConnectionFactory());
        store.Initialize();

        // Assert - the store owns its pool; initialization opens the first connection
        Assert.NotNull(store);
        Assert.True(store.MaxPoolSize >= 2);
        Assert.Equal(1, store.OpenConnectionCount);
    }

    [Fact]
    public void Constructor_WithNullArguments_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DocumentStore(null!, new DefaultConnectionFactory()));

        Assert.Throws<ArgumentNullException>(() =>
            new DocumentStore(FileOptions(), null!));
    }

    [Fact]
    public void Constructor_WithPrivateInMemoryConnectionString_ThrowsArgumentException()
    {
        // Arrange - every pooled connection would get its own empty private database
        var options = new DocumentStoreOptions { ConnectionString = "Data Source=:memory:" };

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            new DocumentStore(options, new DefaultConnectionFactory()));

        Assert.Contains(nameof(DocumentStoreOptions.ForInMemory), ex.Message);
    }

    [Fact]
    public async Task ForInMemory_SharesOneDatabaseAcrossPooledConnections()
    {
        // Arrange - each operation rents its own connection, so the writes below are only
        // visible to the reads because ForInMemory uses a named shared-cache database
        await using var store = await CreateStoreAsync(DocumentStoreOptions.ForInMemory());

        // Act
        await store.CreateTableAsync<TestPerson>();
        await store.UpsertAsync("person-1", new TestPerson { Name = "Test Person", Age = 25 });
        var loaded = await store.GetAsync<TestPerson>("person-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("Test Person", loaded.Name);
        Assert.Equal(25, loaded.Age);
    }

    [Fact]
    public async Task GetTableName_ReturnsTypeName()
    {
        // This tests the interaction indirectly through CreateTableAsync
        await using var store = await CreateStoreAsync(FileOptions());

        // Act - create table should use type name
        await store.CreateTableAsync<TestPerson>();

        // Assert - verify table exists with correct name
        var checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='TestPerson'";
        var result = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(checkSql, ct));
        Assert.Equal("TestPerson", result);
    }

    [Fact]
    public async Task ExecuteRawAsync_WithVoidOverload_RunsSqlOnStoreDatabase()
    {
        // Arrange
        await using var store = await CreateStoreAsync(FileOptions());

        // Act - the escape hatch: plain relational SQL in the same database
        await store.ExecuteRawAsync((connection, ct) =>
            connection.ExecuteAsync("CREATE TABLE Relational (id INTEGER PRIMARY KEY, label TEXT)", ct));

        await store.ExecuteRawAsync((connection, ct) =>
            connection.ExecuteAsync("INSERT INTO Relational (id, label) VALUES (1, 'first')", ct));

        // Assert - a later rent sees the committed writes
        var label = await store.ExecuteRawAsync((connection, ct) =>
            connection.QueryFirstStringAsync("SELECT label FROM Relational WHERE id = 1", ct));

        Assert.Equal("first", label);
    }

    [Fact]
    public async Task DisposeAsync_ClosesPooledConnectionsAndReleasesDatabaseFile()
    {
        // Arrange
        var store = await CreateStoreAsync(FileOptions());
        await store.CreateTableAsync<TestPerson>();
        Assert.True(File.Exists(_testDbPath));

        // Act
        await store.DisposeAsync();

        // Assert - the store owns its pool, so disposal releases every handle on the file
        File.Delete(_testDbPath);
        Assert.False(File.Exists(_testDbPath));
    }

    [Fact]
    public async Task Operations_OnDisposedStore_ThrowObjectDisposedException()
    {
        // Arrange
        var store = await CreateStoreAsync(FileOptions());
        await store.DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.CreateTableAsync<TestPerson>());

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.UpsertAsync("test-id", new TestPerson { Name = "Test" }));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.GetAsync<TestPerson>("test-id"));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.GetAllAsync<TestPerson>());

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.DeleteAsync<TestPerson>("test-id"));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync("SELECT 1", ct)));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.BeginTransactionAsync());
    }

    [Fact]
    public async Task Dispose_CalledRepeatedly_IsIdempotent()
    {
        // Arrange
        var store = await CreateStoreAsync(FileOptions());

        // Act & Assert - no throw on any combination of repeated disposal
        store.Dispose();
        store.Dispose();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task IsHealthyAsync_OnInitializedStore_ReturnsTrue()
    {
        // Arrange
        await using var store = await CreateStoreAsync(FileOptions());

        // Act
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.True(isHealthy);
    }

    [Fact]
    public async Task IsHealthyAsync_WithUnreachableDatabase_ReturnsFalse()
    {
        // Arrange - a database file inside a directory that does not exist; the pool cannot
        // open a connection for the health probe
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}");
        var options = new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={Path.Combine(missingDirectory, "test.db")}"
        };

        await using var store = new DocumentStore(options, new DefaultConnectionFactory());

        // Act
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.False(isHealthy);
        Assert.False(Directory.Exists(missingDirectory));
    }

    [Fact]
    public async Task IsHealthyAsync_OnDisposedStore_ReturnsFalse()
    {
        // Arrange
        var store = await CreateStoreAsync(FileOptions());

        // Act
        await store.DisposeAsync();
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.False(isHealthy);
    }

    [Fact]
    public async Task ConcurrentUpsert_AllOperationsComplete()
    {
        // Arrange
        await using var store = await CreateStoreAsync(FileOptions());
        await store.CreateTableAsync<TestPerson>();

        // Act - Perform multiple concurrent upserts
        var tasks = Enumerable.Range(0, 10).Select(i =>
            store.UpsertAsync($"person-{i}", new TestPerson
            {
                Name = $"Person {i}",
                Age = 20 + i,
                Email = $"person{i}@example.com"
            })
        );

        await Task.WhenAll(tasks);

        // Assert - All records should be inserted
        var count = await store.CountAsync<TestPerson>();
        Assert.Equal(10, count);
    }

    [Fact]
    public async Task ConcurrentRead_AllOperationsSucceed()
    {
        // Arrange
        await using var store = await CreateStoreAsync(FileOptions());
        await store.CreateTableAsync<TestPerson>();

        // Insert test data
        await store.UpsertAsync("person-1", new TestPerson
        {
            Name = "Test Person",
            Age = 25,
            Email = "test@example.com"
        });

        // Act - Perform multiple concurrent reads
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            store.GetAsync<TestPerson>("person-1")
        );

        var results = await Task.WhenAll(tasks);

        // Assert - All reads should succeed and return the same data
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal("Test Person", result.Name);
            Assert.Equal(25, result.Age);
        });
    }

    [Fact]
    public async Task ConcurrentMixedOperations_AllOperationsComplete()
    {
        // Arrange
        await using var store = await CreateStoreAsync(FileOptions());
        await store.CreateTableAsync<TestPerson>();

        // Insert initial data
        await store.UpsertAsync("person-0", new TestPerson
        {
            Name = "Initial Person",
            Age = 30,
            Email = "initial@example.com"
        });

        // Act - Mix of concurrent operations: upserts, reads, and updates
        var tasks = new List<Task>();

        // Add some upserts
        for (int i = 1; i <= 5; i++)
        {
            int id = i; // Capture for closure
            tasks.Add(store.UpsertAsync($"person-{id}", new TestPerson
            {
                Name = $"Person {id}",
                Age = 20 + id,
                Email = $"person{id}@example.com"
            }));
        }

        // Add some reads
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(store.GetAsync<TestPerson>("person-0"));
        }

        // Add some updates to person-0
        for (int i = 0; i < 5; i++)
        {
            int iteration = i; // Capture for closure
            tasks.Add(store.UpsertAsync("person-0", new TestPerson
            {
                Name = $"Updated Person {iteration}",
                Age = 30 + iteration,
                Email = "initial@example.com"
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Verify final state
        var count = await store.CountAsync<TestPerson>();
        Assert.Equal(6, count); // person-0 through person-5

        var person0 = await store.GetAsync<TestPerson>("person-0");
        Assert.NotNull(person0);
        Assert.Equal("initial@example.com", person0.Email);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _testDbPath, $"{_testDbPath}-wal", $"{_testDbPath}-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (IOException) { /* still locked by SQLite */ }
                catch (UnauthorizedAccessException) { /* nothing to clean up */ }
            }
        }
    }

    private sealed class TestPerson
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string Email { get; set; } = string.Empty;
    }
}
