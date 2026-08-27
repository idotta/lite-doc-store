namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Exception thrown when JSON serialization or deserialization fails.
/// </summary>
public class DocumentSerializationException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the type that was being serialized or deserialized when the error occurred.
    /// </summary>
    public Type? TargetType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSerializationException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public DocumentSerializationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSerializationException"/> class with an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public DocumentSerializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSerializationException"/> class with type
    /// information, for a failure that has no underlying exception (such as stored JSON that
    /// deserializes to null).
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="targetType">The type that was being serialized or deserialized.</param>
    public DocumentSerializationException(string message, Type targetType)
        : base(message)
    {
        TargetType = targetType;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSerializationException"/> class with type information.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="targetType">The type that was being serialized or deserialized.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public DocumentSerializationException(string message, Type targetType, Exception innerException)
        : base(message, innerException)
    {
        TargetType = targetType;
    }
}
