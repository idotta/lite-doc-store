using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// Regression tests for the options snapshot, against real SQLite files: a store writes to the
/// database its options were validated against, not to one swapped in afterwards.
/// </summary>
/// <remarks>
/// The measured defect was a caller-supplied <see cref="ILoggerFactory"/> whose
/// <c>CreateLogger</c> — arbitrary caller code, running between <c>Validate()</c> and construction,
/// on the very object that was validated — replaced one valid connection string with another. Both
/// pass every guard, so nothing was bypassed; the store simply opened a different database.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class OptionsSnapshotIntegrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _validatedPath;
    private readonly string _retargetPath;

    public OptionsSnapshotIntegrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _validatedPath = Path.Combine(_directory, "validated.db");
        _retargetPath = Path.Combine(_directory, "retarget.db");
    }

    private sealed record Doc(string Name, int Value);

    [Fact]
    public async Task Create_WithALoggerFactoryThatRetargetsTheConnectionString_WritesToTheValidatedDatabase()
    {
        var options = new DocumentStoreOptions
        {
            ConnectionString = $"Data Source={_validatedPath}",
            EnableWalMode = false
        };
        var loggerFactory = new RetargetingLoggerFactory(options, $"Data Source={_retargetPath}");

        await using (var store = new DocumentStoreFactory(
            new DefaultConnectionFactory(),
            null,
            loggerFactory).Create(options))
        {
            await store.CreateTableAsync<Doc>();
            await store.UpsertAsync("doc-1", new Doc("snapshot", 1));
        }

        Assert.True(loggerFactory.Fired);
        Assert.Equal($"Data Source={_retargetPath}", options.ConnectionString);

        Assert.True(File.Exists(_validatedPath));
        Assert.Equal(1, await CountRowsAsync(_validatedPath));

        // The retarget target must never have been opened at all: no file, or an empty one.
        Assert.Equal(0, await CountRowsAsync(_retargetPath));
    }

    private static async Task<long> CountRowsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync();

        var table = DefaultTableNamingConvention.Instance.GetTableName<Doc>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'";
        var exists = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        if (exists == 0)
        {
            return 0;
        }

        command.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the provider's finalizers is not this test's concern.
        }
    }

    private sealed class RetargetingLoggerFactory(DocumentStoreOptions options, string retargetTo) : ILoggerFactory
    {
        public bool Fired { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            Fired = true;
            options.ConnectionString = retargetTo;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
