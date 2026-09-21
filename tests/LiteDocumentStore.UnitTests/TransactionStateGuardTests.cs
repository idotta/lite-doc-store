using Xunit;

using GuardTable = Xunit.TheoryData<
    string,
    System.Func<LiteDocumentStore.IDocumentTransaction, object?>>;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// The transaction-side mirror of <see cref="DisposalGuardTests"/>, for the three synchronous
/// helpers that need no connection: they run <c>ActiveTransaction()</c> like every other member,
/// so a completed transaction answers with the state exception rather than a stale result.
/// </summary>
/// <remarks>
/// One table of three members driven through the three terminal states, plus the live-transaction
/// mirror so a member made to throw unconditionally cannot pass the guarded theory vacuously.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class TransactionStateGuardTests
{
    private sealed record Doc(string Name, int Value);

    private static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await store.CreateTableAsync<Doc>();
        return store;
    }

    public static GuardTable ConnectionlessMembers()
    {
        var table = new GuardTable();
        table.Add("GetTableName", t => t.GetTableName<Doc>());
        table.Add("SerializeDocument", t => t.SerializeDocument(new Doc("a", 1)));
        table.Add("DeserializeDocument", t => t.DeserializeDocument<Doc>("{\"Name\":\"a\",\"Value\":1}"));
        return table;
    }

    private static string Describe(Exception? exception) =>
        exception is null ? "no exception" : exception.GetType().Name;

    [Theory]
    [MemberData(nameof(ConnectionlessMembers))]
    public async Task Member_AfterCommit_ThrowsInvalidOperation(
        string memberName,
        Func<IDocumentTransaction, object?> member)
    {
        await using var store = await CreateStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.CommitAsync();

        var exception = Record.Exception(() => member(transaction));

        Assert.True(
            exception is InvalidOperationException and not ObjectDisposedException,
            $"{memberName} after commit: expected InvalidOperationException, got {Describe(exception)}.");
    }

    [Theory]
    [MemberData(nameof(ConnectionlessMembers))]
    public async Task Member_AfterRollback_ThrowsInvalidOperation(
        string memberName,
        Func<IDocumentTransaction, object?> member)
    {
        await using var store = await CreateStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.RollbackAsync();

        var exception = Record.Exception(() => member(transaction));

        Assert.True(
            exception is InvalidOperationException and not ObjectDisposedException,
            $"{memberName} after rollback: expected InvalidOperationException, got {Describe(exception)}.");
    }

    [Theory]
    [MemberData(nameof(ConnectionlessMembers))]
    public async Task Member_AfterDisposal_ThrowsObjectDisposed(
        string memberName,
        Func<IDocumentTransaction, object?> member)
    {
        await using var store = await CreateStoreAsync();
        var transaction = await store.BeginTransactionAsync();
        await transaction.DisposeAsync();

        var exception = Record.Exception(() => member(transaction));

        Assert.True(
            exception is ObjectDisposedException,
            $"{memberName} after disposal: expected ObjectDisposedException, got {Describe(exception)}.");
    }

    [Theory]
    [MemberData(nameof(ConnectionlessMembers))]
    public async Task Member_AfterCommitThenDisposal_ThrowsObjectDisposed(
        string memberName,
        Func<IDocumentTransaction, object?> member)
    {
        // Release() is idempotent, so commit-then-dispose is a supported completion path: the
        // second state must still be the disposed one, not the committed one.
        await using var store = await CreateStoreAsync();
        var transaction = await store.BeginTransactionAsync();
        await transaction.CommitAsync();
        await transaction.DisposeAsync();

        var exception = Record.Exception(() => member(transaction));

        Assert.True(
            exception is ObjectDisposedException,
            $"{memberName} after commit then disposal: expected ObjectDisposedException, " +
            $"got {Describe(exception)}.");
    }

    [Theory]
    [MemberData(nameof(ConnectionlessMembers))]
    public async Task Member_OnALiveTransaction_IsNotGuarded(
        string memberName,
        Func<IDocumentTransaction, object?> member)
    {
        await using var store = await CreateStoreAsync();
        await using var transaction = await store.BeginTransactionAsync();

        var exception = Record.Exception(() => member(transaction));

        Assert.True(
            exception is null,
            $"{memberName} threw on a live transaction: {Describe(exception)}.");
    }

    [Fact]
    public async Task SerializeDocument_WithNullOnACompletedTransaction_StillThrowsArgumentNull()
    {
        // Argument validation precedes the state guard, surface-wide: a null document is a caller
        // bug whether the transaction is open, committed or disposed.
        await using var store = await CreateStoreAsync();

        await using (var committed = await store.BeginTransactionAsync())
        {
            await committed.CommitAsync();
            Assert.Throws<ArgumentNullException>(() => committed.SerializeDocument<Doc>(null!));
        }

        var disposed = await store.BeginTransactionAsync();
        await disposed.DisposeAsync();

        Assert.Throws<ArgumentNullException>(() => disposed.SerializeDocument<Doc>(null!));
    }
}
