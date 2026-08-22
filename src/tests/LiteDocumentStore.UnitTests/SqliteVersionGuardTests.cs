using LiteDocumentStore.Exceptions;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="SqliteVersionGuard"/>. The grammar is exercised through the
/// internal <c>Validate</c> entry point, because an actually-too-old SQLite library cannot be
/// produced in a test run — the version string is the only input the guard has.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteVersionGuardTests
{
    [Fact]
    public void MinimumVersion_IsTheFirstReleaseWithJsonb()
    {
        Assert.Equal(new Version(3, 45, 0), SqliteVersionGuard.MinimumVersion);
    }

    [Theory]
    [InlineData("3.45.0")]
    [InlineData("3.45.1")]
    [InlineData("3.50.3")]
    [InlineData("4.0.0")]
    public void Validate_WithASupportedVersion_ReturnsIt(string reported)
    {
        var version = SqliteVersionGuard.Validate(reported);

        Assert.Equal(Version.Parse(reported), version);
    }

    [Theory]
    [InlineData("3.44.2")]
    [InlineData("3.45")]      // 3.45 parses as 3.45.-1, which is below 3.45.0
    [InlineData("3.7.17")]
    [InlineData("2.8.17")]
    public void Validate_WithATooOldVersion_ThrowsWithBothVersions(string reported)
    {
        var ex = Assert.Throws<UnsupportedSqliteVersionException>(() => SqliteVersionGuard.Validate(reported));

        Assert.Equal(Version.Parse(reported).ToString(), ex.ActualVersion);
        Assert.Equal(SqliteVersionGuard.MinimumVersion, ex.MinimumVersion);
        Assert.Contains("3.45.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("jsonb()", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    [InlineData("3.45.0-beta")]
    public void Validate_WithAnUnparsableVersion_ThrowsAndKeepsTheRawValue(string? reported)
    {
        var ex = Assert.Throws<UnsupportedSqliteVersionException>(() => SqliteVersionGuard.Validate(reported));

        Assert.Equal(reported, ex.ActualVersion);
        Assert.Equal(SqliteVersionGuard.MinimumVersion, ex.MinimumVersion);
        Assert.Contains("Could not determine the SQLite version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedSqliteVersionException_IsALiteDocumentStoreException()
    {
        // Callers catching the base exception must keep catching this one.
        var ex = new UnsupportedSqliteVersionException("boom", "3.44.2", new Version(3, 45, 0));

        Assert.IsAssignableFrom<LiteDocumentStoreException>(ex);
        Assert.Equal("boom", ex.Message);
        Assert.Equal("3.44.2", ex.ActualVersion);
        Assert.Equal(new Version(3, 45, 0), ex.MinimumVersion);
    }

    [Fact]
    public void EnsureSupported_OnARealConnection_PassesAndReportsTheLoadedVersion()
    {
        // The bundled library must satisfy the guard, or nothing else in the suite could work.
        using var pool = new SqliteConnectionPool(
            DocumentStoreOptions.ForInMemory(),
            new DefaultConnectionFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        using var lease = pool.Rent();

        var version = SqliteVersionGuard.EnsureSupported(lease.Connection);

        Assert.True(version >= SqliteVersionGuard.MinimumVersion);
    }

    [Fact]
    public async Task EnsureSupportedAsync_OnARealConnection_MatchesTheSynchronousPath()
    {
        using var pool = new SqliteConnectionPool(
            DocumentStoreOptions.ForInMemory(),
            new DefaultConnectionFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await using var lease = await pool.RentAsync();

        var asyncVersion = await SqliteVersionGuard.EnsureSupportedAsync(lease.Connection);
        var syncVersion = SqliteVersionGuard.EnsureSupported(lease.Connection);

        Assert.Equal(syncVersion, asyncVersion);
    }
}
