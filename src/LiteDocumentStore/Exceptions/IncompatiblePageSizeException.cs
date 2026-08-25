namespace LiteDocumentStore.Exceptions;

/// <summary>
/// Exception thrown when <see cref="DocumentStoreOptions.PageSize"/> could not be applied to the
/// database a connection opened, because the database already has a different page size.
/// </summary>
/// <remarks>
/// <c>PRAGMA page_size</c> is silently ignored once a database has been written, so without this
/// check the option would appear to be honoured while the database kept its original page size.
/// An existing database can only be converted by a <c>VACUUM</c>, and only while it is not in WAL
/// mode. Set <see cref="DocumentStoreOptions.PageSize"/> to <c>0</c> to accept whatever page size
/// the database already has.
/// </remarks>
public class IncompatiblePageSizeException : LiteDocumentStoreException
{
    /// <summary>
    /// Gets the page size the options asked for.
    /// </summary>
    public int RequestedPageSize { get; }

    /// <summary>
    /// Gets the page size the database actually reports.
    /// </summary>
    public int ActualPageSize { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IncompatiblePageSizeException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public IncompatiblePageSizeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IncompatiblePageSizeException"/> class with an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public IncompatiblePageSizeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IncompatiblePageSizeException"/> class with the
    /// requested and actual page sizes.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="requestedPageSize">The page size the options asked for.</param>
    /// <param name="actualPageSize">The page size the database reports.</param>
    public IncompatiblePageSizeException(string message, int requestedPageSize, int actualPageSize)
        : base(message)
    {
        RequestedPageSize = requestedPageSize;
        ActualPageSize = actualPageSize;
    }
}
