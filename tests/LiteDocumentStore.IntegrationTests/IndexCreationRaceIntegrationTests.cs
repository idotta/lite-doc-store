using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The name look-up and the <c>CREATE INDEX</c> are two statements, so the name can be claimed
/// between them. The executed statement keeps <c>IF NOT EXISTS</c>, which is right for two
/// callers racing to create the <em>same</em> index and wrong for a conflicting one: there it
/// makes the create a silent no-op and the caller is told their definition is in place when
/// someone else's is — the very failure the collision guard exists to kill, reached by a race.
/// </summary>
/// <remarks>
/// <para>
/// Pinned deterministically rather than by racing, through the seam
/// <see cref="BlobSavepointCancellationIntegrationTests"/> established:
/// <see cref="SqliteConnection.CreateCommand"/> is virtual, and a custom
/// <see cref="IConnectionFactory"/> delegating to <see cref="DefaultConnectionFactory"/> can hand
/// the store a subclass that observes every statement while keeping correct PRAGMA behaviour.
/// The path issues the look-up <c>SELECT</c> (0) then the <c>CREATE</c> (1), so running the
/// interfering DDL as command 1 is created lands exactly in the window, with no timing involved.
/// </para>
/// <para>
/// The ordinal is asserted rather than assumed — each test checks that the command it interfered
/// with really was the <c>CREATE</c> — so a change to the statement sequence fails the test
/// instead of quietly moving the interference somewhere harmless.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class IndexCreationRaceIntegrationTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private sealed record Member(string Id, string? Email, int Age);

    /// <summary>Runs a statement on a second connection as the n-th command is created.</summary>
    private sealed class InterferingConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private readonly List<SqliteCommand> _created = [];
        private int _armedAt = -1;
        private string? _interference;
        private string? _interferenceConnectionString;

        public void RunAsCommandIsCreated(int ordinal, string connectionStringForInterference, string sql)
        {
            _created.Clear();
            _armedAt = ordinal;
            _interferenceConnectionString = connectionStringForInterference;
            _interference = sql;
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
                    _armedAt = -1; // fire once; the interference itself must not re-enter
                    using var other = new SqliteConnection(_interferenceConnectionString);
                    other.Open();
                    using var interfering = other.CreateCommand();
                    interfering.CommandText = _interference;
                    interfering.ExecuteNonQuery();
                }
            }

            return command;
        }
    }

    /// <summary>Hands the store the interfering connection; everything else is the default factory's.</summary>
    private sealed class InterferingConnectionFactory : IConnectionFactory
    {
        private readonly DefaultConnectionFactory _inner = new();

        public InterferingConnection? Connection { get; private set; }

        public SqliteConnection CreateConnection(DocumentStoreOptions options)
        {
            var connection = new InterferingConnection(options.ConnectionString);
            connection.Open();
            _inner.ConfigureConnection(connection, options);
            Connection = connection;
            return connection;
        }

        public async Task<SqliteConnection> CreateConnectionAsync(
            DocumentStoreOptions options,
            CancellationToken cancellationToken = default)
        {
            var connection = new InterferingConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await _inner.ConfigureConnectionAsync(connection, options, cancellationToken);
            Connection = connection;
            return connection;
        }
    }

    private DocumentStoreOptions FileOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-index-race-{Guid.NewGuid():N}.db");
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
    public async Task CreateIndexAsync_WhenAConflictingIndexClaimsTheNameMidCreate_Throws()
    {
        var factory = new InterferingConnectionFactory();
        var options = FileOptions();
        await using var store = new DocumentStoreFactory(factory).Create(options);
        await store.CreateTableAsync<Member>();

        var table = store.GetTableName<Member>();
        var indexName = $"idx_{table}_Email";

        // Claim the name with a *different* definition as the CREATE is built. The store's own
        // statement carries IF NOT EXISTS, so it silently no-ops against it.
        factory.Connection!.RunAsCommandIsCreated(
            1,
            options.ConnectionString,
            $"CREATE INDEX [{indexName}] ON [{table}] (json_extract(data, '$.Age'))");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateIndexAsync<Member>(x => x.Email!));

        Assert.StartsWith("CREATE INDEX IF NOT EXISTS", factory.Connection!.TextOf(1), StringComparison.Ordinal);
        Assert.Contains(indexName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Age')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json_extract(data, '$.Email')", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexAsync_WhenAnIdenticalIndexClaimsTheNameMidCreate_IsAbsorbed()
    {
        var factory = new InterferingConnectionFactory();
        var options = FileOptions();
        await using var store = new DocumentStoreFactory(factory).Create(options);
        await store.CreateTableAsync<Member>();

        var table = store.GetTableName<Member>();
        var indexName = $"idx_{table}_Email";

        // The same race with the *same* definition must still succeed: two callers creating one
        // index is what IF NOT EXISTS is for, and refusing it would be the wrong over-correction.
        factory.Connection!.RunAsCommandIsCreated(
            1,
            options.ConnectionString,
            $"CREATE INDEX [{indexName}] ON [{table}] (json_extract(data, '$.Email'))");

        await store.CreateIndexAsync<Member>(x => x.Email!);

        Assert.StartsWith("CREATE INDEX IF NOT EXISTS", factory.Connection!.TextOf(1), StringComparison.Ordinal);

        var stored = await store.ExecuteRawAsync((connection, ct) => connection.QueryFirstStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @Name",
            ct,
            ("Name", indexName)));

        Assert.Equal(
            SqlGenerator.GenerateCreateJsonIndexSql(table, indexName, "$.Email", null, ifNotExists: false),
            stored);
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + "-wal");
                File.Delete(path + "-shm");
            }
            catch (IOException)
            {
                // A stray temp file is not worth failing a test run over.
            }
        }
    }
}
