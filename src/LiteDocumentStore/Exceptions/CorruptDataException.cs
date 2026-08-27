namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Exception thrown when a row exists but its payload cannot be read: a document whose
/// <c>data</c> column is SQL NULL or deserializes to null, or a blob whose <c>data</c> column
/// holds something other than a BLOB.
/// </summary>
/// <remarks>
/// <para>
/// Row presence decides not-found; the payload decides corrupt. An absent id is still absent —
/// <c>GetAsync</c> returns default and <c>GetBlobAsync</c> returns null — so this exception only
/// ever describes a row that is really there.
/// </para>
/// <para>
/// It is distinct from <see cref="DocumentSerializationException"/>, which reports a JSON
/// serialization or deserialization failure. Stored JSON that is well-formed but incompatible
/// with the target type still surfaces as <see cref="DocumentSerializationException"/> from the
/// serializer, since nothing about the row itself is corrupt.
/// </para>
/// <para>
/// None of the store's own DDL can produce such a row: the document tables and the blob table
/// both declare <c>data BLOB NOT NULL</c>. It is reachable through raw SQL, or on a table a
/// consumer created themselves with a nullable or untyped <c>data</c> column.
/// </para>
/// </remarks>
public class CorruptDataException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the id of the row whose payload could not be read.
    /// </summary>
    public string? Id { get; }

    /// <summary>
    /// Gets the table the row was read from.
    /// </summary>
    public string? TableName { get; }

    /// <summary>
    /// Gets the type the payload was being read as, or null for a blob, which is raw bytes and
    /// has no target type.
    /// </summary>
    public Type? TargetType { get; }

    /// <summary>
    /// Gets the SQLite storage class the <c>data</c> column actually held, in SQLite's own
    /// lowercase spelling — <c>"null"</c>, <c>"text"</c>, <c>"integer"</c> or <c>"real"</c> for a
    /// blob that is not a blob. Null for a document, whose reads project <c>json(data)</c> and
    /// never observe the storage class.
    /// </summary>
    public string? StoredTypeName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CorruptDataException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CorruptDataException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CorruptDataException"/> class for a row whose
    /// payload could not be read.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="id">The id of the row.</param>
    /// <param name="tableName">The table the row was read from.</param>
    /// <param name="targetType">The type the payload was being read as, or null for a blob.</param>
    /// <param name="storedTypeName">
    /// The SQLite storage class the <c>data</c> column held, or null when it was not observed.
    /// </param>
    public CorruptDataException(
        string message,
        string? id,
        string? tableName,
        Type? targetType = null,
        string? storedTypeName = null)
        : base(message)
    {
        Id = id;
        TableName = tableName;
        TargetType = targetType;
        StoredTypeName = storedTypeName;
    }
}
