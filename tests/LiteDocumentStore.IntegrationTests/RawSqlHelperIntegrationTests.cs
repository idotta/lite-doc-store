using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Integration tests for the raw-SQL helpers (<c>GetTableName</c>, <c>SerializeDocument</c>,
/// <c>DeserializeDocument</c>) against real SQLite: they must produce exactly the table name and
/// the JSONB byte encoding the store itself uses, in both directions, on the store and inside a
/// transaction.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public sealed class RawSqlHelperIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public RawSqlHelperIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Person(string Name, string Email, int Age);

    private async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<Person>();
        return store;
    }

    [Fact]
    public async Task GetTableNameAndDeserializeDocument_InRawSql_ReadBackAnUpsertedDocument()
    {
        // Arrange - written through the document API
        var store = await CreateStoreAsync();
        var person = new Person("Ada", "ada@example.com", 36);
        await store.UpsertAsync("p1", person);

        // Act - the caller builds its own SQL, knowing neither the table name nor the JSON shape
        var table = store.GetTableName<Person>();
        var loaded = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            var json = await connection.QueryFirstStringAsync(
                $"SELECT json(data) FROM [{table}] WHERE id = @Id", ct, ("Id", "p1"));

            return store.DeserializeDocument<Person>(json);
        });

        // Assert
        Assert.Equal(person, loaded);
    }

    [Fact]
    public async Task SerializeDocument_BoundToARawJsonbInsert_IsReadableByGetAsync()
    {
        // Arrange
        var store = await CreateStoreAsync();
        var person = new Person("Grace", "grace@example.com", 45);
        var table = store.GetTableName<Person>();

        // Act - the write direction: raw INSERT with jsonb(@Data) over the store's own bytes
        await store.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO [{table}] (id, data, version) VALUES (@Id, jsonb(@Data), 1)";
            command.Parameters.AddWithValue("@Id", "p2");
            command.Parameters.AddWithValue("@Data", store.SerializeDocument(person));

            await command.ExecuteNonQueryAsync(ct);
        });

        // Assert - the byte encoding matches what the store expects on read
        var loaded = await store.GetAsync<Person>("p2");
        Assert.Equal(person, loaded);
    }

    [Fact]
    public async Task GetTableName_OnTheStoreAndInRawSql_NamesTheTableSqliteActuallyCreated()
    {
        // Arrange
        var store = await CreateStoreAsync();

        // Act
        var table = store.GetTableName<Person>();
        var found = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @Name", ct, ("Name", table)));

        // Assert
        Assert.Equal(table, found);
    }

    [Fact]
    public async Task TransactionHelpers_InRawSql_SeeTheTransactionsUncommittedWrite()
    {
        // Arrange
        var store = await CreateStoreAsync();
        var person = new Person("Linus", "linus@example.com", 28);

        // Act
        await using (var transaction = await store.BeginTransactionAsync())
        {
            // All three members are reachable on the transaction and agree with the store
            var table = transaction.GetTableName<Person>();
            Assert.Equal(store.GetTableName<Person>(), table);

            await transaction.UpsertAsync("p3", person);

            var uncommitted = await transaction.ExecuteRawAsync(async (connection, ct) =>
            {
                var json = await connection.QueryFirstStringAsync(
                    $"SELECT json(data) FROM [{table}] WHERE id = @Id", ct, ("Id", "p3"));

                return transaction.DeserializeDocument<Person>(json);
            });

            // Assert - the raw read runs on the transaction's connection, so it sees the write
            Assert.Equal(person, uncommitted);

            await transaction.CommitAsync();
        }

        Assert.Equal(person, await store.GetAsync<Person>("p3"));
    }

    [Fact]
    public async Task TransactionSerializeDocument_BoundToARawJsonbInsert_CommitsReadableBytes()
    {
        // Arrange
        var store = await CreateStoreAsync();
        var person = new Person("Edsger", "edsger@example.com", 72);

        // Act
        await store.ExecuteInTransactionAsync(async transaction =>
        {
            var table = transaction.GetTableName<Person>();

            await transaction.ExecuteRawAsync(async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"INSERT INTO [{table}] (id, data, version) VALUES (@Id, jsonb(@Data), 1)";
                command.Parameters.AddWithValue("@Id", "p4");
                command.Parameters.AddWithValue("@Data", transaction.SerializeDocument(person));

                await command.ExecuteNonQueryAsync(ct);
            });
        });

        // Assert
        Assert.Equal(person, await store.GetAsync<Person>("p4"));
    }
}
