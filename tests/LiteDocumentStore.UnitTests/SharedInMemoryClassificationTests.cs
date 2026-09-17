using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The keeper exists only for a shared-cache in-memory database, so what decides that is
/// load-bearing. It reuses <see cref="SqliteConnectionStringGuard"/>'s structural classification
/// rather than matching text — the same parser whose absence let five measured shapes past the
/// private-in-memory rejection.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SharedInMemoryClassificationTests
{
    /// <summary>
    /// Shapes the guard already pins, including the ones that defeated substring matching: a
    /// <c>#</c> fragment SQLite discards, a last-wins duplicate parameter, and the keyword losing
    /// to the query where both state a value.
    /// </summary>
    [Theory]
    // Genuinely shared in-memory: the keeper is needed.
    [InlineData("Data Source=file:shared-name?mode=memory&cache=shared", true)]
    [InlineData("Data Source=file::memory:?cache=shared", true)]
    [InlineData("Data Source=named;Mode=Memory;Cache=Shared", true)]
    [InlineData("Data Source=file:a%20b?mode=memory&cache=shared", true)]
    // The query beats the keyword where it states one, so this one is private.
    [InlineData("Data Source=file:x?mode=memory&cache=private;Cache=Shared", false)]
    // A fragment is discarded with everything after it, so the cache=shared never applies.
    [InlineData("Data Source=file:x?mode=memory#ignored&cache=shared", false)]
    // Last occurrence wins.
    [InlineData("Data Source=file:x?mode=memory&cache=shared&cache=private", false)]
    // Private per connection whatever the cache says.
    [InlineData("Data Source=:memory:;Cache=Shared", false)]
    [InlineData("Data Source=file::memory:", false)]
    // Case-sensitive, mirroring SQLite: these name no in-memory database at all.
    [InlineData("Data Source=file:x?mode=MEMORY&cache=shared", false)]
    // Ordinary file databases.
    [InlineData("Data Source=store.db", false)]
    [InlineData("Data Source=file:store.db?cache=shared", false)]
    public void IsSharedInMemory_ClassifiesStructurally(string connectionString, bool expected)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        Assert.Equal(expected, SqliteConnectionStringGuard.IsSharedInMemory(builder));
    }

    /// <summary>
    /// Every shape the guard <em>accepts</em> as in-memory needs a keeper, and nothing it rejects
    /// can reach one — a private in-memory database is refused before the pool is built, so a
    /// <c>true</c> here always means the database really does outlive a single connection.
    /// </summary>
    [Fact]
    public void IsSharedInMemory_AgreesWithThePresetsThatBuildInMemoryStores()
    {
        Assert.True(SqliteConnectionStringGuard.IsSharedInMemory(
            new SqliteConnectionStringBuilder(DocumentStoreOptions.ForInMemory().ConnectionString)));

        Assert.True(SqliteConnectionStringGuard.IsSharedInMemory(
            new SqliteConnectionStringBuilder(DocumentStoreOptions.ForSharedInMemory("named").ConnectionString)));

        Assert.False(SqliteConnectionStringGuard.IsSharedInMemory(
            new SqliteConnectionStringBuilder(DocumentStoreOptions.ForFile("some.db").ConnectionString)));
    }

    /// <summary>
    /// An unpooled connection (a blob read stream's) opened while the pool is being disposed must
    /// be closed rather than handed out: after the keeper closes, opening the shared-cache name
    /// again <em>recreates the database empty</em>, and the stream would query that instead of
    /// failing.
    /// </summary>
    /// <remarks>
    /// The count is the trap this pins as well. Unpooled connections never reach <c>Announce</c>,
    /// so they were never counted; closing one through the pooled discard helper would drive
    /// <c>ConnectionCount</c> below the number of pooled connections actually held. The assertion
    /// below is that it stays at zero, not merely that the open threw.
    /// </remarks>
    [Fact]
    public async Task CreateUnpooledConnectionAsync_WhenDisposedDuringTheOpen_ClosesItWithoutUncounting()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var factory = new GatedFactory(entered, release);

        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 2;
        var pool = new SqliteConnectionPool(options, factory, NullLogger<DocumentStore>.Instance);
        pool.Initialize();

        var countBefore = pool.ConnectionCount;

        factory.Gate = true;
        var opening = Task.Run(() => pool.CreateUnpooledConnectionAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the open never entered the factory");

        // Dispose while the open is parked inside the factory, then let it finish.
        pool.Dispose();
        release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);

        // The connection it opened was closed, not handed out...
        Assert.All(factory.Opened, connection => Assert.Equal(ConnectionState.Closed, connection.State));

        // ...and the count is untouched: an unpooled connection was never counted, so closing it
        // must not decrement either.
        Assert.Equal(countBefore, pool.ConnectionCount);
        Assert.Equal(0, pool.ConnectionCount);
    }

    private sealed class GatedFactory(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public bool Gate { get; set; }

        public List<SqliteConnection> Opened { get; } = [];

        private void WaitIfGated()
        {
            if (!Gate)
            {
                return;
            }

            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            WaitIfGated();
            var connection = _inner.CreateConnection(options);
            Opened.Add(connection);
            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            WaitIfGated();
            var connection = await _inner.CreateConnectionAsync(options, cancellationToken);
            Opened.Add(connection);
            return connection;
        }

        public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
            _inner.ConfigureConnection(connection, options);

        public Task ConfigureConnectionAsync(
            SqliteConnection connection,
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ConfigureConnectionAsync(connection, options, cancellationToken);
    }

    /// <summary>
    /// The lifecycle half: a pool over a shared in-memory database reserves its first connection
    /// instead of banking it, and one over a file database opens no keeper at all. Visible through
    /// <see cref="SqliteConnectionPool.ConnectionCount"/>, which counts only leasable connections.
    /// </summary>
    [Fact]
    public void Initialize_ReservesAKeeperOnlyForASharedInMemoryDatabase()
    {
        var memoryOptions = DocumentStoreOptions.ForInMemory();
        memoryOptions.MaxPoolSize = 2;

        using var memoryPool = new SqliteConnectionPool(
            memoryOptions, new DefaultConnectionFactory(), NullLogger<DocumentStore>.Instance);
        memoryPool.Initialize();

        // The keeper is un-pooled, so it is not counted and it did not go into the idle bag:
        // renting has to open a connection of its own.
        Assert.Equal(0, memoryPool.ConnectionCount);
        using (var lease = memoryPool.Rent())
        {
            Assert.Equal(1, memoryPool.ConnectionCount);
        }

        var path = Path.Combine(Path.GetTempPath(), $"lds-keeper-unit-{Guid.NewGuid():N}.db");
        try
        {
            var fileOptions = DocumentStoreOptions.ForFile(path);
            fileOptions.MaxPoolSize = 2;

            using var filePool = new SqliteConnectionPool(
                fileOptions, new DefaultConnectionFactory(), NullLogger<DocumentStore>.Instance);
            filePool.Initialize();

            // No keeper: the connection Initialize opened is banked and leasable, as before.
            Assert.Equal(1, filePool.ConnectionCount);
            using var fileLease = filePool.Rent();
            Assert.Equal(1, filePool.ConnectionCount);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // The temp directory keeps it.
                }
            }
        }
    }
}
