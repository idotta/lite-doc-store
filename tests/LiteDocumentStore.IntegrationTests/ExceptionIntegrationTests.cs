using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

public class ExceptionIntegrationTests : IDisposable
{
    private readonly IDocumentStore _store;

    public ExceptionIntegrationTests()
    {
        // The store owns its connection pool; ForInMemory gives a uniquely named
        // shared-cache in-memory database that every pooled connection sees.
        _store = new DocumentStoreFactory().Create(DocumentStoreOptions.ForInMemory());
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public async Task SerializationException_ThrownWhenSerializationFails()
    {
        // Arrange
        await _store.CreateTableAsync<CircularReference>();

        // Create a circular reference that will cause serialization to fail
        var obj = new CircularReference();
        obj.Self = obj;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SerializationException>(
            async () => await _store.UpsertAsync("test-1", obj));

        Assert.NotNull(exception);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(typeof(CircularReference), exception.TargetType);
    }

    [Fact]
    public async Task SerializationException_ThrownWhenDeserializationFails()
    {
        // Arrange
        await _store.CreateTableAsync<StrictModel>();

        // Manually insert invalid JSON through the raw-SQL escape hatch
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES (@Id, jsonb(@Data))",
            ct,
            ("Id", "test-1"), ("Data", "{\"RequiredInt\": \"not-a-number\"}")));

        // Act & Assert - Deserialization should fail because "not-a-number" cannot be parsed as int
        var exception = await Assert.ThrowsAsync<SerializationException>(
            async () => await _store.GetAsync<StrictModel>("test-1"));

        // Pinned, not "either type": JsonHelper wraps every JsonException on the way out, so a
        // caller catching SerializationException catches the whole family.
        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Equal(typeof(StrictModel), exception.TargetType);
    }

    [Fact]
    public void TableNotFoundException_CanBeCreatedAndContainsTableName()
    {
        // Arrange
        var tableName = "NonExistentTable";

        // Act
        var exception = new TableNotFoundException(tableName);

        // Assert
        Assert.Equal(tableName, exception.TableName);
        Assert.Contains(tableName, exception.Message);
    }

    [Fact]
    public void ConcurrencyException_CanBeCreatedWithContextInformation()
    {
        // Arrange
        var documentId = "doc-123";
        var tableName = "Customer";
        var message = "Document was modified by another process";

        // Act
        var exception = new ConcurrencyException(message, documentId, tableName);

        // Assert
        Assert.Equal(documentId, exception.DocumentId);
        Assert.Equal(tableName, exception.TableName);
        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public void LiteDocumentStoreException_IsBaseClassForAllCustomExceptions()
    {
        // Assert
        Assert.True(typeof(LiteDocumentStoreException).IsAssignableFrom(typeof(TableNotFoundException)));
        Assert.True(typeof(LiteDocumentStoreException).IsAssignableFrom(typeof(SerializationException)));
        Assert.True(typeof(LiteDocumentStoreException).IsAssignableFrom(typeof(ConcurrencyException)));
    }

    /// <summary>
    /// A document table without the store's <c>NOT NULL</c> on <c>data</c>, so a SQL NULL row can
    /// be created at all — the store's own DDL forbids it, and only raw SQL can produce one.
    /// </summary>
    private async Task CreateNullableTableAsync<T>()
    {
        var table = _store.GetTableName<T>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            $"CREATE TABLE [{table}] (id TEXT PRIMARY KEY, data BLOB, version INTEGER NOT NULL DEFAULT 1)",
            ct));
    }

    [Fact]
    public async Task GetAllAsync_WithASqlNullDataColumn_ThrowsCorruptDataNamingTheId()
    {
        await CreateNullableTableAsync<StrictModel>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES ('null-row', NULL)",
            ct));

        var exception = await Assert.ThrowsAsync<CorruptDataException>(() => _store.GetAllAsync<StrictModel>());

        // The JSON literal null case is pinned in DeserializationIntegrationTests; this is the
        // other way a row can read back as nothing, and it must not be silently dropped either.
        Assert.Contains("null-row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StrictModel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_WithASqlNullDataColumn_Throws()
    {
        await CreateNullableTableAsync<StrictModel>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES ('null-row', NULL)",
            ct));

        // Both query shapes read through the same id-carrying reader, so both have to report it.
        await Assert.ThrowsAsync<CorruptDataException>(() =>
            _store.QueryAsync(DocumentQuery<StrictModel>.All()));
        await Assert.ThrowsAsync<CorruptDataException>(() =>
            _store.GetManyAsync<StrictModel>(["null-row"]));
    }

    [Fact]
    public async Task GetAsync_WithASqlNullDataColumn_ThrowsInsteadOfLookingMissing()
    {
        await CreateNullableTableAsync<StrictModel>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES ('null-row', NULL)",
            ct));

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => _store.GetAsync<StrictModel>("null-row"));

        Assert.Equal(typeof(StrictModel), exception.TargetType);
        Assert.Contains("null-row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StrictModel", exception.Message, StringComparison.Ordinal);

        // The row that made the single read throw is the same row Exists reports as present, which
        // is the contradiction this pins: one answer for the whole store, not two.
        Assert.True(await _store.ExistsAsync<StrictModel>("null-row"));
    }

    [Fact]
    public async Task GetWithVersionAsync_WithASqlNullDataColumn_ThrowsInsteadOfReturningNull()
    {
        // A value-type document on purpose: an empty projection deserializes to default(T), which
        // is 0 rather than null for a struct, so a guard that only tests the deserialized document
        // for null would hand back a fabricated row here instead of throwing.
        await CreateNullableTableAsync<StrictValue>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictValue] (id, data) VALUES ('null-row', NULL)",
            ct));

        var exception = await Assert.ThrowsAsync<CorruptDataException>(
            () => _store.GetWithVersionAsync<StrictValue>("null-row"));

        Assert.Equal(typeof(StrictValue), exception.TargetType);
        Assert.Contains("null-row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StrictValue", exception.Message, StringComparison.Ordinal);
        Assert.True(await _store.ExistsAsync<StrictValue>("null-row"));
    }

    [Fact]
    public async Task GetAsync_WithASqlNullDataRowInTheTable_StillReportsAMissingIdAsMissing()
    {
        await CreateNullableTableAsync<StrictModel>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES ('null-row', NULL)",
            ct));

        // Row presence now decides "not found", so the absent id must stay absent rather than
        // being swept into the new throw.
        Assert.Null(await _store.GetAsync<StrictModel>("no-such-id"));
        Assert.Null(await _store.GetWithVersionAsync<StrictModel>("no-such-id"));
    }

    [Theory]
    [InlineData("GetAll")]
    [InlineData("Query")]
    [InlineData("GetMany")]
    public async Task CollectionRead_WithASqlNullValueTypePayload_ThrowsInsteadOfReturningDefault(
        string operation)
    {
        await CreateNullableTableAsync<StrictValue>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictValue] (id, data) VALUES ('null-row', NULL)",
            ct));

        // JsonHelper maps an absent JSON string to default(T). For a struct that is a real value,
        // so checking only the deserialized result fabricates a document instead of detecting the
        // SQL NULL row. The single-row reads guard the projection before deserializing; every
        // collection shape must make the same distinction.
        Func<Task> read = operation switch
        {
            "GetAll" => async () => _ = await _store.GetAllAsync<StrictValue>(),
            "Query" => async () => _ = await _store.QueryAsync(DocumentQuery<StrictValue>.All()),
            "GetMany" => async () => _ = await _store.GetManyAsync<StrictValue>(["null-row"]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var exception = await Assert.ThrowsAsync<CorruptDataException>(read);

        Assert.Equal("null-row", exception.Id);
        Assert.Equal("StrictValue", exception.TableName);
        Assert.Equal(typeof(StrictValue), exception.TargetType);
    }

    [Theory]
    [InlineData("Get")]
    [InlineData("GetWithVersion")]
    [InlineData("GetAll")]
    [InlineData("Query")]
    [InlineData("GetMany")]
    public async Task EveryRead_WithAJsonNullValueTypePayload_ThrowsTheSameExceptionAsAReferenceType(
        string operation)
    {
        await _store.CreateTableAsync<StrictValue>();
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictValue] (id, data, version) VALUES ('json-null', jsonb('null'), 1)",
            ct));

        // The other shape of a row that reads back as nothing: json(data) yields the 4-character
        // text "null". A reference type deserializes that to null and was already reported as
        // corrupt, but a value type made System.Text.Json refuse the conversion, so the one row
        // surfaced as a SerializationException instead — the exception type depended on the type
        // the row was read as. It is rejected before deserializing now, so it does not.
        Func<Task> read = operation switch
        {
            "Get" => async () => _ = await _store.GetAsync<StrictValue>("json-null"),
            "GetWithVersion" => async () => _ = await _store.GetWithVersionAsync<StrictValue>("json-null"),
            "GetAll" => async () => _ = await _store.GetAllAsync<StrictValue>(),
            "Query" => async () => _ = await _store.QueryAsync(DocumentQuery<StrictValue>.All()),
            "GetMany" => async () => _ = await _store.GetManyAsync<StrictValue>(["json-null"]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var exception = await Assert.ThrowsAsync<CorruptDataException>(read);

        Assert.Equal("json-null", exception.Id);
        Assert.Equal("StrictValue", exception.TableName);
        Assert.Equal(typeof(StrictValue), exception.TargetType);
    }

    [Fact]
    public async Task DeserializeDocument_WithAJsonNullPayload_KeepsItsOwnContract()
    {
        // The raw-SQL helper is documented to return default for null/empty JSON and to throw
        // SerializationException on malformed input; the corrupt-row guard lives in the read
        // paths, not in JsonHelper, so this surface is deliberately unchanged.
        Assert.Null(_store.DeserializeDocument<StrictModel>(null));
        Assert.Null(_store.DeserializeDocument<StrictModel>(string.Empty));
        Assert.Null(_store.DeserializeDocument<StrictModel>("null"));
    }

    [Fact]
    public async Task GetAsync_WithACorruptJsonbPayload_SurfacesTheSqliteError()
    {
        await _store.CreateTableAsync<StrictModel>();

        // Bytes that are not JSONB at all. SQLite fails inside the json(data) projection, before
        // any of them reach the serializer, so this is a SqliteException and neither a
        // CorruptDataException nor a SerializationException. Translating it would mean classifying
        // SQLite error text, which this library deliberately does not do — pinned here as the
        // honest current contract.
        await _store.ExecuteRawAsync((connection, ct) => connection.ExecuteAsync(
            "INSERT INTO [StrictModel] (id, data) VALUES ('corrupt', X'FFFFFF')",
            ct));

        var exception = await Assert.ThrowsAsync<SqliteException>(() => _store.GetAsync<StrictModel>("corrupt"));

        Assert.Contains("malformed JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CircularReference
    {
        public CircularReference? Self { get; set; }
    }

    private sealed class StrictModel
    {
        public int RequiredInt { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private readonly record struct StrictValue(int RequiredInt, string Name);
}
