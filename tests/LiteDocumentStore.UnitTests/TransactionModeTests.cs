using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="TransactionMode"/> and the mode overloads on the store.
/// </summary>
[Trait("Category", "Unit")]
public class TransactionModeTests : IDisposable
{
    private readonly string _testDbPath = Path.Combine(Path.GetTempPath(), $"test_txmode_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var file in new[] { _testDbPath, $"{_testDbPath}-wal", $"{_testDbPath}-shm" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Temp file still held by a finalizing connection.
            }
        }
    }

    [Fact]
    public void Deferred_IsTheDefaultEnumValue()
    {
        // Load-bearing: the tokenless overloads delegate with TransactionMode.Deferred, so a
        // renumbering here would silently change what every existing caller gets.
        Assert.Equal(TransactionMode.Deferred, default(TransactionMode));
        Assert.Equal(0, (int)TransactionMode.Deferred);
    }

    [Fact]
    public async Task BeginTransactionAsync_WithAnUndefinedMode_Throws()
    {
        await using var store = await CreateStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.BeginTransactionAsync((TransactionMode)42));

        Assert.Equal("mode", error.ParamName);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WithAnUndefinedMode_Throws()
    {
        await using var store = await CreateStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ExecuteInTransactionAsync(_ => Task.CompletedTask, (TransactionMode)42));

        Assert.Equal("mode", error.ParamName);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WithAModeAndANullAction_ThrowsBeforeRentingAConnection()
    {
        await using var store = await CreateStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.ExecuteInTransactionAsync(null!, TransactionMode.Immediate));

        Assert.Equal("action", error.ParamName);
    }

    [Theory]
    [InlineData(TransactionMode.Deferred)]
    [InlineData(TransactionMode.Immediate)]
    public async Task BeginTransactionAsync_WithEitherMode_ReturnsAUsableTransaction(TransactionMode mode)
    {
        await using var store = await CreateStoreAsync();
        await store.CreateTableAsync<Person>();

        await using var transaction = await store.BeginTransactionAsync(mode);
        await transaction.UpsertAsync("a", new Person { Name = "A", Age = 1 });
        await transaction.CommitAsync();

        Assert.NotNull(await store.GetAsync<Person>("a"));
    }

    private Task<IDocumentStore> CreateStoreAsync() =>
        new DocumentStoreFactory().CreateAsync(
            new DocumentStoreOptions { ConnectionString = $"Data Source={_testDbPath}" });

    private sealed class Person
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }
}
