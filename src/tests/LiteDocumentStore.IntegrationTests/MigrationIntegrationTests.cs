using LiteDocumentStore.Exceptions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

public class MigrationIntegrationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private MigrationRunner _runner = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _runner = new MigrationRunner(_connection);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ApplyMigrationAsync_WithNewMigration_AppliesSuccessfully()
    {
        // Arrange
        var migration = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        // Act
        var applied = await _runner.ApplyMigrationAsync(migration);

        // Assert
        Assert.True(applied);
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(1, currentVersion);
    }

    [Fact]
    public async Task ApplyMigrationAsync_WithAlreadyAppliedMigration_ReturnsFalse()
    {
        // Arrange
        var migration = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        await _runner.ApplyMigrationAsync(migration);

        // Act
        var applied = await _runner.ApplyMigrationAsync(migration);

        // Assert
        Assert.False(applied);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_WithMultipleMigrations_AppliesInOrder()
    {
        // Arrange
        var migration1 = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        var migration2 = new Migration(
            version: 2,
            name: "CreateOrderTable",
            upSql: "CREATE TABLE [Order] (id TEXT PRIMARY KEY, product_id TEXT NOT NULL)",
            downSql: "DROP TABLE [Order]");

        var migrations = new[] { migration2, migration1 }; // Out of order intentionally

        // Act
        var appliedCount = await _runner.ApplyMigrationsAsync(migrations);

        // Assert
        Assert.Equal(2, appliedCount);
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(2, currentVersion);

        var appliedMigrations = (await _runner.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(2, appliedMigrations.Count);
        Assert.Equal(1, appliedMigrations[0].Version);
        Assert.Equal(2, appliedMigrations[1].Version);
    }

    [Fact]
    public async Task GetAppliedMigrationsAsync_WithNoMigrations_ReturnsEmpty()
    {
        // Act
        var migrations = await _runner.GetAppliedMigrationsAsync();

        // Assert
        Assert.Empty(migrations);
    }

    [Fact]
    public async Task GetAppliedMigrationsAsync_WithAppliedMigrations_ReturnsAll()
    {
        // Arrange
        var migration1 = new Migration(1, "First", "SELECT 1", "SELECT 2");
        var migration2 = new Migration(2, "Second", "SELECT 1", "SELECT 2");

        await _runner.ApplyMigrationAsync(migration1);
        await _runner.ApplyMigrationAsync(migration2);

        // Act
        var migrations = (await _runner.GetAppliedMigrationsAsync()).ToList();

        // Assert
        Assert.Equal(2, migrations.Count);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal("First", migrations[0].Name);
        Assert.Equal(2, migrations[1].Version);
        Assert.Equal("Second", migrations[1].Name);
    }

    [Fact]
    public async Task GetCurrentMigrationVersionAsync_WithNoMigrations_ReturnsZero()
    {
        // Act
        var version = await _runner.GetCurrentVersionAsync();

        // Assert
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task GetCurrentMigrationVersionAsync_WithAppliedMigrations_ReturnsHighestVersion()
    {
        // Arrange
        var migration1 = new Migration(1, "First", "SELECT 1", "SELECT 2");
        var migration2 = new Migration(5, "Second", "SELECT 1", "SELECT 2");
        var migration3 = new Migration(3, "Third", "SELECT 1", "SELECT 2");

        await _runner.ApplyMigrationsAsync(new[] { migration1, migration2, migration3 });

        // Act
        var version = await _runner.GetCurrentVersionAsync();

        // Assert
        Assert.Equal(5, version);
    }

    [Fact]
    public async Task RollbackMigrationAsync_WithAppliedMigration_RollsBackSuccessfully()
    {
        // Arrange
        var migration = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        await _runner.ApplyMigrationAsync(migration);

        // Verify table exists using introspector
        var introspector = new SchemaIntrospector(_connection);
        var tableExists = await introspector.TableExistsAsync("Product");
        Assert.True(tableExists);

        // Act
        var rolledBack = await _runner.RollbackMigrationAsync(migration);

        // Assert
        Assert.True(rolledBack);
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(0, currentVersion);

        // Verify table is dropped
        tableExists = await introspector.TableExistsAsync("Product");
        Assert.False(tableExists);
    }

    [Fact]
    public async Task RollbackMigrationAsync_WithUnappliedMigration_ReturnsFalse()
    {
        // Arrange
        var migration = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        // Act
        var rolledBack = await _runner.RollbackMigrationAsync(migration);

        // Assert
        Assert.False(rolledBack);
    }

    [Fact]
    public async Task RollbackToVersionAsync_WithMultipleMigrations_RollsBackCorrectly()
    {
        // Arrange
        var migration1 = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Product");

        var migration2 = new Migration(
            version: 2,
            name: "CreateOrderTable",
            upSql: "CREATE TABLE [Order] (id TEXT PRIMARY KEY, product_id TEXT NOT NULL)",
            downSql: "DROP TABLE [Order]");

        var migration3 = new Migration(
            version: 3,
            name: "CreateCustomerTable",
            upSql: "CREATE TABLE Customer (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            downSql: "DROP TABLE Customer");

        var migrations = new[] { migration1, migration2, migration3 };
        await _runner.ApplyMigrationsAsync(migrations);

        // Act - Rollback to version 1 (should rollback 2 and 3)
        var rolledBackCount = await _runner.RollbackToVersionAsync(1, migrations);

        // Assert
        Assert.Equal(2, rolledBackCount);
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(1, currentVersion);

        // Verify only migration 1 is still applied
        var appliedMigrations = (await _runner.GetAppliedMigrationsAsync()).ToList();
        Assert.Single(appliedMigrations);
        Assert.Equal(1, appliedMigrations[0].Version);
    }

    [Fact]
    public async Task RollbackToVersionAsync_ToVersionZero_RollsBackAllMigrations()
    {
        // Arrange
        var migration1 = new Migration(1, "First", "SELECT 1", "SELECT 2");
        var migration2 = new Migration(2, "Second", "SELECT 1", "SELECT 2");

        var migrations = new[] { migration1, migration2 };
        await _runner.ApplyMigrationsAsync(migrations);

        // Act
        var rolledBackCount = await _runner.RollbackToVersionAsync(0, migrations);

        // Assert
        Assert.Equal(2, rolledBackCount);
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(0, currentVersion);

        var appliedMigrations = await _runner.GetAppliedMigrationsAsync();
        Assert.Empty(appliedMigrations);
    }

    [Fact]
    public async Task Migration_WithTransactionRollback_DoesNotApply()
    {
        // Arrange
        var migration = new Migration(
            version: 1,
            name: "CreateProductTable",
            upSql: "CREATE TABLE Product (id TEXT PRIMARY KEY); CREATE TABLE Invalid (,,,);", // Invalid SQL
            downSql: "DROP TABLE Product");

        // Act & Assert
        await Assert.ThrowsAsync<SqliteException>(() => _runner.ApplyMigrationAsync(migration));

        // Migration should not be recorded
        var currentVersion = await _runner.GetCurrentVersionAsync();
        Assert.Equal(0, currentVersion);

        // Table should not exist
        var introspector = new SchemaIntrospector(_connection);
        var tableExists = await introspector.TableExistsAsync("Product");
        Assert.False(tableExists);
    }

    [Fact]
    public async Task MigrationHistoryRecord_ContainsAppliedAt_Timestamp()
    {
        // Arrange
        var migration = new Migration(1, "Test", "SELECT 1", "SELECT 2");
        var before = DateTimeOffset.UtcNow;

        // Act
        await _runner.ApplyMigrationAsync(migration);
        var after = DateTimeOffset.UtcNow;

        // Assert
        var appliedMigrations = (await _runner.GetAppliedMigrationsAsync()).ToList();
        Assert.Single(appliedMigrations);

        var record = appliedMigrations[0];
        Assert.True(record.AppliedAt >= before);
        Assert.True(record.AppliedAt <= after);
    }

    [Fact]
    public async Task ApplyMigrationAsync_VersionBelowCurrentMax_IsSkipped()
    {
        // Arrange - apply v1 and v3, leaving a gap at v2. The runner tracks the highest applied
        // version, so a later-introduced v2 sits below the current max.
        await _runner.ApplyMigrationAsync(new Migration(1, "V1", "SELECT 1", "SELECT 1"));
        await _runner.ApplyMigrationAsync(new Migration(3, "V3", "SELECT 1", "SELECT 1"));

        // Act - v2 (below current max version 3) is skipped by the linear version model
        var v2 = new Migration(
            version: 2,
            name: "V2",
            upSql: "CREATE TABLE ShouldNotExist (id TEXT PRIMARY KEY)",
            downSql: "DROP TABLE ShouldNotExist");
        var applied = await _runner.ApplyMigrationAsync(v2);

        // Assert - not applied, its Up SQL never ran, and history is unchanged (only v1, v3)
        Assert.False(applied);

        var introspector = new SchemaIntrospector(_connection);
        Assert.False(await introspector.TableExistsAsync("ShouldNotExist"));

        var versions = (await _runner.GetAppliedMigrationsAsync()).Select(m => m.Version).ToList();
        Assert.Equal(new long[] { 1, 3 }, versions);
    }

    [Fact]
    public async Task RollbackToVersionAsync_MissingDefinition_Throws_AndRollsBackNothing()
    {
        // Arrange - apply v1 and v2
        var v1 = new Migration(1, "V1", "CREATE TABLE T1 (id TEXT PRIMARY KEY)", "DROP TABLE T1");
        var v2 = new Migration(2, "V2", "CREATE TABLE T2 (id TEXT PRIMARY KEY)", "DROP TABLE T2");
        await _runner.ApplyMigrationAsync(v1);
        await _runner.ApplyMigrationAsync(v2);

        // Act & Assert - rolling back to 0 requires both definitions; only v2 is supplied, so the
        // whole operation must fail before mutating anything (no partial rollback).
        await Assert.ThrowsAsync<LiteDocumentStoreException>(async () =>
            await _runner.RollbackToVersionAsync(0, new[] { v2 }));

        // Nothing was rolled back: both tables remain and both versions stay recorded.
        var introspector = new SchemaIntrospector(_connection);
        Assert.True(await introspector.TableExistsAsync("T1"));
        Assert.True(await introspector.TableExistsAsync("T2"));
        Assert.Equal(2, await _runner.GetCurrentVersionAsync());
    }
}
