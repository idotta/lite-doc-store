using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// Extension methods for configuring LiteDocumentStore services in an <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <see cref="IDocumentStore"/> is always registered as a singleton. The store is thread-safe
/// and rents a connection per operation from its own pool, so there is nothing for a scoped or
/// transient registration to isolate — it would only multiply connection pools. Size the pool
/// with <see cref="DocumentStoreOptions.MaxPoolSize"/> instead, and use
/// <see cref="IDocumentStore.BeginTransactionAsync(CancellationToken)"/> for per-request units of work.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds LiteDocumentStore services with a singleton, thread-safe document store.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to</param>
    /// <param name="configureOptions">A delegate to configure the <see cref="DocumentStoreOptions"/></param>
    /// <returns>The <see cref="IServiceCollection"/> for method chaining</returns>
    public static IServiceCollection AddLiteDocumentStore(
        this IServiceCollection services,
        Action<DocumentStoreOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new DocumentStoreOptions();
        configureOptions(options);

        return services.AddLiteDocumentStore(options);
    }

    /// <summary>
    /// Adds LiteDocumentStore services with pre-configured options.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to</param>
    /// <param name="options">The pre-configured <see cref="DocumentStoreOptions"/></param>
    /// <returns>The <see cref="IServiceCollection"/> for method chaining</returns>
    public static IServiceCollection AddLiteDocumentStore(
        this IServiceCollection services,
        DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        AddCoreServices(services);

        // One store per database, shared by every consumer: it is thread-safe and owns the
        // connection pool.
        services.TryAddSingleton<IDocumentStore>(
            sp => sp.GetRequiredService<IDocumentStoreFactory>().Create(options));

        return services;
    }


    /// <summary>
    /// Adds a keyed LiteDocumentStore document store for managing multiple databases.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to</param>
    /// <param name="serviceKey">The key to identify this store instance</param>
    /// <param name="configureOptions">A delegate to configure the <see cref="DocumentStoreOptions"/></param>
    /// <returns>The <see cref="IServiceCollection"/> for method chaining</returns>
    public static IServiceCollection AddKeyedLiteDocumentStore(
        this IServiceCollection services,
        object serviceKey,
        Action<DocumentStoreOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new DocumentStoreOptions();
        configureOptions(options);

        return services.AddKeyedLiteDocumentStore(serviceKey, options);
    }

    /// <summary>
    /// Adds a keyed LiteDocumentStore document store for managing multiple databases.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to</param>
    /// <param name="serviceKey">The key to identify this store instance</param>
    /// <param name="options">The pre-configured <see cref="DocumentStoreOptions"/></param>
    /// <returns>The <see cref="IServiceCollection"/> for method chaining</returns>
    public static IServiceCollection AddKeyedLiteDocumentStore(
        this IServiceCollection services,
        object serviceKey,
        DocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(options);

        AddCoreServices(services);

        services.TryAddKeyedSingleton<IDocumentStore>(
            serviceKey,
            (sp, _) => sp.GetRequiredService<IDocumentStoreFactory>().Create(options));

        return services;
    }

    /// <summary>
    /// Registers the stateless dependencies shared by every store (connection factory, naming
    /// convention, store factory).
    /// </summary>
    private static void AddCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton<IConnectionFactory, DefaultConnectionFactory>();
        services.TryAddSingleton<ITableNamingConvention, DefaultTableNamingConvention>();

        services.TryAddSingleton<IDocumentStoreFactory>(sp => new DocumentStoreFactory(
            sp.GetRequiredService<IConnectionFactory>(),
            sp.GetRequiredService<ITableNamingConvention>(),
            sp.GetService<ILoggerFactory>()));
    }
}
