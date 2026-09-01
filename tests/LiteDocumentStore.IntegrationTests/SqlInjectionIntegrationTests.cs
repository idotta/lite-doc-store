using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Malicious JSON paths, column names and column types must be rejected before reaching
/// SQLite — and valid paths must still hit their expression index, which is why the path is
/// interpolated rather than bound.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlInjectionIntegrationTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly IDocumentStore _store;

    private sealed record Person(string Name, string Email, int Age);

    public SqlInjectionIntegrationTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"lds-injection-{Guid.NewGuid():N}.db");
        _store = new DocumentStoreFactory().Create(DocumentStoreOptions.ForFile(_testDbPath));
        _store.CreateTableAsync<Person>().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task QueryAsync_WithAnInjectedJsonPath_ThrowsAndLeavesTheTableIntact()
    {
        await _store.UpsertAsync("p1", new Person("Ada", "ada@example.com", 36));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.QueryAsync<Person, int>("$.Age') = 1 OR 1=1 --", 999));

        var byAge = await _store.QueryAsync<Person, int>("$.Age", 36);
        Assert.Single(byAge);
    }

    [Fact]
    public async Task QueryAsync_WithAValidPath_StillMatchesOnTheIndexedExpression()
    {
        await _store.CreateIndexAsync<Person>(p => p.Email);
        await _store.UpsertAsync("p1", new Person("Ada", "ada@example.com", 36));

        var plan = await _store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"EXPLAIN QUERY PLAN SELECT json(data) FROM [{_store.GetTableName<Person>()}] " +
                "WHERE json_extract(data, '$.Email') = @Value";
            command.Parameters.AddWithValue("@Value", "ada@example.com");

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(3));
            }

            return string.Join(" | ", rows);
        });

        // A bound path would not match the index expression and would scan instead.
        Assert.Contains("idx_", plan);
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithAnInjectedColumnName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AddVirtualColumnAsync<Person>(p => p.Email, "email] ; DROP TABLE [Person"));

        Assert.Equal(0, await _store.CountAsync<Person>());
    }

    [Fact]
    public async Task AddVirtualColumnAsync_WithAnUnsupportedColumnType_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AddVirtualColumnAsync<Person>(p => p.Email, "email", columnType: "TEXT, injected INTEGER"));
    }

    [Fact]
    public async Task CreateIndexAsync_WithAnInjectedIndexName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.CreateIndexAsync<Person>(p => p.Email, "idx] ON [Person] (id); --"));
    }

    public void Dispose()
    {
        _store.Dispose();

        foreach (var file in new[] { _testDbPath, $"{_testDbPath}-wal", $"{_testDbPath}-shm" })
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); }
                catch (IOException) { /* still locked */ }
                catch (UnauthorizedAccessException) { /* nothing to clean up */ }
            }
        }
    }
}
