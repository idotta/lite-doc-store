using Xunit;

using GuardTable = Xunit.TheoryData<
    string,
    System.Func<LiteDocumentStore.IDocumentOperations, System.Threading.Tasks.Task>>;

namespace LiteDocumentStore.IntegrationTests;

/// <summary>
/// The other half of the argument-validation contract: the guards that <c>DocumentStore</c> runs
/// before renting a pooled connection have to stay in <c>DocumentOperations</c> as well, because
/// <c>DocumentStoreTransaction</c> calls straight into it and never rents.
/// </summary>
/// <remarks>
/// <para>
/// <c>ArgumentValidationTests</c> in the unit project covers the breadth — every id-bearing,
/// null-rejecting and range-checking entry point, replayed under a cancelled token, a saturated
/// pool and a disposed store. What it cannot see is a "fix" that <em>moves</em> a guard up into
/// the store instead of duplicating it: on the store path both arrangements look identical.
/// This file is the counterweight, so it deliberately covers a representative slice rather than
/// the whole surface — one blank id, one null document, one null builder of each kind, and one
/// negative number of each kind.
/// </para>
/// <para>
/// Both <see cref="IDocumentStore"/> and <see cref="IDocumentTransaction"/> implement
/// <see cref="IDocumentOperations"/>, so one table of delegates is invoked against both. That is
/// also what makes the parity test possible: the store validates twice (once before the rent,
/// once inside the operations) and a caller must not be able to tell — same exception type, same
/// <c>ParamName</c>, same message, whichever object the call was made on.
/// </para>
/// <para>
/// None of these calls reaches SQL, so nothing here contends for the shared-cache in-memory
/// database's table-level locks; the store-side call is still made before the transaction is
/// opened, so a regression that renders a guard late cannot deadlock the test.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(nameof(LiteDocumentStoreCollection))]
public class TransactionArgumentValidationIntegrationTests
{
    private readonly LiteDocumentStoreTestFixture _fixture;

    public TransactionArgumentValidationIntegrationTests(LiteDocumentStoreTestFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record TxnDoc(string Name, int Age);

    private static readonly byte[] Payload = [1, 2, 3];

    private async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await _fixture.CreateInMemoryStoreAsync();
        await store.CreateTableAsync<TxnDoc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    /// <summary>
    /// One representative guard per family, expressed against <see cref="IDocumentOperations"/>
    /// so the same delegate can be run on the store and on a transaction.
    /// </summary>
    public static GuardTable SharedGuards()
    {
        var table = new GuardTable();

        // The id family: ArgumentException naming 'id', never ArgumentNullException.
        table.Add("GetAsync(blank id)", o => o.GetAsync<TxnDoc>(""));
        table.Add("PutBlobAsync(blank id)", o => o.PutBlobAsync("", Payload));

        // The null-reference family, once for a document and once for each builder shape.
        table.Add("UpsertAsync(null data)", o => o.UpsertAsync<TxnDoc>("a", null!));
        table.Add("PatchAsync(null patch)", o => o.PatchAsync<TxnDoc>("a", null!));
        table.Add("CountAsync(null query)", o => o.CountAsync<TxnDoc>((DocumentQuery<TxnDoc>)null!));

        // The out-of-range family: a compare-and-swap version and a paging offset.
        table.Add("DeleteWithVersionAsync(negative version)", o => o.DeleteWithVersionAsync<TxnDoc>("a", -1));
        table.Add("ListBlobsAsync(negative skip)", o => o.ListBlobsAsync(skip: -1));

        return table;
    }

    private static string Describe(Exception? exception) =>
        exception is null ? "no exception" : $"{exception.GetType().Name}: {exception.Message}";

    [Theory]
    [MemberData(nameof(SharedGuards))]
    public async Task Operation_OnATransaction_StillValidatesItsArguments(
        string operationName,
        Func<IDocumentOperations, Task> operation)
    {
        var store = await CreateStoreAsync();

        await using var transaction = await store.BeginTransactionAsync();

        var exception = await Record.ExceptionAsync(() => operation(transaction));

        Assert.True(
            exception is ArgumentException,
            $"{operationName} was not rejected on the transaction path; got {Describe(exception)}. "
            + "The guard belongs in DocumentOperations as well as in DocumentStore — a transaction "
            + "never rents, so hoisting it out of the operations loses it entirely.");

        await transaction.RollbackAsync();
    }

    [Theory]
    [MemberData(nameof(SharedGuards))]
    public async Task Operation_OnTheStoreAndOnATransaction_ThrowsTheSameExceptionWithTheSameParamNameAndMessage(
        string operationName,
        Func<IDocumentOperations, Task> operation)
    {
        var store = await CreateStoreAsync();

        // Store first, with no transaction open: the store validates twice, so this is the side
        // where a divergence could creep in.
        var fromStore = await Record.ExceptionAsync(() => operation(store));

        await using var transaction = await store.BeginTransactionAsync();
        var fromTransaction = await Record.ExceptionAsync(() => operation(transaction));

        Assert.True(
            fromStore is ArgumentException,
            $"{operationName} on the store: expected an ArgumentException; got {Describe(fromStore)}.");
        Assert.True(
            fromTransaction is ArgumentException,
            $"{operationName} on the transaction: expected an ArgumentException; got {Describe(fromTransaction)}.");

        Assert.Equal(fromStore!.GetType(), fromTransaction!.GetType());
        Assert.Equal(
            ((ArgumentException)fromStore).ParamName,
            ((ArgumentException)fromTransaction).ParamName);

        // The message carries the ParamName and, for the range family, the offending value; the
        // two paths share one helper per guard precisely so these cannot drift.
        Assert.Equal(fromStore.Message, fromTransaction.Message);

        await transaction.RollbackAsync();
    }
}
