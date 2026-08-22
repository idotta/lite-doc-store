using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Regression tests for the SQLite 3.45+ guard: the store must refuse an old library when a
/// connection is opened, instead of failing later on the first write with
/// <c>no such function: jsonb</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqliteVersionGuardIntegrationTests
{
    private sealed record Doc(string Name, int Value);

    /// <summary>
    /// A factory whose connections report an arbitrary version. SQLite allows a user-defined
    /// function to override a built-in of the same name, which is the only way to exercise the
    /// too-old path without an actually-old native library.
    /// </summary>
    private sealed class VersionSpoofingConnectionFactory(string reportedVersion) : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            var connection = _inner.CreateConnection(options);
            Spoof(connection);
            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            var connection = await _inner.CreateConnectionAsync(options, cancellationToken);
            Spoof(connection);
            return connection;
        }

        public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
            _inner.ConfigureConnection(connection, options);

        public Task ConfigureConnectionAsync(
            SqliteConnection connection,
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ConfigureConnectionAsync(connection, options, cancellationToken);

        private void Spoof(SqliteConnection connection) =>
            connection.CreateFunction("sqlite_version", () => reportedVersion);
    }

    private static SqliteConnectionPool CreatePool(IConnectionFactory factory) =>
        new(DocumentStoreOptions.ForInMemory(), factory, NullLogger.Instance);

    [Fact]
    public async Task Pool_WithATooOldSqlite_ThrowsWhenTheConnectionIsOpened()
    {
        using var pool = CreatePool(new VersionSpoofingConnectionFactory("3.44.2"));

        var ex = await Assert.ThrowsAsync<UnsupportedSqliteVersionException>(() => pool.InitializeAsync());

        Assert.Equal("3.44.2", ex.ActualVersion);
        Assert.Equal(new Version(3, 45, 0), ex.MinimumVersion);

        // The rejected connection must not be counted or leaked into the idle bag.
        Assert.Equal(0, pool.ConnectionCount);
    }

    [Fact]
    public void Pool_WithATooOldSqlite_ThrowsOnTheSynchronousOpenPath()
    {
        using var pool = CreatePool(new VersionSpoofingConnectionFactory("3.44.2"));

        Assert.Throws<UnsupportedSqliteVersionException>(() => pool.Initialize());
        Assert.Equal(0, pool.ConnectionCount);
    }

    [Fact]
    public async Task Pool_WithATooOldSqlite_ReleasesTheSlotSoTheFailureIsNotAPoolLeak()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 1;

        using var pool = new SqliteConnectionPool(
            options, new VersionSpoofingConnectionFactory("3.44.2"), NullLogger.Instance);

        // Two attempts on a single-slot pool: the second can only run if the first handed its
        // slot back, and it must report the version rather than time out.
        await Assert.ThrowsAsync<UnsupportedSqliteVersionException>(() => pool.RentAsync().AsTask());

        var second = await Assert.ThrowsAsync<UnsupportedSqliteVersionException>(
            () => pool.RentAsync(TimeSpan.FromSeconds(5)).AsTask());

        Assert.Equal("3.44.2", second.ActualVersion);
    }

    [Fact]
    public async Task Pool_WithAnUnparsableVersion_Throws()
    {
        using var pool = CreatePool(new VersionSpoofingConnectionFactory("definitely not a version"));

        var ex = await Assert.ThrowsAsync<UnsupportedSqliteVersionException>(() => pool.InitializeAsync());

        Assert.Equal("definitely not a version", ex.ActualVersion);
    }

    [Fact]
    public async Task Store_OnTheBundledSqlite_OpensAndTheReportedVersionSupportsJsonb()
    {
        await using var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await store.CreateTableAsync<Doc>();

        // The write itself is the guard's premise: it goes through jsonb().
        await store.UpsertAsync("a", new Doc("first", 1));
        Assert.Equal(new Doc("first", 1), await store.GetAsync<Doc>("a"));

        var reported = await store.ExecuteRawAsync(
            (connection, ct) => new SchemaIntrospector(connection).GetSqliteVersionAsync(ct));

        Assert.True(Version.Parse(reported) >= new Version(3, 45, 0), $"SQLite {reported} is too old for JSONB");
        Assert.True(await store.IsHealthyAsync());
    }

    [Fact]
    public async Task IsHealthyAsync_WithATooOldSqlite_ReturnsFalseInsteadOfThrowing()
    {
        var options = DocumentStoreOptions.ForInMemory();

        // The store is built on a healthy factory so it can open at all, then the version is
        // spoofed on the connection it already holds — that is what the health check re-reads.
        await using var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.ExecuteRawAsync((connection, _) =>
        {
            connection.CreateFunction("sqlite_version", () => "3.44.2");
            return Task.CompletedTask;
        });

        Assert.False(await store.IsHealthyAsync());
    }
}
