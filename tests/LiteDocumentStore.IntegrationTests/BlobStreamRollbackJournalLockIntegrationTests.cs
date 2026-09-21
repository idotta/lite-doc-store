using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Pins what an open blob read stream does to a concurrent writer on a real <em>file</em> database
/// running the rollback journal (<c>EnableWalMode = false</c>) — the third journal mode, alongside
/// the WAL and shared-cache in-memory answers recorded in <c>docs/DESIGN-RATIONALE.md</c>.
/// </summary>
/// <remarks>
/// The lock is database-wide there, not table-level as under a shared cache, and SQLite <em>does</em>
/// run the busy handler, so the writer waits and then fails with plain <c>SQLITE_BUSY</c> rather than
/// failing immediately. Only a file database can exhibit this, so there is no unit-test counterpart.
/// Every case carries a positive control: the same write succeeds once the stream is disposed.
/// </remarks>
[Trait("Category", "Integration")]
public class BlobStreamRollbackJournalLockIntegrationTests : IAsyncLifetime
{
    private const int BusyTimeoutMs = 100;
    private const int SqliteBusy = 5;

    private readonly List<string> _databasePaths = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        // A read stream holds its connection until disposed or finalized.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _databasePaths)
        {
            // A rollback-journal database's sidecar is "-journal"; "-wal"/"-shm" are listed too so
            // the cleanup stays total if a mutation or a future edit flips the journal mode.
            foreach (var file in new[] { path, $"{path}-journal", $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // A handle the provider has not finalized yet; the temp directory keeps it.
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-rbjlock-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private static DocumentStoreOptions RollbackJournalOptions(string path) =>
        new DocumentStoreOptionsBuilder()
            .UseFile(path)
            .WithWalMode(false)
            .WithBusyTimeout(BusyTimeoutMs)
            .Build();

    /// <summary>
    /// Seeds a blob and a document, then returns a reader store holding an open stream over the
    /// blob and a second store — a separate connection over the same file — to contend with it.
    /// </summary>
    private async Task<(IDocumentStore Writer, IDocumentStore Reader, Stream Stream)> ArrangeAsync()
    {
        var path = NewDatabasePath();
        var factory = new DocumentStoreFactory();

        var writer = await factory.CreateAsync(RollbackJournalOptions(path));
        await writer.CreateBlobTableAsync();
        await writer.CreateTableAsync<Person>();
        await writer.PutBlobAsync("blob/held", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        await writer.UpsertAsync("p-seed", new Person { Name = "Seed", Age = 1, Email = "seed@example.com" });

        var reader = await factory.CreateAsync(RollbackJournalOptions(path));

        // A measurement taken in the wrong mode is worse than no measurement.
        foreach (var store in new[] { writer, reader })
        {
            var mode = await store.ExecuteRawAsync((connection, ct) =>
                connection.QueryFirstStringAsync("PRAGMA journal_mode", ct));
            Assert.Equal("delete", mode, StringComparer.OrdinalIgnoreCase);
        }

        var stream = await reader.OpenBlobReadAsync("blob/held");
        Assert.NotNull(stream);

        return (writer, reader, stream);
    }

    [Fact]
    public async Task OpenBlobReadAsync_BlobTableWriteWhileStreamHeld_FailsWithSqliteBusy()
    {
        var (writer, reader, stream) = await ArrangeAsync();
        await using var w = writer;
        await using var r = reader;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var busy = await Assert.ThrowsAsync<SqliteException>(
            () => writer.PutBlobAsync("blob/other", new byte[] { 9, 9, 9 }));
        elapsed.Stop();

        Assert.Equal(SqliteBusy, busy.SqliteErrorCode);
        Assert.Equal(SqliteBusy, busy.SqliteExtendedErrorCode);

        // A generous lower bound only: the busy handler waits, so this is not an immediate refusal.
        // Never an upper bound — the provider re-runs the attempt until its own command timeout.
        Assert.True(
            elapsed.ElapsedMilliseconds >= BusyTimeoutMs / 2,
            $"expected a wait of at least {BusyTimeoutMs / 2} ms, got {elapsed.ElapsedMilliseconds} ms");

        // Positive control: the same write on the same store succeeds once the stream is gone.
        await stream.DisposeAsync();
        await writer.PutBlobAsync("blob/other", new byte[] { 9, 9, 9 });
        Assert.True(await writer.BlobExistsAsync("blob/other"));
    }

    [Fact]
    public async Task OpenBlobReadAsync_DocumentTableWriteWhileStreamHeld_FailsWithSqliteBusy()
    {
        var (writer, reader, stream) = await ArrangeAsync();
        await using var w = writer;
        await using var r = reader;

        var person = new Person { Name = "Contender", Age = 2, Email = "contender@example.com" };

        // Database-wide, unlike the shared-cache in-memory case where only the blob table locks.
        var busy = await Assert.ThrowsAsync<SqliteException>(() => writer.UpsertAsync("p-1", person));
        Assert.Equal(SqliteBusy, busy.SqliteErrorCode);
        Assert.Equal(SqliteBusy, busy.SqliteExtendedErrorCode);

        // Positive control.
        await stream.DisposeAsync();
        await writer.UpsertAsync("p-1", person);
        Assert.NotNull(await writer.GetAsync<Person>("p-1"));
    }

    [Fact]
    public async Task OpenBlobReadAsync_BlobTableReadsWhileStreamHeld_Succeed()
    {
        var (writer, reader, stream) = await ArrangeAsync();
        await using var w = writer;
        await using var r = reader;
        await using var s = stream;

        var metadata = await writer.GetBlobMetadataAsync("blob/held");
        Assert.NotNull(metadata);
        Assert.Equal(8, metadata.Length);
        Assert.True(await writer.BlobExistsAsync("blob/held"));
        Assert.NotNull(await writer.GetAsync<Person>("p-seed"));
    }

    [Fact]
    public async Task OpenBlobReadAsync_StreamReadFromWhileHeld_BlocksTheWriterTheSameWay()
    {
        var (writer, reader, stream) = await ArrangeAsync();
        await using var w = writer;
        await using var r = reader;

        // The rowid lookup happens at open, so reading bytes out changes nothing.
        var buffer = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(buffer.AsMemory(0, 4)));

        var busy = await Assert.ThrowsAsync<SqliteException>(
            () => writer.PutBlobAsync("blob/other", new byte[] { 7, 7 }));
        Assert.Equal(SqliteBusy, busy.SqliteErrorCode);

        // Positive control.
        await stream.DisposeAsync();
        await writer.PutBlobAsync("blob/other", new byte[] { 7, 7 });
        Assert.True(await writer.BlobExistsAsync("blob/other"));
    }
}
