namespace LiteDocumentStore;

/// <summary>
/// The metadata a blob write records alongside the payload.
/// </summary>
/// <remarks>
/// Passed through an <em>overload</em> of the write methods rather than an inserted parameter, so
/// callers that pass a trailing <see cref="CancellationToken"/> positionally still compile. The
/// cost is that <c>PutBlobAsync(id, data, default)</c> becomes ambiguous — <c>default</c> matches
/// both this type and <see cref="CancellationToken"/> — and needs a cast to say which it means.
/// </remarks>
public sealed class BlobWriteOptions
{
    /// <summary>
    /// The content type to record, e.g. <c>application/pdf</c>. Null records none.
    /// </summary>
    /// <remarks>
    /// Stored verbatim as a bound parameter and never interpreted, so any media type, charset
    /// parameter or private value is preserved as written. Blank is rejected rather than stored:
    /// it carries no more information than null and would read back as a content type that is
    /// present but empty.
    /// </remarks>
    public string? ContentType { get; init; }

    /// <summary>
    /// Throws when the options cannot be honoured.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="ContentType"/> is present but blank.
    /// </exception>
    internal void Validate()
    {
        if (ContentType is not null && string.IsNullOrWhiteSpace(ContentType))
        {
            throw new ArgumentException(
                "ContentType cannot be blank. Use null to record no content type.",
                nameof(ContentType));
        }
    }
}
