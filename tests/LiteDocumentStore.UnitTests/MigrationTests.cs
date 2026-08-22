using Xunit;

namespace LiteDocumentStore.UnitTests;

[Trait("Category", "Unit")]
public class MigrationTests
{
    [Fact]
    public void Migration_Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange & Act
        var migration = new Migration(
            version: 20260109001,
            name: "CreateCustomerTable",
            upSql: "CREATE TABLE Customer (id TEXT PRIMARY KEY)",
            downSql: "DROP TABLE Customer");

        // Assert
        Assert.Equal(20260109001, migration.Version);
        Assert.Equal("CreateCustomerTable", migration.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Migration_Constructor_WithInvalidVersion_ThrowsArgumentException(long version)
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => new Migration(
            version,
            "TestMigration",
            "SELECT 1",
            "SELECT 2"));

        Assert.Contains("Version must be greater than zero", ex.Message);
    }

    [Fact]
    public void Migration_Constructor_WithNullName_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, null!, "SELECT 1", "SELECT 2"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullUpSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, "Test", null!, "SELECT 2"));

        Assert.Equal("upSql", ex.ParamName);
    }

    [Fact]
    public void Migration_Constructor_WithNullDownSql_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new Migration(1, "Test", "SELECT 1", null!));

        Assert.Equal("downSql", ex.ParamName);
    }
}
