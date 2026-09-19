using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The DI registration's options snapshot, in its most literal form: against real file databases,
/// the only file that may exist on disk is the one the resolved key was registered with.
/// </summary>
/// <remarks>
/// The unit sibling counts rows through two shared-cache in-memory databases. This one adds the
/// evidence a shared-cache name cannot give: a database the store never opened leaves no file, so
/// "the wrong database" is visible without trusting any reader.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ServiceCollectionExtensionsIntegrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _aPath;
    private readonly string _bPath;

    public ServiceCollectionExtensionsIntegrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"di_snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _aPath = Path.Combine(_directory, "a.db");
        _bPath = Path.Combine(_directory, "b.db");
    }

    private sealed record Doc(string Name, int Value);

    [Fact]
    public async Task AddKeyedLiteDocumentStore_WithOneOptionsInstanceRegisteredTwice_OpensOnlyTheKeyOwnFile()
    {
        var options = DocumentStoreOptions.ForFile(_aPath);

        var services = new ServiceCollection();
        services.AddKeyedLiteDocumentStore("a", options);
        options.ConnectionString = $"Data Source={_bPath}";
        services.AddKeyedLiteDocumentStore("b", options);

        await using (var provider = services.BuildServiceProvider())
        {
            // Only "a" is resolved, so only a.db may ever be touched.
            var a = provider.GetRequiredKeyedService<IDocumentStore>("a");
            await a.CreateTableAsync<Doc>();
            await a.UpsertAsync("doc-1", new Doc("a", 1));
        }

        Assert.True(File.Exists(_aPath), $"a.db is missing; the directory holds {FileNames()}");
        Assert.False(File.Exists(_bPath), $"key \"a\" opened b.db; the directory holds {FileNames()}");
        Assert.Equal(1, await CountRowsAsync(_aPath));
    }

    private string FileNames() =>
        string.Join(", ", Directory.GetFiles(_directory).Select(Path.GetFileName));

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
}
