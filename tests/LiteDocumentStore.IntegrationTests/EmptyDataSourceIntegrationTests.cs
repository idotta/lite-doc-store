using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// An empty data source names a private temporary database that SQLite deletes when the
/// connection closes, so a pooled store turns one configured database into one per connection —
/// the same failure the private-in-memory rejection exists for, spelled on disk.
/// </summary>
/// <remarks>
/// Measured against real SQLite before the guard split, at <c>MaxPoolSize = 4</c> with four
/// concurrent transactions each re-issuing the idempotent DDL: four documents written with no
/// exception, <c>CountAsync</c> = 1, three of four <c>GetAsync</c> calls null, and
/// <c>IsHealthyAsync</c> still true. These pin the refusal at the store-construction boundary and
/// the workload the refusal protects.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EmptyDataSourceIntegrationTests
{
    private static DocumentStoreOptions Options(string connectionString) =>
        new(connectionString) { MaxPoolSize = 4, EnableWalMode = false };

    /// <summary>
    /// The factory is the boundary the DI registration also goes through, so refusing here is what
    /// stops such a store existing at all.
    /// </summary>
    [Theory]
    [InlineData("Data Source=")]
    [InlineData("Data Source=file:")]
    [InlineData("Data Source=file:?cache=shared")]
    [InlineData("Cache=Shared")]
    public void Create_WithAnEmptyDataSource_Throws(string connectionString)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DocumentStoreFactory().Create(Options(connectionString)));

        Assert.Contains("empty data source", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workload that lost three of four documents on an empty data source, run against a named
    /// file database: every write survives, so the refusal removes a broken configuration rather
    /// than a working one.
    /// </summary>
    /// <remarks>
    /// The writes are concurrent but not barrier-synchronized, which is the difference between a
    /// real database and the broken one. On an empty data source each connection had a database of
    /// its own and nothing contended; here they share one, so parking four open transactions on a
    /// barrier would deadlock — the first holds SQLite's write lock while waiting for three that
    /// cannot take it. Concurrency is still what the test needs: it is physical connection count,
    /// not task count, that produced the loss.
    /// </remarks>
    [Fact]
    public async Task ConcurrentTransactions_OnANamedDatabase_KeepEveryWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lds-c42-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DocumentStoreFactory().Create(Options($"Data Source={path}"));
            await using (store)
            {
                await store.CreateTableAsync<TestDocument>();

                await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
                {
                    await using var transaction = await store.BeginTransactionAsync();
                    await transaction.UpsertAsync(
                        $"id{i}", new TestDocument { Title = $"t{i}", Content = $"c{i}" });
                    await transaction.CommitAsync();
                })));

                Assert.Equal(4, await store.CountAsync<TestDocument>());
                for (var i = 0; i < 4; i++)
                {
                    var document = await store.GetAsync<TestDocument>($"id{i}");
                    Assert.NotNull(document);
                    Assert.Equal($"t{i}", document.Title);
                }
            }
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // The temp directory keeps it.
                }
            }
        }
    }
}
