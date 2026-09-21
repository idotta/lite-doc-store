using System.Text;
using Xunit;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The real-SQLite half of the state guard on the three connectionless members: the same three
/// terminal states over a live transaction lifecycle, the positive control that they still do
/// their real work while the transaction is open, and the store-side contrast that makes the
/// break a transaction break rather than a surface-wide one.
/// </summary>
/// <remarks>
/// These run on a shared-cache in-memory database, so nothing here touches the store while a
/// transaction is open — the members under test need no connection, and the store-side assertions
/// are made before any transaction is begun or after it has ended.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public class TransactionConnectionlessMemberGuardIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public TransactionConnectionlessMemberGuardIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record TxnDoc(string Name, int Age);

    private async Task<IDocumentStore> CreateStoreWithTableAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<TxnDoc>();
        return store;
    }

    [Fact]
    public async Task ConnectionlessMembers_OnALiveTransaction_StillDoTheirRealWork()
    {
        // The positive control: without it the guarded assertions below would pass just as well
        // against three members that throw unconditionally.
        await using var store = await CreateStoreWithTableAsync();
        await using var transaction = await store.BeginTransactionAsync();

        Assert.Equal(store.GetTableName<TxnDoc>(), transaction.GetTableName<TxnDoc>());

        var bytes = transaction.SerializeDocument(new TxnDoc("Ann", 30));
        Assert.Equal(store.SerializeDocument(new TxnDoc("Ann", 30)), bytes);

        var roundTripped = transaction.DeserializeDocument<TxnDoc>(Encoding.UTF8.GetString(bytes));
        Assert.Equal(new TxnDoc("Ann", 30), roundTripped);

        // And the transaction is genuinely still usable, so the guard passed for the right reason.
        await transaction.UpsertAsync("a", new TxnDoc("Ann", 30));
        await transaction.CommitAsync();

        Assert.Equal(new TxnDoc("Ann", 30), await store.GetAsync<TxnDoc>("a"));
    }

    [Fact]
    public async Task GetTableName_AfterTheAwaitUsingBlock_ThrowsObjectDisposed()
    {
        // The shape the break actually costs a consumer: a name resolved outside the block.
        await using var store = await CreateStoreWithTableAsync();

        IDocumentTransaction escaped;
        await using (var transaction = await store.BeginTransactionAsync())
        {
            escaped = transaction;
            await transaction.CommitAsync();
        }

        Assert.Throws<ObjectDisposedException>(() => escaped.GetTableName<TxnDoc>());

        // The store is the unaffected route for exactly that call.
        Assert.Equal(store.GetTableName<TxnDoc>(), DefaultTableNamingConvention.Instance.GetTableName<TxnDoc>());
    }

    [Fact]
    public async Task DeserializeDocument_AfterCommit_ThrowsInvalidOperationRatherThanAnsweringDefault()
    {
        // Null json answers default on a live transaction, so the guard has to fire ahead of that
        // short-circuit or a completed transaction would keep answering.
        await using var store = await CreateStoreWithTableAsync();
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.CommitAsync();

        var exception = Assert.Throws<InvalidOperationException>(
            () => transaction.DeserializeDocument<TxnDoc>(null));

        Assert.IsNotType<ObjectDisposedException>(exception);
        Assert.Contains("committed or rolled back", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerializeDocument_AfterRollback_ThrowsInvalidOperationButStillChecksItsArgument()
    {
        await using var store = await CreateStoreWithTableAsync();
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.RollbackAsync();

        Assert.Throws<ArgumentNullException>(() => transaction.SerializeDocument<TxnDoc>(null!));

        var exception = Assert.Throws<InvalidOperationException>(
            () => transaction.SerializeDocument(new TxnDoc("Ann", 30)));

        Assert.IsNotType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task ConnectionlessMembers_OnTheStore_AreUnaffectedByATransactionEnding()
    {
        // The break is scoped to the transaction object: the store keeps answering across a
        // transaction's whole lifetime and past its end.
        await using var store = await CreateStoreWithTableAsync();

        var tableName = store.GetTableName<TxnDoc>();
        var bytes = store.SerializeDocument(new TxnDoc("Ann", 30));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.RollbackAsync();
        }

        Assert.Equal(tableName, store.GetTableName<TxnDoc>());
        Assert.Equal(bytes, store.SerializeDocument(new TxnDoc("Ann", 30)));
        Assert.Equal(
            new TxnDoc("Ann", 30),
            store.DeserializeDocument<TxnDoc>(Encoding.UTF8.GetString(bytes)));
    }
}
