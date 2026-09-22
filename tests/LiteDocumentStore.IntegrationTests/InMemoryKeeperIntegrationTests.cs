using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// A shared-cache in-memory database is destroyed when its last connection closes, so every site
/// that closes an <em>established</em> pooled connection could take the whole database with it.
/// The pool reserves the connection it opens in <c>Initialize</c> as a keeper so that cannot
/// happen.
/// </summary>
/// <remarks>
/// Measured before the keeper existed: a caller leaving a raw <c>BEGIN</c> in an
/// <c>ExecuteRawAsync</c> callback made the dirty-session guard discard the only connection, and
/// the database went with it — silently, with ordinary store operations then succeeding against a
/// fresh empty one. These pin every path that reaches such a close: the four documented dirty
/// shapes, the <c>State != Open</c> branch, and a transaction whose connection is compromised.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class InMemoryKeeperIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IDocumentStore> StoreWithCanaryAsync(DocumentStoreOptions options)
    {
        var store = new DocumentStoreFactory().Create(options);
        await store.ExecuteRawAsync((connection, cancellationToken) => ExecuteAsync(
            connection,
            "CREATE TABLE canary(id INTEGER PRIMARY KEY); INSERT INTO canary VALUES (1);",
            cancellationToken));
        return store;
    }

    private static Task<long> CanaryRowsAsync(IDocumentStore store) =>
        store.ExecuteRawAsync(async (connection, cancellationToken) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM canary;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        });

    private static DocumentStoreOptions InMemoryOptions(int maxPoolSize = 1)
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = maxPoolSize;
        return options;
    }

    /// <summary>The four shapes the dirty-session guard documents, each of which discards.</summary>
    [Theory]
    [InlineData("BEGIN;")]
    [InlineData("SAVEPOINT sp;")]
    [InlineData("BEGIN; COMMIT; BEGIN;")]
    [InlineData("BEGIN; ROLLBACK; BEGIN;")]
    public async Task DirtySessionDiscard_LeavesTheInMemoryDatabaseIntact(string dirtySql)
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        await store.ExecuteRawAsync((connection, cancellationToken) =>
            ExecuteAsync(connection, dirtySql, cancellationToken));

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>
    /// The new discard site: a raw callback that left nothing behind still retires the connection,
    /// so at <c>MaxPoolSize = 1</c> the last leasable connection to the in-memory database closes
    /// on every <c>ExecuteRawAsync</c>. Only the reserved keeper stops the database going with it.
    /// </summary>
    [Fact]
    public async Task CleanExternalAccessDiscard_LeavesTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        for (int i = 0; i < 3; i++)
        {
            await store.ExecuteRawAsync((_, _) => Task.CompletedTask);
        }

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>An undisposed provider transaction: the second documented shape.</summary>
    [Fact]
    public async Task UndisposedProviderTransactionDiscard_LeavesTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        await store.ExecuteRawAsync((connection, _) =>
        {
            var undisposed = connection.BeginTransaction();
            GC.KeepAlive(undisposed);
            return Task.CompletedTask;
        });

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary><c>ReturnCore</c>'s <c>State != Open</c> branch.</summary>
    [Fact]
    public async Task ClosedConnectionDiscard_LeavesTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        await store.ExecuteRawAsync((connection, _) =>
        {
            connection.Close();
            return Task.CompletedTask;
        });

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>
    /// A transaction whose connection the caller closed underneath it: the lease leaves through
    /// the transaction's own release path rather than an ordinary operation's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each step is asserted on its own rather than under one <c>try</c> over the whole sequence.
    /// With a single catch, a <c>BeginTransactionAsync</c> or <c>ExecuteRawAsync</c> that threw
    /// first would leave the canary intact and pass the test without the connection ever having
    /// been closed — the test would pin nothing. Reaching the canary check now means the close
    /// happened, the commit failed <em>on it</em>, and disposal handed the lease back.
    /// </para>
    /// <para>
    /// What it still does <strong>not</strong> discriminate is <c>_lease.Discard()</c> from
    /// <c>_lease.Dispose()</c>: a closed connection reaches <c>ReturnCore</c>'s
    /// <c>State != Open</c> discard either way, so the canary would survive under both. It proves
    /// the route — a transaction, rather than a bare operation, ending on a connection that must
    /// be closed — not which call site inside <c>Release</c> ends it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TransactionEndingOnAClosedConnection_LeavesTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        var transaction = await store.BeginTransactionAsync();
        await transaction.ExecuteRawAsync((connection, _) =>
        {
            connection.Close();
            return Task.CompletedTask;
        });

        // The commit is what must fail, and it must fail on the closed connection: CommitAsync
        // releases only on success, so the lease is still held when this returns.
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());

        // Disposal rolls back, fails on the same closed connection, and hands the lease back.
        await transaction.DisposeAsync();

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>
    /// The pool's live connection count reaching zero is what used to destroy the database, and it
    /// is reachable at any <see cref="DocumentStoreOptions.MaxPoolSize"/> once enough connections
    /// have been discarded — the cap is not a count.
    /// </summary>
    [Fact]
    public async Task RepeatedDiscardsAtALargerPool_LeaveTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions(maxPoolSize: 4));

        for (var i = 0; i < 6; i++)
        {
            await store.ExecuteRawAsync((connection, cancellationToken) =>
                ExecuteAsync(connection, "BEGIN;", cancellationToken));
        }

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>
    /// The other half of the keeper: it must not outlive the store. A keeper still open after
    /// disposal is a leaked handle <em>and</em> an in-memory database the caller believes is gone.
    /// </summary>
    [Fact]
    public async Task DisposingTheStore_ReleasesTheKeeperSoTheDatabaseIsGone()
    {
        var options = InMemoryOptions();
        var store = await StoreWithCanaryAsync(options);
        await store.DisposeAsync();

        // A fresh connection to the same shared-cache name must find nothing: if the keeper were
        // still open the table would still be there.
        await using var probe = new SqliteConnection(options.ConnectionString);
        await probe.OpenAsync();
        using var command = probe.CreateCommand();
        command.CommandText = "SELECT count(*) FROM canary;";

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        Assert.Contains("no such table: canary", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A file database never had this problem and must not change behaviour.</summary>
    [Fact]
    public async Task FileDatabase_IsUnaffectedByTheSameDiscard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-keeper-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        await using var store = await StoreWithCanaryAsync(new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={path};Pooling=False",
            EnableWalMode = true,
            PageSize = 0,
            MaxPoolSize = 1,
        });

        await store.ExecuteRawAsync((connection, cancellationToken) =>
            ExecuteAsync(connection, "BEGIN;", cancellationToken));

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    /// <summary>
    /// <c>TryTakeIdle</c>'s own discard branch: a connection that returned <em>clean</em> into the
    /// idle bag and is only closed afterwards, so the next rent finds it non-open and discards it
    /// there rather than in <c>ReturnCore</c>.
    /// </summary>
    /// <remarks>
    /// Reaching it means holding the callback's connection past the callback, which the
    /// <c>ExecuteRawAsync</c> contract forbids — but forbidden is not unreachable, and this is the
    /// one discard site that closes a connection the pool had already accepted as healthy.
    /// </remarks>
    [Fact]
    public async Task IdleConnectionClosedAfterReturning_LeavesTheInMemoryDatabaseIntact()
    {
        await using var store = await StoreWithCanaryAsync(InMemoryOptions());

        SqliteConnection? captured = null;
        await store.ExecuteRawAsync((connection, _) =>
        {
            captured = connection;
            return Task.CompletedTask;
        });

        // It went back clean; closing it now makes the next rent take the TryTakeIdle discard.
        captured!.Close();

        Assert.Equal(1, await CanaryRowsAsync(store));
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A failed test may still hold the file; the temp directory keeps it.
                }
            }
        }
    }
}
