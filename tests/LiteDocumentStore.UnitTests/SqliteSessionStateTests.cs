using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="SqliteSessionState"/> — the two probes that decide whether a
/// connection coming back to the pool still carries transaction state.
/// </summary>
/// <remarks>
/// The shapes are the ones a consumer's raw SQL can leave behind. Each was measured against
/// Microsoft.Data.Sqlite 10.0.11: neither probe subsumes the other, so both are pinned per
/// shape rather than only through <see cref="SqliteSessionState.IsSessionDirty"/>.
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
        Assert.False(SqliteSessionState.HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));
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
        Assert.False(SqliteSessionState.HasManagedTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));
    }

    [Fact]
    public void RawBegin_IsSeenOnlyByTheAutocommitProbe()
    {
        using var connection = OpenConnection();

        Execute(connection, "BEGIN");

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(SqliteSessionState.HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out var reason));
        Assert.Contains("pending", reason);

        // Seen by the cheap check alone, which is what every store operation pays for.
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: false, out _));

        Execute(connection, "ROLLBACK");
    }

    [Fact]
    public void RawSavepointOutsideATransaction_IsSeenOnlyByTheAutocommitProbe()
    {
        using var connection = OpenConnection();

        Execute(connection, "SAVEPOINT sp");

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(SqliteSessionState.HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));

        Execute(connection, "RELEASE sp");
    }

    [Fact]
    public void AnAbandonedManagedTransaction_IsSeenByBothProbes()
    {
        using var connection = OpenConnection();

        var abandoned = connection.BeginTransaction();

        Assert.True(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(SqliteSessionState.HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));

        abandoned.Rollback();
    }

    [Fact]
    public void RawCommitUnderAManagedTransaction_IsSeenOnlyByTheManagedProbe()
    {
        using var connection = OpenConnection();

        // The provider watches for an out-of-band ROLLBACK but not for a COMMIT, so the
        // transaction object stays attached with nothing left to roll back. SQLite's own
        // autocommit flag is clean here, which is why the cheap probe alone is not enough.
        var stale = connection.BeginTransaction();
        Execute(connection, "COMMIT");

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(SqliteSessionState.HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out var reason));
        Assert.Contains("attached", reason);

        // Invisible without the managed probe — the reason the raw-connection paths pay for it.
        Assert.False(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: false, out _));

        // Closing this connection as-is throws "cannot rollback - no transaction is active" — the
        // reason the pool closes through CloseQuietly. Give the attached transaction something to
        // roll back so that this test's own cleanup succeeds.
        Execute(connection, "BEGIN");
        GC.KeepAlive(stale);
    }

    [Fact]
    public void RawRollbackUnderAManagedTransaction_IsSeenOnlyByTheManagedProbe()
    {
        using var connection = OpenConnection();

        // Here the provider's rollback hook does complete the transaction, but it stays attached,
        // and every later command on the connection throws "This SqliteTransaction has completed".
        var stale = connection.BeginTransaction();
        Execute(connection, "ROLLBACK");

        Assert.False(SqliteSessionState.HasPendingTransaction(connection));
        Assert.True(SqliteSessionState.HasManagedTransaction(connection));
        Assert.True(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: false, out _));

        GC.KeepAlive(stale);
    }

    [Fact]
    public void OnAClosedConnection_TheManagedProbeAnswersAndTheAutocommitProbeThrows()
    {
        // Why IsSessionDirty checks State before calling the autocommit probe, and why the pool
        // guards both behind its own State check and a catch.
        var connection = OpenConnection();
        connection.Close();

        Assert.False(SqliteSessionState.HasManagedTransaction(connection));
        Assert.Throws<ArgumentNullException>(() => SqliteSessionState.HasPendingTransaction(connection));
        Assert.False(SqliteSessionState.IsSessionDirty(connection, includeManagedTransaction: true, out _));

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
