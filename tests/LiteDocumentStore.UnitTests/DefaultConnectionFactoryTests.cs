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

    [Theory]
    [InlineData(5000, 5)]
    [InlineData(250, 1)]   // Rounded up: a sub-second busy timeout must not become "no retry".
    [InlineData(1500, 2)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]     // Never 0: that means "retry forever" to the provider, not "fail now".
    public void CreateConnection_CapsTheProvidersRetryLoopAtBusyTimeoutMs(int busyTimeoutMs, int expectedSeconds)
    {
        // PRAGMA busy_timeout only bounds SQLite's handler within one attempt; the provider then
        // retries the attempt until the command timeout, whose 30 s default made BusyTimeoutMs a
        // floor rather than a bound.
        var options = FileOptions();
        options.BusyTimeoutMs = busyTimeoutMs;

        using var connection = new DefaultConnectionFactory().CreateConnection(options);

        Assert.Equal(expectedSeconds, connection.DefaultTimeout);
    }

    [Theory]
    [InlineData("Default Timeout=17")]
    [InlineData("default timeout=17")]
    [InlineData("Command Timeout=17")]
    public void CreateConnection_LeavesACommandTimeoutStatedInTheConnectionStringAlone(string keyword)
    {
        var options = FileOptions();
        options.ConnectionString = $"{options.ConnectionString};{keyword}";
        options.BusyTimeoutMs = 250;

        using var connection = new DefaultConnectionFactory().CreateConnection(options);

        Assert.Equal(17, connection.DefaultTimeout);
    }

    [Fact]
    public async Task CreateConnectionAsync_CapsTheProvidersRetryLoopAtBusyTimeoutMs()
    {
        var options = FileOptions();
        options.BusyTimeoutMs = 2500;

        await using var connection = await new DefaultConnectionFactory().CreateConnectionAsync(options);

        Assert.Equal(3, connection.DefaultTimeout);
    }

    private DocumentStoreOptions FileOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-factory-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);

        var options = DocumentStoreOptions.ForFile(path);
        // Written by hand rather than through SqliteConnectionStringBuilder: the builder rewrites
        // the string with every keyword it knows, which would state a command timeout these tests
        // need absent.
        options.ConnectionString = $"Data Source={path};Pooling=False";
        return options;
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
