using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Regression tests for the pool's dirty-session guard: a raw-SQL callback that leaves
/// transaction state on the connection must not poison the next renter.
/// </summary>
/// <remarks>
/// <para>
/// Every store runs with <c>MaxPoolSize = 1</c>, so the connection the callback touched is
/// necessarily the one the next operation gets. Without the guard each of these leaves the pool
/// holding a connection whose statements enlist in a stranded transaction, or whose
/// <c>BeginTransaction</c> and <c>Close</c> throw for the rest of the process.
/// </para>
/// <para>
/// The assertion is a store transaction rather than a plain write, and that matters: three of
/// the four shapes let a plain write through — two by silently enlisting it in the stranded
/// transaction — so "the next operation succeeded" would pass on a poisoned connection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RawSqlSessionStateIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private sealed record Doc(string Name, int Value);

    private async Task<IDocumentStore> CreateStoreAsync(string path)
    {
        var options = DocumentStoreOptions.ForFile(path);
        options.MaxPoolSize = 1;

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Doc>();
        return store;
    }

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-rawsession-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Proves the connection the callback poisoned is not the one still serving the store: a
    /// transaction is the only operation every one of these shapes breaks.
    /// </summary>
    private static async Task AssertStoreStillUsableAsync(IDocumentStore store)
    {
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("after", new Doc("after", 1));
        await transaction.CommitAsync();

        Assert.Equal(new Doc("after", 1), await store.GetAsync<Doc>("after"));
    }

    [Fact]
    public async Task ExecuteRawAsync_LeavingARawBeginOpen_DoesNotPoisonTheNextOperation()
    {
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        await store.ExecuteRawAsync((connection, _) =>
        {
            Execute(connection, "BEGIN");
            Execute(connection, $"INSERT INTO [{store.GetTableName<Doc>()}](id, data, version) " +
                                "VALUES('leaked', jsonb('{\"Name\":\"leaked\",\"Value\":1}'), 1)");
            return Task.CompletedTask;
        });

        await AssertStoreStillUsableAsync(store);

        // Closing the connection rolled the stranded transaction back, so its write is gone.
        Assert.False(await RowExistsOutsideTheStoreAsync(path, store.GetTableName<Doc>(), "leaked"));
    }

    [Fact]
    public async Task ExecuteRawAsync_AbandoningATransactionObject_DoesNotPoisonTheNextOperation()
    {
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        await store.ExecuteRawAsync((connection, _) =>
        {
            // Neither committed nor disposed — the missed-disposable bug, on the raw connection.
            var abandoned = connection.BeginTransaction();
            Execute(connection, $"INSERT INTO [{store.GetTableName<Doc>()}](id, data, version) " +
                                "VALUES('leaked', jsonb('{\"Name\":\"leaked\",\"Value\":1}'), 1)");
            GC.KeepAlive(abandoned);
            return Task.CompletedTask;
        });

        await AssertStoreStillUsableAsync(store);
        Assert.False(await RowExistsOutsideTheStoreAsync(path, store.GetTableName<Doc>(), "leaked"));
    }

    [Fact]
    public async Task ExecuteRawAsync_CommittingOutOfBand_DoesNotPoisonTheNextOperation()
    {
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        await store.ExecuteRawAsync((connection, _) =>
        {
            // SQLite's autocommit flag ends up clean here; what is left behind is the provider's
            // transaction object, which breaks BeginTransaction and Close on the connection.
            var stale = connection.BeginTransaction();
            Execute(connection, $"INSERT INTO [{store.GetTableName<Doc>()}](id, data, version) " +
                                "VALUES('committed', jsonb('{\"Name\":\"committed\",\"Value\":1}'), 1)");
            Execute(connection, "COMMIT");
            GC.KeepAlive(stale);
            return Task.CompletedTask;
        });

        await AssertStoreStillUsableAsync(store);

        // The write committed before the connection was discarded, so it stays: closing a
        // connection cannot undo a transaction SQLite has already committed.
        Assert.True(await RowExistsOutsideTheStoreAsync(path, store.GetTableName<Doc>(), "committed"));
    }

    [Fact]
    public async Task ExecuteRawAsync_RollingBackOutOfBand_DoesNotPoisonTheNextOperation()
    {
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        await store.ExecuteRawAsync((connection, _) =>
        {
            // The provider's rollback hook completes the transaction but leaves it attached, and
            // every later command on the connection then throws "has completed".
            var stale = connection.BeginTransaction();
            Execute(connection, $"INSERT INTO [{store.GetTableName<Doc>()}](id, data, version) " +
                                "VALUES('leaked', jsonb('{\"Name\":\"leaked\",\"Value\":1}'), 1)");
            Execute(connection, "ROLLBACK");
            GC.KeepAlive(stale);
            return Task.CompletedTask;
        });

        await AssertStoreStillUsableAsync(store);
        Assert.False(await RowExistsOutsideTheStoreAsync(path, store.GetTableName<Doc>(), "leaked"));
    }

    [Fact]
    public async Task ExecuteRawAsync_LeakingATransactionRepeatedly_DoesNotExhaustThePool()
    {
        // Each leak costs a connection, not a slot: the discard releases the slot, so the pool
        // opens a fresh connection instead of queueing the next caller forever.
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        for (int i = 0; i < 3; i++)
        {
            await store.ExecuteRawAsync((connection, _) =>
            {
                Execute(connection, "BEGIN");
                return Task.CompletedTask;
            });

            await store.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i));
        }

        await AssertStoreStillUsableAsync(store);
        Assert.Equal(4, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task ExecuteRawAsync_WhoseCallbackThrows_SurfacesItsOwnExceptionAndKeepsTheSlot()
    {
        // The return runs in a finally, so neither the probe nor the discard may replace the
        // exception the caller is waiting for.
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ExecuteRawAsync<int>((connection, _) =>
            {
                Execute(connection, "BEGIN");
                throw new InvalidOperationException("callback failed");
            }));

        Assert.Equal("callback failed", failure.Message);
        await AssertStoreStillUsableAsync(store);
    }

    [Fact]
    public async Task TransactionExecuteRawAsync_CommittingOutOfBand_DoesNotPoisonTheNextOperation()
    {
        // On a transaction the guard is the one that already existed: the commit fails, disposal
        // marks the connection compromised and discards it. Pinned here because the two paths
        // share the pool and only one of them runs the new checks.
        var path = NewDatabasePath();
        await using var store = await CreateStoreAsync(path);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync("in-tx", new Doc("in-tx", 1));
            await transaction.ExecuteRawAsync((connection, _) =>
            {
                Execute(connection, "COMMIT");
                return Task.CompletedTask;
            });

            await Assert.ThrowsAnyAsync<Exception>(() => transaction.CommitAsync());
        }

        await AssertStoreStillUsableAsync(store);
    }

    private static async Task<bool> RowExistsOutsideTheStoreAsync(string path, string table, string id)
    {
        // Read on a connection of our own: with MaxPoolSize = 1 the store's own read would be
        // served by whatever connection is left in the pool, which is the thing under test.
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // Best effort: a leaked handle should fail the test that leaked it, not cleanup.
                    }
                }
            }
        }
    }
}
