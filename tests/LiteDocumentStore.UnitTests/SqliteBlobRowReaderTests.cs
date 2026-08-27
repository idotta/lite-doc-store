using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for the row-aware blob readers — <c>QueryFirstBlobRowAsync</c>,
/// <c>QueryFirstInt64RowAsync</c> and <c>QueryFirstInt64StringAsync</c> — which keep "no row"
/// apart from "row holding no readable payload" and surface the storage class the row actually
/// carries.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteBlobRowReaderTests
{
    private const string BlobRowSql = "SELECT typeof(data), data FROM t WHERE id = @Id";
    private const string LengthRowSql = "SELECT typeof(data), length(data) FROM t WHERE id = @Id";
    private const string RowIdSql = "SELECT rowid, typeof(data) FROM t WHERE id = @Id";

    private static async Task<SqliteConnection> OpenSeededAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "CREATE TABLE t (id TEXT PRIMARY KEY, data BLOB); " +
            "INSERT INTO t (id, data) VALUES " +
            "('blob', x'0102030405'), ('empty', x''), ('zero', zeroblob(0)), " +
            "('null', NULL), ('text', 'hello'), ('int', 42), ('real', 1.5)",
            CancellationToken.None);

        return connection;
    }

    [Fact]
    public async Task QueryFirstBlobRowAsync_WithNoMatchingRow_ReportsNotFound()
    {
        await using var connection = await OpenSeededAsync();

        var (typeName, payload, found) = await connection.QueryFirstBlobRowAsync(
            BlobRowSql, CancellationToken.None, ("Id", "absent"));

        Assert.False(found);
        Assert.Null(typeName);
        Assert.Null(payload);
    }

    [Fact]
    public async Task QueryFirstBlobRowAsync_WithABlob_ReportsFoundWithThePayload()
    {
        await using var connection = await OpenSeededAsync();

        var (typeName, payload, found) = await connection.QueryFirstBlobRowAsync(
            BlobRowSql, CancellationToken.None, ("Id", "blob"));

        Assert.True(found);
        Assert.Equal("blob", typeName);
        Assert.Equal([1, 2, 3, 4, 5], payload);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    public async Task QueryFirstBlobRowAsync_WithAnEmptyBlob_ReportsFoundWithNoBytes(string id)
    {
        await using var connection = await OpenSeededAsync();

        // An empty blob is a blob, whichever way it was written: it must not read as corrupt.
        var (typeName, payload, found) = await connection.QueryFirstBlobRowAsync(
            BlobRowSql, CancellationToken.None, ("Id", id));

        Assert.True(found);
        Assert.Equal("blob", typeName);
        Assert.Empty(payload!);
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("text", "text")]
    [InlineData("int", "integer")]
    [InlineData("real", "real")]
    public async Task QueryFirstBlobRowAsync_WithANonBlobValue_ReportsFoundWithoutReadingIt(
        string id,
        string expectedTypeName)
    {
        await using var connection = await OpenSeededAsync();

        var (typeName, payload, found) = await connection.QueryFirstBlobRowAsync(
            BlobRowSql, CancellationToken.None, ("Id", id));

        Assert.True(found);
        Assert.Equal(expectedTypeName, typeName);

        // The payload is left unread on purpose. GetFieldValue<byte[]> succeeds on a TEXT,
        // INTEGER or REAL value and hands back SQLite's coerced bytes, so reading first and
        // validating afterwards would substitute wrong bytes for a detectable failure.
        Assert.Null(payload);
    }

    [Fact]
    public async Task QueryFirstBlobRowAsync_OnANonBlobValue_DoesNotReadWhatAReaderWouldCoerce()
    {
        await using var connection = await OpenSeededAsync();

        // The behaviour the guard exists for, stated directly: a bare reader is happy to convert.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM t WHERE id = 'text'";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(5, reader.GetFieldValue<byte[]>(0).Length);
    }

    [Fact]
    public async Task QueryFirstInt64RowAsync_WithNoMatchingRow_ReportsNotFound()
    {
        await using var connection = await OpenSeededAsync();

        var (typeName, value, found) = await connection.QueryFirstInt64RowAsync(
            LengthRowSql, CancellationToken.None, ("Id", "absent"));

        Assert.False(found);
        Assert.Null(typeName);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryFirstInt64RowAsync_WithASqlNullValue_ReportsFoundWithNoValue()
    {
        await using var connection = await OpenSeededAsync();

        // The distinction QueryFirstInt64Async cannot make: it returns null for both.
        var (typeName, value, found) = await connection.QueryFirstInt64RowAsync(
            LengthRowSql, CancellationToken.None, ("Id", "null"));

        Assert.True(found);
        Assert.Equal("null", typeName);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("blob", "blob", 5L)]
    [InlineData("empty", "blob", 0L)]
    [InlineData("text", "text", 5L)]
    [InlineData("int", "integer", 2L)]
    [InlineData("real", "real", 3L)]
    public async Task QueryFirstInt64RowAsync_ReportsTheStorageClassBesideTheLength(
        string id,
        string expectedTypeName,
        long expectedLength)
    {
        await using var connection = await OpenSeededAsync();

        var (typeName, value, found) = await connection.QueryFirstInt64RowAsync(
            LengthRowSql, CancellationToken.None, ("Id", id));

        Assert.True(found);
        Assert.Equal(expectedTypeName, typeName);

        // length() answers for a non-blob too — characters for TEXT, digits for a number — which
        // is why the storage class has to travel with it rather than the length being trusted.
        Assert.Equal(expectedLength, value);
    }

    [Fact]
    public async Task QueryFirstInt64StringAsync_WithNoMatchingRow_ReturnsNull()
    {
        await using var connection = await OpenSeededAsync();

        var row = await connection.QueryFirstInt64StringAsync(
            RowIdSql, CancellationToken.None, ("Id", "absent"));

        Assert.Null(row);
    }

    [Theory]
    [InlineData("blob", "blob")]
    [InlineData("null", "null")]
    [InlineData("text", "text")]
    public async Task QueryFirstInt64StringAsync_ReturnsTheRowIdAndTheStorageClass(
        string id,
        string expectedTypeName)
    {
        await using var connection = await OpenSeededAsync();

        var row = await connection.QueryFirstInt64StringAsync(
            RowIdSql, CancellationToken.None, ("Id", id));

        Assert.NotNull(row);
        Assert.True(row.Value.Number > 0);
        Assert.Equal(expectedTypeName, row.Value.Text);
    }
}
