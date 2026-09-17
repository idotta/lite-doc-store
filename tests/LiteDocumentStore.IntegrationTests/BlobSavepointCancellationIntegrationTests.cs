using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The streamed blob write inside a caller's transaction runs in a savepoint, and the
/// <c>RELEASE</c> that closes it must not honour the caller's token: by then every declared byte
/// is written, so a cancellation there would report failure while leaving the write committable
/// in the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// The window is a few microseconds wide in production — measured before the fix, an
/// adversarially-timed cancel landed in it 15 times in 600 attempts, and every one of those 15
/// threw <em>and</em> persisted. It is pinned deterministically instead of by racing, through the
/// one seam that exists: <see cref="SqliteConnection.CreateCommand"/> is virtual, and the
/// statements this path issues go through it in a fixed order — <c>SAVEPOINT</c>, the reserve
/// <c>INSERT</c>, then <c>RELEASE</c>. The payload itself does not appear, because incremental
/// blob I/O writes through <c>SqliteBlob</c> rather than a command, which is exactly what makes
/// the third creation the instant after the copy has finished.
/// </para>
/// <para>
/// So cancelling as the third command is created lands precisely in the window, with no timing
/// involved. The ordinal is asserted rather than assumed: the test checks that the command created
/// at that point really was the <c>RELEASE</c>, so a change to the statement sequence fails the
/// test instead of quietly moving the cancel somewhere harmless.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class BlobSavepointCancellationIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    /// <summary>Cancels as the n-th command is created, and records what each one turned out to be.</summary>
    private sealed class CommandCountingConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private readonly List<SqliteCommand> _created = [];
        private int _armedAt = -1;
        private CancellationTokenSource? _cancellation;

        public void CancelAsCommandIsCreated(int ordinal, CancellationTokenSource cancellation)
        {
            _created.Clear();
            _armedAt = ordinal;
            _cancellation = cancellation;
        }

        public string TextOf(int ordinal) =>
            ordinal < _created.Count ? _created[ordinal].CommandText ?? string.Empty : string.Empty;

        public override SqliteCommand CreateCommand()
        {
            var command = base.CreateCommand();

            if (_armedAt >= 0)
            {
                _created.Add(command);
                if (_created.Count - 1 == _armedAt)
                {
                    _cancellation!.Cancel();
                }
            }

            return command;
        }
    }

    /// <summary>Hands the store the counting connection; everything else is the default factory's.</summary>
    private sealed class CountingConnectionFactory : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public CommandCountingConnection? Connection { get; private set; }

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            var connection = new CommandCountingConnection(options.ConnectionString);
            connection.Open();
            _inner.ConfigureConnection(connection, options);
            Connection = connection;
            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            var connection = new CommandCountingConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await _inner.ConfigureConnectionAsync(connection, options, cancellationToken);
            Connection = connection;
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

    private DocumentStoreOptions FileOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-savepoint-cancel-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        return new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={path};Pooling=False",
            EnableWalMode = true,
            PageSize = 0,
            BusyTimeoutMs = 1000,
            MaxPoolSize = 1,
        };
    }

    [Fact]
    public async Task StreamedBlobWriteInATransaction_CancelledAtTheRelease_CompletesInsteadOfReportingAFailureThatPersists()
    {
        var factory = new CountingConnectionFactory();
        await using var store = new DocumentStoreFactory(factory).Create(FileOptions());
        await store.CreateBlobTableAsync();
        await store.PutBlobAsync("payload", new byte[] { 1, 2, 3 });

        var replacement = new byte[64];
        Random.Shared.NextBytes(replacement);

        using var cancellation = new CancellationTokenSource();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            // From here the path issues SAVEPOINT (0), the reserve INSERT (1), then RELEASE (2).
            // Cancelling as (2) is created is the instant after the copy and before the release.
            factory.Connection!.CancelAsCommandIsCreated(2, cancellation);

            using var source = new MemoryStream(replacement);
            await transaction.PutBlobAsync("payload", source, replacement.Length, cancellation.Token);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // The cancel really did land on the release, not somewhere harmless.
        Assert.StartsWith("RELEASE", factory.Connection!.TextOf(2), StringComparison.Ordinal);
        Assert.True(cancellation.IsCancellationRequested);

        // And the write the caller was never told had failed is the one that is there.
        var stored = await store.GetBlobAsync("payload");
        Assert.NotNull(stored);
        Assert.Equal(replacement, stored);
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
