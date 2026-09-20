using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// A hand-written <see cref="IMigration"/> skips <see cref="SqlMigration"/>'s constructor guard, so
/// the runner applies the same floor at every entry point: the collection ones through
/// <c>Validate</c>, and the two single-migration ones directly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MigrationVersionFloorTests : IDisposable
{
    private readonly SqliteConnection _connection =
        new($"Data Source=migration-floor-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    public MigrationVersionFloorTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private sealed class HandWrittenMigration(long version) : IMigration
    {
        public long Version => version;

        public string Name => "HandWritten";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ApplyMigrationsAsync_WithANonPositiveVersion_ThrowsNamingMigrations(long version)
    {
        var runner = new MigrationRunner(_connection);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ApplyMigrationsAsync([new HandWrittenMigration(version)], new MigrationOptions()));

        Assert.Equal("migrations", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RollbackToVersionAsync_WithANonPositiveVersion_ThrowsNamingMigrations(long version)
    {
        var runner = new MigrationRunner(_connection);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RollbackToVersionAsync(0, [new HandWrittenMigration(version)]));

        Assert.Equal("migrations", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ApplyMigrationAsync_WithANonPositiveVersion_ThrowsNamingMigration(long version)
    {
        var runner = new MigrationRunner(_connection);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ApplyMigrationAsync(new HandWrittenMigration(version), new MigrationOptions()));

        Assert.Equal("migration", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RollbackMigrationAsync_WithANonPositiveVersion_ThrowsNamingMigration(long version)
    {
        var runner = new MigrationRunner(_connection);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RollbackMigrationAsync(new HandWrittenMigration(version)));

        Assert.Equal("migration", ex.ParamName);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_WithAPositiveVersion_IsUnaffected()
    {
        var runner = new MigrationRunner(_connection);

        Assert.Equal(1, await runner.ApplyMigrationsAsync([new HandWrittenMigration(1)], new MigrationOptions()));
    }
}
