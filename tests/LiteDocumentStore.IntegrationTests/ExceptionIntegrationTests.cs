using LiteDocumentStore.Exceptions;
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
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await _store.GetAsync<StrictModel>("test-1"));

        // System.Text.Json may throw JsonException or SerializationException depending on the scenario
        Assert.True(
            exception is SerializationException || exception is JsonException,
            $"Expected SerializationException or JsonException, but got {exception.GetType().Name}");
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

    private sealed class CircularReference
    {
        public CircularReference? Self { get; set; }
    }

    private sealed class StrictModel
    {
        public int RequiredInt { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
