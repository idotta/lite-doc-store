using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

[Trait("Category", "Unit")]
public class ExceptionTests
{
    [Fact]
    public void LiteDocumentStoreException_DefaultConstructor_CreatesExceptionWithDefaultMessage()
    {
        // Act
        var exception = new LiteDocumentStoreException();

        // Assert
        Assert.NotNull(exception);
        Assert.NotNull(exception.Message);
        Assert.Contains("LiteDocumentStore", exception.Message);
    }

    [Fact]
    public void LiteDocumentStoreException_MessageConstructor_CreatesExceptionWithCustomMessage()
    {
        // Arrange
        var expectedMessage = "Custom error message";

        // Act
        var exception = new LiteDocumentStoreException(expectedMessage);

        // Assert
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void LiteDocumentStoreException_InnerExceptionConstructor_PreservesInnerException()
    {
        // Arrange
        var innerException = new SqliteException("SQLite error", 1);
        var message = "Wrapper error";

        // Act
        var exception = new LiteDocumentStoreException(message, innerException);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void TableNotFoundException_Constructor_SetsTableName()
    {
        // Arrange
        var tableName = "Customer";

        // Act
        var exception = new TableNotFoundException(tableName);

        // Assert
        Assert.Equal(tableName, exception.TableName);
        Assert.Contains(tableName, exception.Message);
    }

    [Fact]
    public void TableNotFoundException_InnerExceptionConstructor_PreservesInnerException()
    {
        // Arrange
        var tableName = "Order";
        var innerException = new SqliteException("Table not found", 1);

        // Act
        var exception = new TableNotFoundException(tableName, innerException);

        // Assert
        Assert.Equal(tableName, exception.TableName);
        Assert.Contains(tableName, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void DocumentSerializationException_MessageConstructor_CreatesException()
    {
        // Arrange
        var message = "Serialization failed";

        // Act
        var exception = new DocumentSerializationException(message);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Null(exception.TargetType);
    }

    [Fact]
    public void DocumentSerializationException_InnerExceptionConstructor_PreservesInnerException()
    {
        // Arrange
        var message = "Serialization failed";
        var innerException = new InvalidOperationException("JSON error");

        // Act
        var exception = new DocumentSerializationException(message, innerException);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Same(innerException, exception.InnerException);
        Assert.Null(exception.TargetType);
    }

    [Fact]
    public void DocumentSerializationException_TypeConstructor_SetsTargetType()
    {
        // Arrange
        var message = "Serialization failed";
        var targetType = typeof(Customer);
        var innerException = new InvalidOperationException("JSON error");

        // Act
        var exception = new DocumentSerializationException(message, targetType, innerException);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Equal(targetType, exception.TargetType);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void ConcurrencyException_MessageConstructor_CreatesException()
    {
        // Arrange
        var message = "Concurrency conflict detected";

        // Act
        var exception = new ConcurrencyException(message);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Null(exception.DocumentId);
        Assert.Null(exception.TableName);
    }

    [Fact]
    public void ConcurrencyException_InnerExceptionConstructor_PreservesInnerException()
    {
        // Arrange
        var message = "Concurrency conflict";
        var innerException = new SqliteException("Database locked", 5);

        // Act
        var exception = new ConcurrencyException(message, innerException);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void ConcurrencyException_DocumentInfoConstructor_SetsProperties()
    {
        // Arrange
        var message = "Concurrency conflict";
        var documentId = "doc-123";
        var tableName = "Customer";

        // Act
        var exception = new ConcurrencyException(message, documentId, tableName);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Equal(documentId, exception.DocumentId);
        Assert.Equal(tableName, exception.TableName);
    }

    [Fact]
    public void ConcurrencyException_FullConstructor_SetsAllProperties()
    {
        // Arrange
        var message = "Concurrency conflict";
        var documentId = "doc-456";
        var tableName = "Order";
        var innerException = new SqliteException("Conflict", 19);

        // Act
        var exception = new ConcurrencyException(message, documentId, tableName, innerException);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Equal(documentId, exception.DocumentId);
        Assert.Equal(tableName, exception.TableName);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void AllCustomExceptions_InheritFromLiteDocumentStoreException()
    {
        // Assert
        Assert.IsAssignableFrom<LiteDocumentStoreException>(new TableNotFoundException("test"));
        Assert.IsAssignableFrom<LiteDocumentStoreException>(new DocumentSerializationException("test"));
        Assert.IsAssignableFrom<LiteDocumentStoreException>(new ConcurrencyException("test"));
        Assert.IsAssignableFrom<LiteDocumentStoreException>(new CorruptDataException("test"));
    }

    [Fact]
    public void CorruptDataException_IsNotADocumentSerializationException()
    {
        // A deliberate split: a corrupt row and a JSON serialization failure are different
        // problems, so catching one must not catch the other.
        Assert.IsNotAssignableFrom<DocumentSerializationException>(new CorruptDataException("test"));
    }

    [Fact]
    public void CorruptDataException_MessageConstructor_LeavesEveryPropertyUnset()
    {
        var exception = new CorruptDataException("no readable payload");

        Assert.Equal("no readable payload", exception.Message);
        Assert.Null(exception.Id);
        Assert.Null(exception.TableName);
        Assert.Null(exception.TargetType);
        Assert.Null(exception.StoredTypeName);
    }

    [Fact]
    public void CorruptDataException_ForADocument_CarriesTheIdTableAndType()
    {
        var exception = new CorruptDataException("corrupt", "doc-1", "Customer", typeof(Customer));

        Assert.Equal("doc-1", exception.Id);
        Assert.Equal("Customer", exception.TableName);
        Assert.Equal(typeof(Customer), exception.TargetType);

        // A document read projects json(data), never typeof(data), so no storage class is seen.
        Assert.Null(exception.StoredTypeName);
    }

    [Fact]
    public void CorruptDataException_ForABlob_CarriesTheStorageClassAndNoTargetType()
    {
        var exception = new CorruptDataException(
            "corrupt", "blob-1", "__store_blobs", targetType: null, storedTypeName: "text");

        Assert.Equal("blob-1", exception.Id);
        Assert.Equal("__store_blobs", exception.TableName);

        // A blob is raw bytes; there is no type it was being read as.
        Assert.Null(exception.TargetType);
        Assert.Equal("text", exception.StoredTypeName);
    }

    private sealed class Customer
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
