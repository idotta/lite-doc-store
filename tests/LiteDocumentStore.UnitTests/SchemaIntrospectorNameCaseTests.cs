using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// SQLite resolves identifiers ignoring ASCII case, so the introspector's name lookups must too:
/// a case-sensitive comparison reported an existing table or index as absent.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SchemaIntrospectorNameCaseTests
{
    private static async Task<SqliteConnection> OpenWithSchemaAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE MyApp_Customer (id TEXT PRIMARY KEY, name TEXT);" +
            "CREATE INDEX Idx_Customer_Name ON MyApp_Customer (name);";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    [Theory]
    [InlineData("MyApp_Customer")]
    [InlineData("myapp_customer")]
    [InlineData("MYAPP_CUSTOMER")]
    public async Task TableExistsAsync_IgnoresAsciiCase(string tableName)
    {
        await using var connection = await OpenWithSchemaAsync();

        Assert.True(await new SchemaIntrospector(connection).TableExistsAsync(tableName));
    }

    [Theory]
    [InlineData("Idx_Customer_Name")]
    [InlineData("idx_customer_name")]
    public async Task IndexExistsAsync_IgnoresAsciiCase(string indexName)
    {
        await using var connection = await OpenWithSchemaAsync();

        Assert.True(await new SchemaIntrospector(connection).IndexExistsAsync(indexName));
    }

    [Fact]
    public async Task GetIndexesAsync_FiltersByTableIgnoringAsciiCase()
    {
        await using var connection = await OpenWithSchemaAsync();

        var indexes = (await new SchemaIntrospector(connection).GetIndexesAsync("myapp_customer")).ToList();

        Assert.Single(indexes);
        Assert.Equal("Idx_Customer_Name", indexes[0].Name);
    }

    [Fact]
    public async Task TableExistsAsync_WithAnotherName_StillReturnsFalse()
    {
        await using var connection = await OpenWithSchemaAsync();

        Assert.False(await new SchemaIntrospector(connection).TableExistsAsync("MyApp_Customers"));
    }
}
