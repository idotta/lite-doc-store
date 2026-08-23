
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Regression tests for the pooled connection model: the store must be safe to share across
/// threads, and transactions must be isolated from one another.
/// </summary>
/// <remarks>
/// These use real file databases. A shared-cache in-memory database locks at table granularity,
/// so overlapping write transactions there fail with SQLITE_LOCKED regardless of the store's
/// design — only a file database exercises the WAL concurrency this model is built for.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ConnectionModelIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private sealed record Doc(string Name, int Value);

    private async Task<IDocumentStore> CreateFileStoreAsync(int? maxPoolSize = null, int? busyTimeoutMs = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-connmodel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var options = DocumentStoreOptions.ForFile(path);
        if (maxPoolSize is { } size)
        {
            options.MaxPoolSize = size;
        }

        if (busyTimeoutMs is { } timeout)
        {
            options.BusyTimeoutMs = timeout;
        }

        var store = await new DocumentStoreFactory().CreateAsync(options);
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    [Fact]
    public async Task ParallelOperations_OnSharedStore_AllSucceed()
    {
        await using var store = await CreateFileStoreAsync();
        const int operations = 200;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, operations),
            async (i, ct) =>
            {
                await store.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i));
                _ = await store.GetAsync<Doc>($"doc-{i}");
                _ = await store.CountAsync<Doc>();
            });

        Assert.Equal(operations, await store.CountAsync<Doc>());

        for (var i = 0; i < operations; i++)
        {
            var document = await store.GetAsync<Doc>($"doc-{i}");
            Assert.Equal(i, document?.Value);
        }
    }

    [Fact]
    public async Task ParallelReads_OnSharedStore_ReturnTheirOwnDocument()
    {
        await using var store = await CreateFileStoreAsync();
        for (var i = 0; i < 50; i++)
        {
            await store.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i));
        }

        // A reader that saw another reader's row would mean two threads sharing one command.
        var reads = Enumerable.Range(0, 400).Select(async i =>
        {
            var id = i % 50;
            var document = await store.GetAsync<Doc>($"doc-{id}");
            Assert.Equal($"name-{id}", document?.Name);
        });

        await Task.WhenAll(reads);
    }

    [Fact]
    public async Task ConcurrentTransactions_WhenOneRollsBack_DoNotDiscardTheOthersWrites()
    {
        await using var store = await CreateFileStoreAsync();

        var committing = Task.Run(async () =>
            await store.ExecuteInTransactionAsync(async tx =>
                await tx.UpsertAsync("committed", new Doc("committed", 1))));

        var failing = Task.Run(async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ExecuteInTransactionAsync(async tx =>
                {
                    await tx.UpsertAsync("rolled-back", new Doc("rolled-back", 2));
                    throw new InvalidOperationException("forced rollback");
                })));

        await Task.WhenAll(committing, failing);

        Assert.NotNull(await store.GetAsync<Doc>("committed"));
        Assert.Null(await store.GetAsync<Doc>("rolled-back"));
    }

    [Fact]
    public async Task ConcurrentWriteTransactions_AllCommit()
    {
        await using var store = await CreateFileStoreAsync();
        const int transactions = 32;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, transactions),
            async (i, ct) => await store.ExecuteInTransactionAsync(async tx =>
            {
                await tx.UpsertAsync($"tx-{i}", new Doc($"tx-{i}", i));
                await tx.PutBlobAsync($"tx-{i}", new byte[] { (byte)i });
            }, ct));

        Assert.Equal(transactions, await store.CountAsync<Doc>());
        for (var i = 0; i < transactions; i++)
        {
            Assert.NotNull(await store.GetAsync<Doc>($"tx-{i}"));
            Assert.True(await store.BlobExistsAsync($"tx-{i}"));
        }
    }

    [Fact]
    public async Task StoreRead_InsideTransactionCallback_DoesNotSeeUncommittedWrites()
    {
        await using var store = await CreateFileStoreAsync();

        // Only operations invoked on the transaction object are transactional. A read through
        // the store runs on a different connection, so it must not see the pending write — and
        // in WAL mode it does not block on it either.
        await store.ExecuteInTransactionAsync(async tx =>
        {
            await tx.UpsertAsync("pending", new Doc("pending", 1));

            Assert.NotNull(await tx.GetAsync<Doc>("pending"));
            Assert.Null(await store.GetAsync<Doc>("pending").WaitAsync(TimeSpan.FromSeconds(10)));
        });

        Assert.NotNull(await store.GetAsync<Doc>("pending"));
    }

    [Fact]
    public async Task StoreWrite_InsideTransactionCallback_FailsWithDatabaseLocked()
    {
        // Documented trap: a writing transaction holds the write lock, so a store write from
        // inside the callback waits on a lock only that transaction can release. Pinning the
        // behaviour here so any change to it is deliberate. Short busy timeout keeps it quick.
        await using var store = await CreateFileStoreAsync(busyTimeoutMs: 200);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            store.ExecuteInTransactionAsync(async tx =>
            {
                await tx.UpsertAsync("in-transaction", new Doc("in-transaction", 1));
                await store.UpsertAsync("outside-transaction", new Doc("outside-transaction", 2));
            }));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);

        // The transaction was rolled back by the failure, and its connection went back to the
        // pool in a usable state.
        Assert.Null(await store.GetAsync<Doc>("in-transaction"));
        Assert.Null(await store.GetAsync<Doc>("outside-transaction"));
        await store.UpsertAsync("after", new Doc("after", 3));
        Assert.NotNull(await store.GetAsync<Doc>("after"));
    }

    [Fact]
    public async Task Transaction_WhenDisposedWithoutCommit_RollsBack()
    {
        await using var store = await CreateFileStoreAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync("uncommitted", new Doc("uncommitted", 1));
            Assert.False(transaction.IsCommitted);
        }

        Assert.Null(await store.GetAsync<Doc>("uncommitted"));
    }

    [Fact]
    public async Task Transaction_AfterCommit_CannotBeReused()
    {
        await using var store = await CreateFileStoreAsync();

        await using var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("committed", new Doc("committed", 1));
        await transaction.CommitAsync();

        Assert.True(transaction.IsCommitted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        Assert.NotNull(await store.GetAsync<Doc>("committed"));
    }

    [Fact]
    public async Task Transaction_AfterCompletion_RejectsEveryDocumentOperation()
    {
        await using var store = await CreateFileStoreAsync();

        var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("committed", new Doc("committed", 1));
        await transaction.CommitAsync();

        var query = DocumentQuery<Doc>.Where("$.Name", QueryOperator.Equal, "committed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.UpsertAsync("late", new Doc("late", 2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.QueryAsync(query));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.CountAsync(query));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.PutBlobAsync("late", new byte[] { 1 }));

        await transaction.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.GetAsync<Doc>("committed"));

        // The rejected write never reached the database, and the committed one is still there.
        Assert.Null(await store.GetAsync<Doc>("late"));
        Assert.NotNull(await store.GetAsync<Doc>("committed"));
    }

    [Fact]
    public async Task Transaction_ExecuteRawAsync_SeesUncommittedWritesAndRollsBackWithThem()
    {
        await using var store = await CreateFileStoreAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.UpsertAsync("raw", new Doc("raw", 1));

            // Raw SQL on the transaction runs on its connection, so it sees the pending write.
            var pending = await transaction.ExecuteRawAsync(async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [Doc]";
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            });

            Assert.Equal(1, pending);
        }

        Assert.Equal(0, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task Transaction_ExecuteRawAsync_EnlistsOnlyCommandsCreatedFromTheConnection()
    {
        await using var store = await CreateFileStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.UpsertAsync("raw", new Doc("raw", 1));

        var viaCreateCommand = await transaction.ExecuteRawAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM [Doc]";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        });

        Assert.Equal(1, viaCreateCommand);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.ExecuteRawAsync(async (connection, ct) =>
            {
                await using var command = new SqliteCommand("SELECT COUNT(*) FROM [Doc]", connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Transactions_ReleaseTheirConnection_SoThePoolDoesNotStarve()
    {
        // One connection: a leaked lease would deadlock the next rent instead of failing.
        await using var store = await CreateFileStoreAsync(maxPoolSize: 1);

        for (var i = 0; i < 5; i++)
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i));
            await transaction.CommitAsync();
        }

        // Would hang forever if a committed transaction kept its connection.
        var count = await store.CountAsync<Doc>().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task ParallelOperations_WithSingleConnectionPool_AreSerializedNotCorrupted()
    {
        await using var store = await CreateFileStoreAsync(maxPoolSize: 1);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 50),
            async (i, ct) => await store.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i)));

        Assert.Equal(50, await store.CountAsync<Doc>());
    }

    [Fact]
    public async Task DisposeAsync_CheckpointsWalAndLeavesDataReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-connmodel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        var factory = new DocumentStoreFactory();

        await using (var store = await factory.CreateAsync(DocumentStoreOptions.ForFile(path)))
        {
            await store.CreateTableAsync<Doc>();
            await store.UpsertAsync("durable", new Doc("durable", 1));

            var journalMode = await store.ExecuteRawAsync((connection, ct) =>
                connection.QueryFirstStringAsync("PRAGMA journal_mode", ct));
            Assert.Equal("wal", journalMode, ignoreCase: true);
        }

        var walPath = path + "-wal";
        var walLength = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        Assert.Equal(0, walLength);

        await using var reopened = await factory.CreateAsync(DocumentStoreOptions.ForFile(path));
        Assert.Equal("durable", (await reopened.GetAsync<Doc>("durable"))?.Name);
    }

    [Fact]
    public async Task ConcurrentDispose_DoesNotThrow()
    {
        var store = await CreateFileStoreAsync();
        await store.UpsertAsync("doc", new Doc("doc", 1));

        var disposals = Enumerable.Range(0, 8)
            .Select(i => i % 2 == 0
                ? Task.Run(() => store.Dispose())
                : Task.Run(async () => await store.DisposeAsync()));

        await Task.WhenAll(disposals);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CountAsync<Doc>());
    }

    [Fact]
    public async Task OperationsInFlightDuringDispose_EitherCompleteOrThrowObjectDisposed()
    {
        var store = await CreateFileStoreAsync();

        var writes = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.UpsertAsync($"doc-{i}", new Doc($"name-{i}", i));
            }
            catch (ObjectDisposedException)
            {
                // Expected once disposal wins the race.
            }
        })).ToArray();

        await store.DisposeAsync();
        await Task.WhenAll(writes);   // must not surface any other exception
    }

    [Fact]
    public void PrivateInMemoryConnectionString_IsRejected()
    {
        var factory = new DocumentStoreFactory();

        Assert.Throws<ArgumentException>(() =>
            factory.Create(new DocumentStoreOptions("Data Source=:memory:")));
        Assert.Throws<ArgumentException>(() =>
            factory.Create(new DocumentStoreOptions("Data Source=test;Mode=Memory")));
    }

    [Fact]
    public async Task ForSharedInMemory_IsVisibleToASecondStoreWithTheSameName()
    {
        var cacheName = $"lds-shared-{Guid.NewGuid():N}";
        var factory = new DocumentStoreFactory();

        await using var writer = await factory.CreateAsync(DocumentStoreOptions.ForSharedInMemory(cacheName));
        await writer.CreateTableAsync<Doc>();
        await writer.UpsertAsync("shared", new Doc("shared", 1));

        await using var reader = await factory.CreateAsync(DocumentStoreOptions.ForSharedInMemory(cacheName));
        Assert.Equal("shared", (await reader.GetAsync<Doc>("shared"))?.Name);
    }

    [Fact]
    public async Task CreateAsync_WhenConnectionConfigurationFails_LeavesNoOpenHandle()
    {
        // The connection is opened before it is PRAGMA-configured, so a failing PRAGMA used to
        // strand an open handle inside the factory — the pool never sees that connection and the
        // store it belongs to is disposed on the way out.
        var path = Path.Combine(Path.GetTempPath(), $"lds-connmodel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var options = DocumentStoreOptions.ForFile(path);
        options.AdditionalPragmas.Add("PRAGMA journal_mode = NOT_A_MODE(");

        await Assert.ThrowsAsync<SqliteException>(() => new DocumentStoreFactory().CreateAsync(options));

        // Throws IOException on Windows while any connection still holds the database file.
        File.Delete(path);
        Assert.False(File.Exists(path));
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
