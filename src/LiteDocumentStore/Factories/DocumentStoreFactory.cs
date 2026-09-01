using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDocumentStore;

/// <summary>
/// Default implementation of <see cref="IDocumentStoreFactory"/>.
/// Creates <see cref="DocumentStore"/> instances with all dependencies composed.
/// </summary>
public sealed class DocumentStoreFactory : IDocumentStoreFactory
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ITableNamingConvention _tableNamingConvention;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Initializes a new instance of DocumentStoreFactory with default dependencies.
    /// </summary>
    public DocumentStoreFactory()
        : this(new DefaultConnectionFactory())
    {
    }

    /// <summary>
    /// Initializes a new instance of DocumentStoreFactory with a custom connection factory.
    /// </summary>
    /// <param name="connectionFactory">The connection factory to use</param>
    public DocumentStoreFactory(IConnectionFactory connectionFactory)
        : this(connectionFactory, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of DocumentStoreFactory with all dependencies.
    /// </summary>
    /// <param name="connectionFactory">The connection factory to use</param>
    /// <param name="tableNamingConvention">Table naming convention (optional, defaults to DefaultTableNamingConvention)</param>
    /// <param name="loggerFactory">Logger factory for creating loggers (optional)</param>
    public DocumentStoreFactory(
        IConnectionFactory connectionFactory,
        ITableNamingConvention? tableNamingConvention,
        ILoggerFactory? loggerFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _tableNamingConvention = tableNamingConvention ?? DefaultTableNamingConvention.Instance;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IDocumentStore Create(DocumentStoreOptions options)
    {
        var store = CreateStore(options);

        try
        {
            store.Initialize();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IDocumentStore> CreateAsync(
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        var store = CreateStore(options);

        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private DocumentStore CreateStore(DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        // Use options-level overrides if provided, otherwise use factory defaults
        var namingConvention = options.TableNamingConvention ?? _tableNamingConvention;
        var logger = _loggerFactory?.CreateLogger<DocumentStore>() ?? NullLogger<DocumentStore>.Instance;

        return new DocumentStore(options, _connectionFactory, namingConvention, logger);
    }
}
