using Microsoft.Data.Sqlite;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="DefaultConnectionFactory"/>, which opens and PRAGMA-configures every
/// physical connection the pool hands out.
/// </summary>
/// <remarks>
/// The connection is opened before it is configured, so a PRAGMA that throws leaves an open handle
/// nothing else holds a reference to. These pin that the factory disposes it on the way out. An
/// open SQLite handle keeps the database file locked on Windows, so a successful delete is the
/// observable proof — hence <c>Pooling=False</c>, which stops Microsoft.Data.Sqlite's own pool from
/// holding the handle open after <c>Dispose</c>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DefaultConnectionFactoryTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    private DocumentStoreOptions FailingOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-factory-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var options = DocumentStoreOptions.ForFile(path);
        options.ConnectionString = new SqliteConnectionStringBuilder(options.ConnectionString)
        {
            Pooling = false,
        }.ToString();

        // Executed verbatim after the built-in PRAGMAs, so this throws once the connection is open.
        options.AdditionalPragmas.Add("PRAGMA journal_mode = NOT_A_MODE(");
        return options;
    }

    [Fact]
    public void CreateConnection_WhenConfigurationFails_DisposesTheConnection()
    {
        var options = FailingOptions();
        var factory = new DefaultConnectionFactory();

        Assert.Throws<SqliteException>(() => factory.CreateConnection(options));

        AssertFileIsNotLocked(options);
    }

    [Fact]
    public async Task CreateConnectionAsync_WhenConfigurationFails_DisposesTheConnection()
    {
        var options = FailingOptions();
        var factory = new DefaultConnectionFactory();

        await Assert.ThrowsAsync<SqliteException>(() => factory.CreateConnectionAsync(options));

        AssertFileIsNotLocked(options);
    }

    private static void AssertFileIsNotLocked(DocumentStoreOptions options)
    {
        var path = new SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
        Assert.True(File.Exists(path), "The factory should have created the database file.");

        // Throws IOException while a connection still holds the file.
        File.Delete(path);
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A failed test may still hold the file; the temp directory keeps it.
            }
        }
    }
}
