using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <c>SqliteCommandExtensions.QueryFirstStringRowAsync</c>, the reader that keeps
/// "no row" and "row whose column is NULL" apart. <c>QueryFirstStringAsync</c> collapses both into
/// a null, which is what made a present-but-unreadable document row report itself as missing.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteStringRowReaderTests
{
    private static async Task<SqliteConnection> OpenSeededAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "CREATE TABLE t (id TEXT PRIMARY KEY, payload TEXT); " +
            "INSERT INTO t (id, payload) VALUES ('text', 'hello'), ('null', NULL)",
            CancellationToken.None);

        return connection;
    }

    [Fact]
    public async Task QueryFirstStringRowAsync_WithNoMatchingRow_ReportsNotFound()
    {
        await using var connection = await OpenSeededAsync();

        var (text, found) = await connection.QueryFirstStringRowAsync(
            "SELECT payload FROM t WHERE id = @Id", CancellationToken.None, ("Id", "absent"));

        Assert.False(found);
        Assert.Null(text);
    }

    [Fact]
    public async Task QueryFirstStringRowAsync_WithASqlNullColumn_ReportsFoundWithNullText()
    {
        await using var connection = await OpenSeededAsync();

        var (text, found) = await connection.QueryFirstStringRowAsync(
            "SELECT payload FROM t WHERE id = @Id", CancellationToken.None, ("Id", "null"));

        // The distinction the helper exists for: the row is there, its value is not.
        Assert.True(found);
        Assert.Null(text);
    }

    [Fact]
    public async Task QueryFirstStringRowAsync_WithATextColumn_ReportsFoundWithTheText()
    {
        await using var connection = await OpenSeededAsync();

        var (text, found) = await connection.QueryFirstStringRowAsync(
            "SELECT payload FROM t WHERE id = @Id", CancellationToken.None, ("Id", "text"));

        Assert.True(found);
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task QueryFirstStringAsync_CannotTellTheTwoApart()
    {
        await using var connection = await OpenSeededAsync();

        // Pinned as the reason the second helper exists, not as a defect: the schema and PRAGMA
        // reads that use this one have no row-versus-null distinction to lose.
        Assert.Null(await connection.QueryFirstStringAsync(
            "SELECT payload FROM t WHERE id = @Id", CancellationToken.None, ("Id", "absent")));
        Assert.Null(await connection.QueryFirstStringAsync(
            "SELECT payload FROM t WHERE id = @Id", CancellationToken.None, ("Id", "null")));
    }
}
