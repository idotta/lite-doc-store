using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Pins what a consumer <see cref="IConnectionFactory"/> inherits by delegating to
/// <see cref="DefaultConnectionFactory"/> instead of reimplementing it.
/// </summary>
/// <remarks>
/// Both settings asserted here are ones the store does <em>not</em> re-check behind the factory —
/// unlike <see cref="DocumentStoreOptions.PageSize"/>, which the pool reads back, and the
/// in-memory/WAL combination, which options validation refuses. Measured against a factory that
/// applies the PRAGMAs naively instead of delegating: <c>PRAGMA foreign_keys</c> came back 1
/// rather than 0, and <c>DefaultTimeout</c> stayed at the provider's 30 rather than 1. A decorator
/// that forwards to the default factory gets both right for free, and that is what these assert.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DelegatingConnectionFactoryIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    /// <summary>
    /// The shape a consumer writes once <see cref="DefaultConnectionFactory"/> ships: hold one,
    /// forward to it, add nothing. Adding nothing is the point — if delegation did not carry the
    /// PRAGMA correctness, this class could not be the whole implementation.
    /// </summary>
    private sealed class DelegatingConnectionFactory : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public int Created;

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            Interlocked.Increment(ref Created);
            return _inner.CreateConnection(options);
        }

        public Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Created);
            return _inner.CreateConnectionAsync(options, cancellationToken);
        }

        public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
            _inner.ConfigureConnection(connection, options);

        public Task ConfigureConnectionAsync(
            SqliteConnection connection,
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ConfigureConnectionAsync(connection, options, cancellationToken);
    }

    private DocumentStoreOptions FileOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-delegating-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={path};Pooling=False",
            EnableWalMode = true,
            PageSize = 0,
        };
    }

    [Fact]
    public async Task DelegatingFactory_WithForeignKeysDisabled_StatesTheOffThatANaiveFactoryOmits()
    {
        var options = FileOptions();
        options.EnableForeignKeys = false;

        var factory = new DelegatingConnectionFactory();
        await using var store = new DocumentStoreFactory(factory).Create(options);

        var foreignKeys = await store.ExecuteRawAsync(async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        });

        Assert.True(factory.Created > 0);
        Assert.Equal(0, foreignKeys);
    }

    [Fact]
    public async Task DelegatingFactory_DerivesTheProviderCommandTimeoutFromBusyTimeoutMs()
    {
        var options = FileOptions();
        options.BusyTimeoutMs = 250;

        var factory = new DelegatingConnectionFactory();
        await using var store = new DocumentStoreFactory(factory).Create(options);

        var defaultTimeout = await store.ExecuteRawAsync(
            (connection, ct) => Task.FromResult(connection.DefaultTimeout));

        Assert.True(factory.Created > 0);
        Assert.Equal(1, defaultTimeout);
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A failed test may still hold the file; the temp directory keeps it.
            }
        }
    }
}
