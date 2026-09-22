using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="SqliteSessionState"/> — the probe that decides whether a connection
/// coming back to the pool still carries transaction state.
/// </summary>
/// <remarks>
/// The shapes are the ones a consumer's raw SQL can leave behind, each measured against
/// Microsoft.Data.Sqlite 10.0.11. Two of them are invisible to the probe and are pinned as such:
/// the provider's own attached-transaction view disagrees with SQLite's autocommit flag in both
/// directions, which is why a connection a caller has had raw access to is discarded outright
/// rather than probed. <see cref="HasManagedTransaction"/> below is that second view, kept here
/// as the observation and not in the library, which no longer asks the question.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SqliteSessionStateTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lds-session-{Guid.NewGuid():N}");

    private SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_directory, "session.db")};Pooling=False");
        connection.Open();
        Execute(connection, "CREATE TABLE IF NOT EXISTS t(id INTEGER PRIMARY KEY, v TEXT)");
        return connection;
    }

    // The provider view SqliteSessionState deliberately does not probe: CreateCommand copies the
    // connection's attached transaction onto every command it makes.
    private static bool HasManagedTransaction(SqliteConnection connection)
    {
        using var probe = connection.CreateCommand();
        return probe.Transaction is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void CleanConnection_IsNotDirty()
    {
        using var connection = OpenConnection();

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, out _));
    }

    [Fact]
    public void AfterACommittedManagedTransaction_IsNotDirty()
    {
        using var connection = OpenConnection();

        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, "INSERT INTO t(v) VALUES('committed')");
            transaction.Commit();
        }

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, out _));
    }

    [Fact]
    public void RawBegin_IsDirty()
    {
        using var connection = OpenConnection();

        Execute(connection, "BEGIN");

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, out var reason));
        Assert.Contains("pending", reason);

        Execute(connection, "ROLLBACK");
    }

    [Fact]
    public void RawSavepointOutsideATransaction_IsDirty()
    {
        using var connection = OpenConnection();

        Execute(connection, "SAVEPOINT sp");

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, out _));

        Execute(connection, "RELEASE sp");
    }

    [Fact]
    public void AnAbandonedManagedTransaction_IsDirty()
    {
        using var connection = OpenConnection();

        var abandoned = connection.BeginTransaction();

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, out _));

        abandoned.Rollback();
    }

    [Fact]
    public void RawCommitUnderAManagedTransaction_IsInvisibleToTheProbe()
    {
        using var connection = OpenConnection();

        // The provider watches for an out-of-band ROLLBACK but not for a COMMIT, so the
        // transaction object stays attached with nothing left to roll back while SQLite's own
        // autocommit flag reads clean. The probe therefore calls this connection usable, and it
        // is not: this is the shape that makes discarding after raw access unconditional rather
        // than a verdict.
        var stale = connection.BeginTransaction();
        Execute(connection, "COMMIT");

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, out _));

        // Closing this connection as-is throws "cannot rollback - no transaction is active" — the
        // reason the pool closes through CloseQuietly. Give the attached transaction something to
        // roll back so that this test's own cleanup succeeds.
        Execute(connection, "BEGIN");
        GC.KeepAlive(stale);
    }

    [Fact]
    public void RawRollbackUnderAManagedTransaction_IsInvisibleToTheProbe()
    {
        using var connection = OpenConnection();

        // Here the provider's rollback hook does complete the transaction, but it stays attached,
        // and every later command on the connection throws "This SqliteTransaction has completed".
        // Clean to the probe, like the COMMIT shape above.
        var stale = connection.BeginTransaction();
        Execute(connection, "ROLLBACK");

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, out _));

        GC.KeepAlive(stale);
    }

    [Fact]
    public void OnAClosedConnection_TheAutocommitProbeThrows()
    {
        // Why IsSessionDirty checks State before calling the autocommit probe, and why the pool
        // guards it behind its own State check and a catch.
        var connection = OpenConnection();
        connection.Close();

        Assert.False(HasManagedTransaction(connection));
        Assert.Throws<ArgumentNullException>(() => SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, out _));

        connection.Dispose();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle a test leaked should fail that test, not this cleanup.
        }
    }
}
