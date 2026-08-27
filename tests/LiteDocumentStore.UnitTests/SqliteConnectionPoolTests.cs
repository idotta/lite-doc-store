using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="SqliteConnectionPool"/> — the component that makes the store
/// thread-safe by renting a connection per operation.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteConnectionPoolTests
{
    private static SqliteConnectionPool CreatePool(int maxPoolSize = 4)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = maxPoolSize;

        return new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Initialize_OpensOneConnection()
    {
        using var pool = CreatePool();

        pool.Initialize();

        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_ReturnsAnOpenConnection()
    {
        using var pool = CreatePool();

        await using var lease = await pool.RentAsync();

        Assert.Equal(ConnectionState.Open, lease.Connection.State);
    }

    [Fact]
    public async Task RentAsync_AfterReturn_ReusesTheSameConnection()
    {
        using var pool = CreatePool();

        SqliteConnection first;
        await using (var lease = await pool.RentAsync())
        {
            first = lease.Connection;
        }

        await using var second = await pool.RentAsync();

        Assert.Same(first, second.Connection);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhileLeasesAreHeld_OpensDistinctConnections()
    {
        using var pool = CreatePool(maxPoolSize: 3);

        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();
        await using var third = await pool.RentAsync();

        Assert.NotSame(first.Connection, second.Connection);
        Assert.NotSame(second.Connection, third.Connection);
        Assert.Equal(3, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhenPoolIsSaturated_WaitsForAReturnedConnection()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        lease.Dispose();

        await using var second = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(lease.Connection, second.Connection);
        Assert.Equal(1, pool.ConnectionCount);
    }

    [Fact]
    public async Task RentAsync_WhenSaturatedAndCancelled_ThrowsAndKeepsTheSlot()
    {
        using var pool = CreatePool(maxPoolSize: 1);
        using var cancellation = new CancellationTokenSource();

        var held = await pool.RentAsync();

        var queued = pool.RentAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // The cancelled waiter must not have consumed the only slot.
        held.Dispose();
        await using var next = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Open, next.Connection.State);
    }

    [Fact]
    public async Task Discard_ClosesTheConnectionAndFreesItsSlot()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var discarded = lease.Connection;
        lease.Discard();

        Assert.Equal(ConnectionState.Closed, discarded.State);
        Assert.Equal(0, pool.ConnectionCount);

        // The slot is free again, and the next rent opens a fresh connection.
        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(discarded, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task Return_AfterConnectionWasClosedExternally_DoesNotRecycleIt()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var broken = lease.Connection;
        broken.Close();
        lease.Dispose();

        await using var replacement = await pool.RentAsync();
        Assert.NotSame(broken, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task Return_WithATransactionStillPending_DoesNotRecycleTheConnection()
    {
        // A raw BEGIN a caller never finished. Recycling the connection would let the next
        // renter's statements silently enlist in that transaction.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        Execute(poisoned, "BEGIN");
        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, poisoned.State);
        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
        Assert.Equal(ConnectionState.Open, replacement.Connection.State);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithACleanConnection_RecyclesIt()
    {
        // The raw path must not discard indiscriminately: a well-behaved callback costs nothing.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var connection = lease.Connection;
        lease.ReturnAfterExternalAccess();

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, pool.ConnectionCount);

        await using var next = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(connection, next.Connection);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithATransactionStillPending_DoesNotRecycleTheConnection()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        Execute(poisoned, "BEGIN");
        lease.ReturnAfterExternalAccess();

        Assert.Equal(ConnectionState.Closed, poisoned.State);
        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
    }

    [Fact]
    public async Task ReturnAfterExternalAccess_WithAManagedTransactionStillAttached_DoesNotRecycleTheConnection()
    {
        // The shape SQLite's own autocommit flag cannot see: a raw COMMIT leaves the provider's
        // transaction object attached, which breaks the next renter's BeginTransaction and makes
        // closing the connection throw. Only the raw path pays for this check.
        using var pool = CreatePool(maxPoolSize: 1);

        var lease = await pool.RentAsync();
        var poisoned = lease.Connection;
        var stale = poisoned.BeginTransaction();
        Execute(poisoned, "COMMIT");
        lease.ReturnAfterExternalAccess();

        Assert.Equal(0, pool.ConnectionCount);

        await using var replacement = await pool.RentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(poisoned, replacement.Connection);
        await using var transaction = await replacement.Connection.BeginTransactionAsync();

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task Return_OnADisposedPool_ReleasesTheSlotEvenWhenClosingThrows()
    {
        // Closing a connection whose transaction object survived a raw COMMIT throws
        // "cannot rollback - no transaction is active". A throw here used to skip the slot
        // release, and a waiter parked in RentAsync only ever wakes on a free slot.
        var pool = CreatePool(maxPoolSize: 1);
        var lease = await pool.RentAsync();
        var stale = lease.Connection.BeginTransaction();
        Execute(lease.Connection, "COMMIT");

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        pool.Dispose();
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10)));

        GC.KeepAlive(stale);
    }

    [Fact]
    public async Task Dispose_ClosesIdleConnections()
    {
        var pool = CreatePool();
        SqliteConnection connection;
        await using (var lease = await pool.RentAsync())
        {
            connection = lease.Connection;
        }

        pool.Dispose();

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Dispose_ClosesAConnectionReturnedAfterwards()
    {
        var pool = CreatePool();
        var lease = await pool.RentAsync();

        pool.Dispose();
        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, lease.Connection.State);
    }

    [Fact]
    public async Task RentAsync_OnDisposedPool_ThrowsObjectDisposedException()
    {
        var pool = CreatePool();
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pool.RentAsync());
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
        Assert.Throws<ObjectDisposedException>(pool.Initialize);
    }

    [Fact]
    public async Task Dispose_WithAQueuedRent_CompletesTheWaiterInsteadOfHangingIt()
    {
        // Disposing _slots under a parked WaitAsync used to leave this rent pending forever.
        var pool = CreatePool(maxPoolSize: 1);
        var held = await pool.RentAsync();

        var queued = pool.RentAsync().AsTask();
        Assert.False(queued.IsCompleted);

        pool.Dispose();
        held.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Rent_WithATimeout_OnASaturatedPool_ThrowsTimeoutException()
    {
        using var pool = CreatePool(maxPoolSize: 1);
        await using var held = await pool.RentAsync();

        Assert.Throws<TimeoutException>(() => pool.Rent(TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await pool.RentAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Rent_WithATimeout_KeepsTheSlotWhenItExpires()
    {
        using var pool = CreatePool(maxPoolSize: 1);

        using (var held = await pool.RentAsync())
        {
            Assert.Throws<TimeoutException>(() => pool.Rent(TimeSpan.FromMilliseconds(50)));
        }

        // The timed-out rent must not have consumed the slot it never acquired.
        await using var second = await pool.RentAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Open, second.Connection.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPoolSize_BelowOne_ThrowsNamingTheOption(int maxPoolSize)
    {
        var options = DocumentStoreOptions.ForInMemory();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxPoolSize = maxPoolSize);
        Assert.True(options.MaxPoolSize >= 1);
    }

    [Fact]
    public void Constructor_WithEmptyConnectionString_ThrowsArgumentException()
    {
        var options = new DocumentStoreOptions { MaxPoolSize = 2 };

        Assert.Throws<ArgumentException>(() =>
            new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance));
    }

    [Fact]
    public void Constructor_WithPrivateInMemoryConnectionString_ThrowsArgumentException()
    {
        var options = new DocumentStoreOptions("Data Source=:memory:");

        var error = Assert.Throws<ArgumentException>(() =>
            new SqliteConnectionPool(options, new DefaultConnectionFactory(), NullLogger.Instance));

        Assert.Contains(nameof(DocumentStoreOptions.ForInMemory), error.Message);
    }

    [Fact]
    public async Task Pool_DisablesTheBuiltInSqlitePool()
    {
        // The store's whole reason for owning a pool is that it configures each physical
        // connection once; Microsoft.Data.Sqlite's pool would hand back handles whose session
        // PRAGMAs it does not preserve.
        using var pool = CreatePool();

        await using var lease = await pool.RentAsync();

        var builder = new SqliteConnectionStringBuilder(lease.Connection.ConnectionString);
        Assert.False(builder.Pooling);
    }

    [Fact]
    public async Task Pool_AppliesConfiguredPragmasToEveryConnection()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 2;
        options.BusyTimeoutMs = 1234;

        using var pool = new SqliteConnectionPool(
            options, new DefaultConnectionFactory(), NullLogger.Instance);

        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();

        foreach (var lease in new[] { first, second })
        {
            var timeout = await lease.Connection.ExecuteScalarAsync<long>("PRAGMA busy_timeout", CancellationToken.None);
            Assert.Equal(1234, timeout);
        }
    }
}
