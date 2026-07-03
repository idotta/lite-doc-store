using Xunit;

namespace LiteDocumentStore.UnitTests;

public class DocumentStoreTests
{
    private readonly string _testDbPath;

    public DocumentStoreTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
    }

    [Fact]
    public void Constructor_WithConnection_CreatesDocumentStore()
    {
        // Arrange & Act
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        using var connection = connectionFactory.CreateConnection(options);
        var store = new DocumentStore(connection);

        // Assert
        Assert.NotNull(store);
        Assert.NotNull(store.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, store.Connection.State);

        // Cleanup
        connection.Close();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task GetTableName_ReturnsTypeName()
    {
        // This tests the interaction indirectly through CreateTableAsync
        var testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();

        using (var connection = connectionFactory.CreateConnection(options))
        {
            var store = new DocumentStore(connection);

            // Act - create table should use type name
            await store.CreateTableAsync<TestPerson>();

            // Assert - verify table exists with correct name
            var checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='TestPerson'";
            var result = await store.Connection.QueryFirstStringAsync(checkSql);
            Assert.Equal("TestPerson", result);
        }

        // Cleanup
        if (File.Exists(testDbPath))
        {
            try { File.Delete(testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Store_WithOwnsConnection_DisposesConnection()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        var connection = connectionFactory.CreateConnection(options);
        var store = new DocumentStore(connection, ownsConnection: true);

        // Act
        await store.DisposeAsync();

        // Assert - connection should be disposed
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Store_WithoutOwnsConnection_DoesNotDisposeConnection()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        var connection = connectionFactory.CreateConnection(options);
        var store = new DocumentStore(connection, ownsConnection: false);

        // Act
        await store.DisposeAsync();

        // Assert - connection should still be open
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        // Cleanup
        connection.Dispose();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Operations_OnClosedConnection_ThrowsInvalidOperationException()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        var connection = connectionFactory.CreateConnection(options);
        connection.Close(); // Close the connection
        var store = new DocumentStore(connection, ownsConnection: false);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateTableAsync<TestPerson>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.UpsertAsync("test-id", new TestPerson { Name = "Test" }));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAsync<TestPerson>("test-id"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAllAsync<TestPerson>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.DeleteAsync<TestPerson>("test-id"));

        // Cleanup
        connection.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task IsHealthyAsync_WithOpenConnection_ReturnsTrue()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        using var connection = connectionFactory.CreateConnection(options);
        var store = new DocumentStore(connection, ownsConnection: false);

        // Act
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.True(isHealthy);

        // Cleanup
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task IsHealthyAsync_WithClosedConnection_ReturnsFalse()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        var connection = connectionFactory.CreateConnection(options);
        connection.Close();
        var store = new DocumentStore(connection, ownsConnection: false);

        // Act
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.False(isHealthy);

        // Cleanup
        connection.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task IsHealthyAsync_OnDisposedStore_ReturnsFalse()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();
        var connection = connectionFactory.CreateConnection(options);
        var store = new DocumentStore(connection, ownsConnection: false);

        // Act
        await store.DisposeAsync();
        var isHealthy = await store.IsHealthyAsync();

        // Assert
        Assert.False(isHealthy);

        // Cleanup
        connection.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    private class TestPerson
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string Email { get; set; } = string.Empty;
    }

    [Fact]
    public async Task ConcurrentUpsert_AllOperationsComplete()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();

        using (var connection = connectionFactory.CreateConnection(options))
        {
            var store = new DocumentStore(connection);
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

        // Cleanup
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrentRead_AllOperationsSucceed()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();

        using (var connection = connectionFactory.CreateConnection(options))
        {
            var store = new DocumentStore(connection);
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

        // Cleanup
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrentMixedOperations_AllOperationsComplete()
    {
        // Arrange
        var options = new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" };
        var connectionFactory = new DefaultConnectionFactory();

        using (var connection = connectionFactory.CreateConnection(options))
        {
            var store = new DocumentStore(connection);
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

        // Cleanup
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }
}
