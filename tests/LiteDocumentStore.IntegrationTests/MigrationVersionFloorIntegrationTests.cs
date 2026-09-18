using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Through the public store surface: a hand-written migration at version 0 or below is refused
/// before anything is applied, where it used to apply and then be unreportable and unrollbackable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationVersionFloorIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _store = null!;

    public async Task InitializeAsync() =>
        _store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private sealed class HandWrittenMigration(long version) : IMigration
    {
        public long Version => version;

        public string Name => "HandWritten";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS FloorProbe (id TEXT)", cancellationToken);

        public Task DownAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
            connection.ExecuteAsync("DROP TABLE IF EXISTS FloorProbe", cancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MigrateAsync_WithANonPositiveVersion_ThrowsAndAppliesNothing(long version)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.MigrateAsync([new HandWrittenMigration(version)]));

        Assert.Equal("migrations", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await _store.GetAppliedMigrationsAsync());
        Assert.Equal(0, await _store.GetCurrentMigrationVersionAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MigrateAsync_WithAllowOutOfOrder_StillRefusesANonPositiveVersion(long version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.MigrateAsync(
                [new HandWrittenMigration(version)],
                new MigrationOptions { AllowOutOfOrder = true }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RollbackToVersionAsync_WithANonPositiveVersion_Throws(long version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RollbackToVersionAsync(0, [new HandWrittenMigration(version)]));
    }

    [Fact]
    public async Task MigrateAsync_WithAPositiveVersion_IsUnaffected()
    {
        Assert.Equal(1, await _store.MigrateAsync([new HandWrittenMigration(1)]));
        Assert.Equal(1, await _store.GetCurrentMigrationVersionAsync());
    }
}
