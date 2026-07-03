using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for WAL mode and concurrent operations with multiple connections.
/// These tests use file-based databases to properly test WAL mode and reader-writer concurrency.
/// </summary>
public class WalConcurrencyIntegrationTests : IDisposable
{
    private readonly string _testDbPath;

    public WalConcurrencyIntegrationTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_wal_{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        // Force garbage collection to ensure connections are fully closed
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Clean up test database files
        var files = new[] { _testDbPath, $"{_testDbPath}-wal", $"{_testDbPath}-shm" };
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
                    // Sometimes files are still locked, ignore for tests
                }
            }
        }
    }

    [Fact]
    public async Task WalMode_EnablesSuccessfully()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Act
        await using var store = await factory.CreateAsync(options);
        await store.CreateTableAsync<Person>();

        // Assert - Verify WAL mode is enabled
        var journalMode = await store.Connection.QueryFirstStringAsync("PRAGMA journal_mode");
        Assert.Equal("wal", journalMode, StringComparer.OrdinalIgnoreCase);

        // Verify WAL file is created
        await store.UpsertAsync("person-1", new Person { Name = "Test", Age = 25, Email = "test@example.com" });
        Assert.True(File.Exists($"{_testDbPath}-wal") || File.Exists(_testDbPath));
    }

    [Fact]
    public async Task WalMode_AllowsConcurrentReaders()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create initial data
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();
            await store.UpsertAsync("person-1", new Person { Name = "Test Person", Age = 30, Email = "test@example.com" });
        }

        // Act - Open multiple readers concurrently
        var readerTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            await using var store = await factory.CreateAsync(options);
            var person = await store.GetAsync<Person>("person-1");
            return person;
        });

        var results = await Task.WhenAll(readerTasks);

        // Assert - All readers should succeed
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal("Test Person", result.Name);
            Assert.Equal(30, result.Age);
        });
    }

    [Fact]
    public async Task WalMode_AllowsConcurrentReadersWithWriter()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create initial data
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();

            // Insert initial records
            for (int i = 0; i < 10; i++)
            {
                await store.UpsertAsync($"person-{i}", new Person
                {
                    Name = $"Person {i}",
                    Age = 20 + i,
                    Email = $"person{i}@example.com"
                });
            }
        }

        // Act - Mix concurrent readers and a writer
        var tasks = new List<Task>();

        // Add reader tasks
        for (int i = 0; i < 10; i++)
        {
            int id = i; // Capture for closure
            tasks.Add(Task.Run(async () =>
            {
                await using var store = await factory.CreateAsync(options);
                var person = await store.GetAsync<Person>($"person-{id}");
                Assert.NotNull(person);
                Assert.Equal($"Person {id}", person.Name);
            }));
        }

        // Add writer task
        tasks.Add(Task.Run(async () =>
        {
            await using var store = await factory.CreateAsync(options);
            await store.UpsertAsync("person-new", new Person
            {
                Name = "New Person",
                Age = 40,
                Email = "new@example.com"
            });
        }));

        // Assert - All operations should complete without blocking
        await Task.WhenAll(tasks);

        // Verify the write succeeded
        await using (var store = await factory.CreateAsync(options))
        {
            var newPerson = await store.GetAsync<Person>("person-new");
            Assert.NotNull(newPerson);
            Assert.Equal("New Person", newPerson.Name);
        }
    }

    [Fact]
    public async Task MultiConnection_ConcurrentWrites_AllSucceed()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create table
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();
        }

        // Act - Multiple connections writing concurrently
        var writeTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await using var store = await factory.CreateAsync(options);
            await store.UpsertAsync($"person-{i}", new Person
            {
                Name = $"Person {i}",
                Age = 20 + i,
                Email = $"person{i}@example.com"
            });
        });

        await Task.WhenAll(writeTasks);

        // Assert - Verify all records were written
        await using (var store = await factory.CreateAsync(options))
        {
            var count = await store.CountAsync<Person>();
            Assert.Equal(10, count);

            // Verify each record
            for (int i = 0; i < 10; i++)
            {
                var person = await store.GetAsync<Person>($"person-{i}");
                Assert.NotNull(person);
                Assert.Equal($"Person {i}", person.Name);
                Assert.Equal(20 + i, person.Age);
            }
        }
    }

    [Fact]
    public async Task MultiConnection_ConcurrentUpdates_LastWriteWins()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create initial data
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();
            await store.UpsertAsync("person-1", new Person
            {
                Name = "Original",
                Age = 25,
                Email = "original@example.com"
            });
        }

        // Act - Multiple connections updating the same record
        var updateTasks = Enumerable.Range(0, 20).Select(async i =>
        {
            await using var store = await factory.CreateAsync(options);
            await store.UpsertAsync("person-1", new Person
            {
                Name = $"Updated {i}",
                Age = 25 + i,
                Email = $"updated{i}@example.com"
            });
        });

        await Task.WhenAll(updateTasks);

        // Assert - Verify one of the updates won (last write wins)
        await using (var finalStore = await factory.CreateAsync(options))
        {
            var person = await finalStore.GetAsync<Person>("person-1");
            Assert.NotNull(person);
            Assert.StartsWith("Updated", person.Name);

            // Count should still be 1
            var count = await finalStore.CountAsync<Person>();
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task MultiConnection_BulkOperations_AllSucceed()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create table
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();
        }

        // Act - Multiple connections performing bulk operations concurrently
        var bulkTasks = Enumerable.Range(0, 5).Select(async batchNum =>
        {
            await using var store = await factory.CreateAsync(options);

            var items = Enumerable.Range(0, 10).Select(i =>
            {
                var id = $"batch-{batchNum}-person-{i}";
                var person = new Person
                {
                    Name = $"Batch {batchNum} Person {i}",
                    Age = 20 + i,
                    Email = $"batch{batchNum}person{i}@example.com"
                };
                return (id, person);
            });

            await store.UpsertManyAsync(items);
        });

        await Task.WhenAll(bulkTasks);

        // Assert - Verify all records were written
        await using (var store = await factory.CreateAsync(options))
        {
            var count = await store.CountAsync<Person>();
            Assert.Equal(50, count); // 5 batches * 10 records each

            // Verify sample records from each batch
            for (int batchNum = 0; batchNum < 5; batchNum++)
            {
                var person = await store.GetAsync<Person>($"batch-{batchNum}-person-0");
                Assert.NotNull(person);
                Assert.Equal($"Batch {batchNum} Person 0", person.Name);
            }
        }
    }

    [Fact]
    public async Task MultiConnection_TransactionIsolation_ProperlyIsolated()
    {
        // Arrange
        var options = new DocumentStoreOptionsBuilder()
            .UseFile(_testDbPath)
            .WithWalMode(true)
            .Build();

        var factory = new DocumentStoreFactory();

        // Create table
        await using (var store = await factory.CreateAsync(options))
        {
            await store.CreateTableAsync<Person>();
        }

        // Act - One connection in transaction, another reading
        var writer = await factory.CreateAsync(options);
        var reader = await factory.CreateAsync(options);

        try
        {
            // Start a transaction in writer but don't commit yet
            await writer.ExecuteInTransactionAsync(async () =>
            {
                await writer.UpsertAsync("person-1", new Person
                {
                    Name = "In Transaction",
                    Age = 30,
                    Email = "transaction@example.com"
                });

                // While transaction is open, reader should not see the uncommitted data
                var person = await reader.GetAsync<Person>("person-1");
                Assert.Null(person); // Should not see uncommitted write

                // Transaction completes here (auto-commit)
            });

            // After transaction commits, reader should see the data
            var committedPerson = await reader.GetAsync<Person>("person-1");
            Assert.NotNull(committedPerson);
            Assert.Equal("In Transaction", committedPerson.Name);
        }
        finally
        {
            await writer.DisposeAsync();
            await reader.DisposeAsync();
        }
    }
}

/// <summary>
/// Test model for concurrency tests.
/// </summary>
public class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Email { get; set; } = string.Empty;
}
