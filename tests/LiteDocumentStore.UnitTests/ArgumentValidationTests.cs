using Xunit;

using ArgTable = Xunit.TheoryData<
    string,
    System.Func<LiteDocumentStore.IDocumentStore, System.Threading.CancellationToken, System.Threading.Tasks.Task>>;
using IdTable = Xunit.TheoryData<
    string,
    System.Func<LiteDocumentStore.IDocumentStore, string, System.Threading.CancellationToken, System.Threading.Tasks.Task>>;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Up-front argument validation on the public store surface, grouped by the family each check
/// belongs to rather than by operation: an id that is null, empty or whitespace; a null document,
/// collection or builder; and a negative version, length or offset.
/// </summary>
/// <remarks>
/// <para>
/// The <c>*Many</c> collection contents (null element, duplicate id) are already pinned by
/// <c>DocumentStoreIntegrationTests</c> and <c>BatchWriteIntegrationTests</c>; what is covered
/// here is the single-argument validation those tests never reach. Operations that visibly share
/// one implementation are listed once per public entry point, because it is the entry point a
/// caller binds against.
/// </para>
/// <para>
/// <b>Why three tables and not twenty-seven tests.</b> Argument validation has to happen
/// <em>before</em> <c>DocumentStore</c> rents a pooled connection, and that is a property of
/// every operation on the surface rather than of any one of them. Proving it operation by
/// operation would mean ~27 near-identical tests per hostile condition; instead the same three
/// tables are replayed under each condition that makes a late guard visible:
/// </para>
/// <list type="bullet">
///   <item><description>a healthy store (the baseline theories) — the guard exists at all;</description></item>
///   <item><description>an already-cancelled token — a late guard surfaces as
///   <c>OperationCanceledException</c> from the rent instead;</description></item>
///   <item><description>a pool saturated by a leaked transaction — a late guard surfaces as a
///   <c>TimeoutException</c> blaming undisposed transactions;</description></item>
///   <item><description>a disposed store — a late guard surfaces as
///   <c>ObjectDisposedException</c>.</description></item>
/// </list>
/// <para>
/// The last one also pins an ordering decision rather than just a defect: argument validation
/// runs <em>before</em> <c>ThrowIfDisposed()</c>, so a caller who passes garbage to a disposed
/// store is told about the garbage. That already matched <c>OpenBlobReadAsync</c>,
/// <c>ExecuteRawAsync</c>, <c>SerializeDocument</c> and <c>MigrateAsync</c>; the rest of the
/// surface now matches them.
/// </para>
/// <para>
/// The guards live in <c>DocumentOperations</c> as well, because <c>DocumentStoreTransaction</c>
/// calls straight into it and never rents. That half is pinned by
/// <c>TransactionArgumentValidationIntegrationTests</c>, which also asserts that the double
/// validation on the store path cannot change the message or <c>ParamName</c> a caller sees.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ArgumentValidationTests
{
    private sealed record Doc(string Name, int Value);

    private static readonly DocumentPatch<Doc> Patch = DocumentPatch<Doc>.Set("$.Value", 1);

    private static readonly byte[] Payload = [1, 2, 3];

    /// <summary>
    /// The blank id the hostile theories use. The baseline theory sweeps all four blank shapes;
    /// replaying that sweep under a saturated pool would multiply a pre-fix run's timeouts by
    /// four for no extra information, since the shapes differ only inside the guard itself.
    /// </summary>
    private const string BlankId = "";

    private static MemoryStream Source() => new([1, 2, 3]);

    private static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = await new DocumentStoreFactory().CreateAsync(DocumentStoreOptions.ForInMemory());
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    /// <summary>
    /// A store whose operation budget is exactly one connection, with a short wait before the
    /// pool gives up.
    /// </summary>
    /// <remarks>
    /// The timeout is deliberately small: each table has ~19-26 entries, and before the guards
    /// were hoisted <em>every</em> entry burned the full wait, so the default 30 s would have
    /// turned one theory into a quarter of an hour. 500 ms is long enough not to be flaky on a
    /// loaded machine — nothing here contends for the connection except the leaked transaction,
    /// which never releases it, so the wait is never nearly-satisfied — and short enough that a
    /// regression reports itself in seconds rather than minutes.
    /// </remarks>
    private static async Task<IDocumentStore> CreateSingleConnectionStoreAsync()
    {
        var options = DocumentStoreOptions.ForInMemory();
        options.MaxPoolSize = 1;
        options.PoolWaitTimeoutMs = 500;

        var store = await new DocumentStoreFactory().CreateAsync(options);

        // The schema has to exist before the only leasable connection is taken hostage.
        await store.CreateTableAsync<Doc>();
        await store.CreateBlobTableAsync();
        return store;
    }

    /// <summary>
    /// An already-cancelled token. Built from the struct constructor rather than from a
    /// <see cref="CancellationTokenSource"/> so nothing has to be kept alive or disposed: the
    /// operations under test only ever observe it, never register on it.
    /// </summary>
    private static CancellationToken CancelledToken() => new(canceled: true);

    private static string Describe(Exception? exception) =>
        exception is null ? "no exception" : $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>
    /// Asserts that the operation failed on its argument rather than on the machinery an early
    /// guard is supposed to run ahead of.
    /// </summary>
    private static void AssertFailedOnTheArgument(string operationName, string condition, Exception? exception)
    {
        Assert.False(
            exception is TimeoutException,
            $"{operationName} waited for a pooled connection before validating its argument ({condition}): "
            + $"{Describe(exception)}");
        Assert.False(
            exception is OperationCanceledException,
            $"{operationName} observed the cancellation token before validating its argument ({condition}): "
            + $"{Describe(exception)}");
        Assert.False(
            exception is ObjectDisposedException,
            $"{operationName} checked disposal before validating its argument ({condition}): "
            + $"{Describe(exception)}");
    }

    private static void AssertBlankIdRejected(string operationName, string condition, Exception? exception)
    {
        AssertFailedOnTheArgument(operationName, condition, exception);

        Assert.True(
            exception is ArgumentException,
            $"{operationName} did not reject its blank id ({condition}); got {Describe(exception)}.");
        Assert.Equal("id", ((ArgumentException)exception!).ParamName);
        Assert.False(
            exception is ArgumentNullException,
            $"{operationName} reported a null id as ArgumentNullException; the family throws ArgumentException.");
    }

    private static void AssertNullRejected(string operationName, string condition, Exception? exception)
    {
        AssertFailedOnTheArgument(operationName, condition, exception);

        Assert.True(
            exception is ArgumentNullException,
            $"{operationName} did not reject its null argument ({condition}); got {Describe(exception)}.");
    }

    private static void AssertOutOfRangeRejected(string operationName, string condition, Exception? exception)
    {
        AssertFailedOnTheArgument(operationName, condition, exception);

        Assert.True(
            exception is ArgumentOutOfRangeException,
            $"{operationName} did not reject its negative argument ({condition}); got {Describe(exception)}.");
    }

    // ---- The id family ---------------------------------------------------------------------

    public static IdTable IdBearingOperations()
    {
        var table = new IdTable();
        AddDocumentIdOperations(table);
        AddBlobIdOperations(table);
        return table;
    }

    private static void AddDocumentIdOperations(IdTable table)
    {
        table.Add("UpsertAsync", (s, id, ct) => s.UpsertAsync(id, new Doc("a", 1), ct));
        table.Add("UpsertWithVersionAsync", (s, id, ct) => s.UpsertWithVersionAsync(id, new Doc("a", 1), 0, ct));
        table.Add("DeleteWithVersionAsync", (s, id, ct) => s.DeleteWithVersionAsync<Doc>(id, 1, ct));
        table.Add("PatchAsync", (s, id, ct) => s.PatchAsync(id, Patch, ct));
        table.Add("PatchWithVersionAsync", (s, id, ct) => s.PatchWithVersionAsync(id, Patch, 1, ct));
        table.Add("GetAsync", (s, id, ct) => s.GetAsync<Doc>(id, ct));
        table.Add("GetWithVersionAsync", (s, id, ct) => s.GetWithVersionAsync<Doc>(id, ct));
        table.Add("DeleteAsync", (s, id, ct) => s.DeleteAsync<Doc>(id, ct));
        table.Add("ExistsAsync", (s, id, ct) => s.ExistsAsync<Doc>(id, ct));
    }

    private static void AddBlobIdOperations(IdTable table)
    {
        table.Add("PutBlobAsync(bytes)", (s, id, ct) => s.PutBlobAsync(id, Payload, ct));
        table.Add("PutBlobAsync(stream)", (s, id, ct) => s.PutBlobAsync(id, Source(), 3, ct));
        table.Add("PutBlobWithVersionAsync(bytes)", (s, id, ct) => s.PutBlobWithVersionAsync(id, Payload, 0, ct));
        table.Add("PutBlobWithVersionAsync(stream)", (s, id, ct) => s.PutBlobWithVersionAsync(id, Source(), 3, 0, ct));
        table.Add("GetBlobAsync", (s, id, ct) => s.GetBlobAsync(id, ct));
        table.Add("DeleteBlobAsync", (s, id, ct) => s.DeleteBlobAsync(id, ct));
        table.Add("DeleteBlobWithVersionAsync", (s, id, ct) => s.DeleteBlobWithVersionAsync(id, 1, ct));
        table.Add("BlobLengthAsync", (s, id, ct) => s.BlobLengthAsync(id, ct));
        table.Add("BlobExistsAsync", (s, id, ct) => s.BlobExistsAsync(id, ct));
        table.Add("GetBlobInfoAsync", (s, id, ct) => s.GetBlobInfoAsync(id, ct));
        table.Add("OpenBlobReadAsync", (s, id, ct) => s.OpenBlobReadAsync(id, ct));
    }

    [Theory]
    [MemberData(nameof(IdBearingOperations))]
    public async Task Operation_WithABlankId_ThrowsNamingTheId(
        string operationName,
        Func<IDocumentStore, string, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        foreach (var id in new[] { null!, "", "   ", "\t" })
        {
            var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
                () => operation(store, id, CancellationToken.None));

            Assert.Equal("id", exception.ParamName);
            Assert.False(
                exception is ArgumentNullException,
                $"{operationName} reported a null id as ArgumentNullException; the family throws ArgumentException.");
        }
    }

    [Theory]
    [MemberData(nameof(IdBearingOperations))]
    public async Task Operation_WithABlankIdAndACancelledToken_ThrowsNamingTheIdNotTheCancellation(
        string operationName,
        Func<IDocumentStore, string, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, BlankId, CancelledToken()));

        AssertBlankIdRejected(operationName, "cancelled token", exception);
    }

    [Theory]
    [MemberData(nameof(IdBearingOperations))]
    public async Task Operation_WithABlankIdOnASaturatedPool_ThrowsNamingTheIdNotATimeout(
        string operationName,
        Func<IDocumentStore, string, CancellationToken, Task> operation)
    {
        await using var store = await CreateSingleConnectionStoreAsync();

        // The leak is the point: an undisposed transaction holds the only leasable connection,
        // so anything that rents before validating blocks until PoolWaitTimeoutMs elapses.
        var leaked = await store.BeginTransactionAsync();
        try
        {
            var exception = await Record.ExceptionAsync(() => operation(store, BlankId, CancellationToken.None));

            AssertBlankIdRejected(operationName, "saturated pool", exception);
        }
        finally
        {
            // Released so the store can dispose without waiting on a connection it will never
            // get back; the assertion above has already run against the saturated pool.
            await leaked.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(IdBearingOperations))]
    public async Task Operation_WithABlankIdOnADisposedStore_ThrowsNamingTheIdNotObjectDisposed(
        string operationName,
        Func<IDocumentStore, string, CancellationToken, Task> operation)
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, BlankId, CancellationToken.None));

        AssertBlankIdRejected(operationName, "disposed store", exception);
    }

    // ---- The null-reference family ---------------------------------------------------------

    public static ArgTable NullReferenceArguments()
    {
        var table = new ArgTable();
        AddDocumentNullArguments(table);
        AddSchemaNullArguments(table);
        AddStoreNullArguments(table);
        return table;
    }

    private static void AddDocumentNullArguments(ArgTable table)
    {
        table.Add("UpsertAsync(data)", (s, ct) => s.UpsertAsync<Doc>("a", null!, ct));
        table.Add("UpsertWithVersionAsync(data)", (s, ct) => s.UpsertWithVersionAsync<Doc>("a", null!, 0, ct));
        table.Add("UpsertManyAsync(items)", (s, ct) => s.UpsertManyAsync<Doc>(null!, ct));
        table.Add("GetManyAsync(ids)", (s, ct) => s.GetManyAsync<Doc>(null!, ct));
        table.Add("DeleteManyAsync(ids)", (s, ct) => s.DeleteManyAsync<Doc>(null!, ct));
        table.Add("PatchAsync(patch)", (s, ct) => s.PatchAsync<Doc>("a", null!, ct));
        table.Add("PatchWithVersionAsync(patch)", (s, ct) => s.PatchWithVersionAsync<Doc>("a", null!, 1, ct));
        table.Add("QueryAsync(value)", (s, ct) => s.QueryAsync<Doc, string>("$.Name", null!, ct));
        table.Add("QueryAsync(query)", (s, ct) => s.QueryAsync<Doc>((DocumentQuery<Doc>)null!, ct));
        table.Add("CountAsync(query)", (s, ct) => s.CountAsync<Doc>(null!, ct));
        table.Add("ExistsAsync(query)", (s, ct) => s.ExistsAsync<Doc>((DocumentQuery<Doc>)null!, ct));
    }

    private static void AddSchemaNullArguments(ArgTable table)
    {
        table.Add(
            "CreateIndexAsync(expression)",
            (s, ct) => s.CreateIndexAsync<Doc>(
                (System.Linq.Expressions.Expression<System.Func<Doc, object>>)null!, cancellationToken: ct));
        table.Add("CreateIndexAsync(jsonPath)", (s, ct) => s.CreateIndexAsync<Doc>((string)null!, cancellationToken: ct));
        table.Add(
            "CreateCompositeIndexAsync(expressions)",
            (s, ct) => s.CreateCompositeIndexAsync<Doc>(
                (System.Linq.Expressions.Expression<System.Func<Doc, object>>[])null!, cancellationToken: ct));
        table.Add(
            "CreateCompositeIndexAsync(jsonPaths)",
            (s, ct) => s.CreateCompositeIndexAsync<Doc>((string[])null!, cancellationToken: ct));
        table.Add(
            "AddVirtualColumnAsync(expression)",
            (s, ct) => s.AddVirtualColumnAsync<Doc>(
                (System.Linq.Expressions.Expression<System.Func<Doc, object>>)null!, "col", cancellationToken: ct));
        table.Add(
            "AddVirtualColumnAsync(jsonPath)",
            (s, ct) => s.AddVirtualColumnAsync<Doc>((string)null!, "col", cancellationToken: ct));
        table.Add("DropIndexAsync(expression)", (s, ct) => s.DropIndexAsync<Doc>(null!, ct));
    }

    private static void AddStoreNullArguments(ArgTable table)
    {
        table.Add("PutBlobAsync(source)", (s, ct) => s.PutBlobAsync("a", (Stream)null!, 3, ct));
        table.Add(
            "PutBlobWithVersionAsync(source)",
            (s, ct) => s.PutBlobWithVersionAsync("a", (Stream)null!, 3, 0, ct));
        table.Add("ExecuteRawAsync<T>(operation)", (s, ct) => s.ExecuteRawAsync<int>(null!, ct));
        table.Add("ExecuteRawAsync(operation)", (s, ct) => s.ExecuteRawAsync(null!, ct));
        table.Add("ExecuteInTransactionAsync(action)", (s, ct) => s.ExecuteInTransactionAsync(null!, ct));
        table.Add("MigrateAsync(migrations)", (s, ct) => s.MigrateAsync(null!, ct));
        table.Add("MigrateAsync(options)", (s, ct) => s.MigrateAsync([], null!, ct));
        table.Add("RollbackToVersionAsync(migrations)", (s, ct) => s.RollbackToVersionAsync(0, null!, ct));

        // Synchronous and connectionless: it takes no token and rents nothing, so it ignores the
        // one threaded through here. It stays in the table because the disposed-store theory is
        // still meaningful for it — it is one of the four members that already validated ahead
        // of ThrowIfDisposed, and this is what stops that regressing.
        table.Add("SerializeDocument(value)", (s, _) => Task.FromResult(s.SerializeDocument<Doc>(null!)));
    }

    [Theory]
    [MemberData(nameof(NullReferenceArguments))]
    public async Task Operation_WithANullArgument_ThrowsArgumentNull(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

        Assert.True(
            exception is ArgumentNullException,
            $"{operationName} did not reject its null argument; got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(NullReferenceArguments))]
    public async Task Operation_WithANullArgumentAndACancelledToken_ThrowsArgumentNullNotCancellation(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancelledToken()));

        AssertNullRejected(operationName, "cancelled token", exception);
    }

    [Theory]
    [MemberData(nameof(NullReferenceArguments))]
    public async Task Operation_WithANullArgumentOnASaturatedPool_ThrowsArgumentNullNotATimeout(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateSingleConnectionStoreAsync();

        var leaked = await store.BeginTransactionAsync();
        try
        {
            var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

            AssertNullRejected(operationName, "saturated pool", exception);
        }
        finally
        {
            await leaked.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(NullReferenceArguments))]
    public async Task Operation_WithANullArgumentOnADisposedStore_ThrowsArgumentNullNotObjectDisposed(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

        AssertNullRejected(operationName, "disposed store", exception);
    }

    // ---- The out-of-range family -----------------------------------------------------------

    public static ArgTable OutOfRangeArguments()
    {
        var table = new ArgTable();
        table.Add(
            "UpsertWithVersionAsync(expectedVersion)",
            (s, ct) => s.UpsertWithVersionAsync("a", new Doc("a", 1), -1, ct));
        table.Add("DeleteWithVersionAsync(expectedVersion)", (s, ct) => s.DeleteWithVersionAsync<Doc>("a", -1, ct));
        table.Add("PatchWithVersionAsync(expectedVersion)", (s, ct) => s.PatchWithVersionAsync("a", Patch, -1, ct));
        table.Add("PutBlobWithVersionAsync(expectedVersion)", (s, ct) => s.PutBlobWithVersionAsync("a", Payload, -1, ct));
        table.Add(
            "PutBlobWithVersionAsync(stream, expectedVersion)",
            (s, ct) => s.PutBlobWithVersionAsync("a", Source(), 3, -1, ct));
        table.Add("DeleteBlobWithVersionAsync(expectedVersion)", (s, ct) => s.DeleteBlobWithVersionAsync("a", -1, ct));
        table.Add("PutBlobAsync(length)", (s, ct) => s.PutBlobAsync("a", Source(), -1, ct));
        table.Add("ListBlobsAsync(skip)", (s, ct) => s.ListBlobsAsync(skip: -1, cancellationToken: ct));
        table.Add("RollbackToVersionAsync(targetVersion)", (s, ct) => s.RollbackToVersionAsync(-1, [], ct));
        return table;
    }

    [Theory]
    [MemberData(nameof(OutOfRangeArguments))]
    public async Task Operation_WithANegativeArgument_ThrowsArgumentOutOfRange(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

        Assert.True(
            exception is ArgumentOutOfRangeException,
            $"{operationName} did not reject its negative argument; got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(OutOfRangeArguments))]
    public async Task Operation_WithANegativeArgumentAndACancelledToken_ThrowsOutOfRangeNotCancellation(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancelledToken()));

        AssertOutOfRangeRejected(operationName, "cancelled token", exception);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeArguments))]
    public async Task Operation_WithANegativeArgumentOnASaturatedPool_ThrowsOutOfRangeNotATimeout(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        await using var store = await CreateSingleConnectionStoreAsync();

        var leaked = await store.BeginTransactionAsync();
        try
        {
            var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

            AssertOutOfRangeRejected(operationName, "saturated pool", exception);
        }
        finally
        {
            await leaked.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(OutOfRangeArguments))]
    public async Task Operation_WithANegativeArgumentOnADisposedStore_ThrowsOutOfRangeNotObjectDisposed(
        string operationName,
        Func<IDocumentStore, CancellationToken, Task> operation)
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => operation(store, CancellationToken.None));

        AssertOutOfRangeRejected(operationName, "disposed store", exception);
    }

    /// <summary>
    /// The one public member outside the <c>RunAsync</c> surface that had the opposite order.
    /// </summary>
    /// <remarks>
    /// It rents like the rest, but through its own body rather than through <c>RunAsync</c>, so
    /// the sweep above never reached it: an unknown <see cref="TransactionMode"/> used to be
    /// reported only after <c>ThrowIfDisposed</c>. The ordering is a rule for the whole surface
    /// or it is not a rule, so this pins the remaining member on its own.
    /// </remarks>
    [Fact]
    public async Task BeginTransactionAsync_WithAnUnknownModeOnADisposedStore_ThrowsOutOfRangeNotObjectDisposed()
    {
        var store = await CreateStoreAsync();
        await store.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => store.BeginTransactionAsync((TransactionMode)99));

        Assert.False(
            exception is ObjectDisposedException,
            $"BeginTransactionAsync reported disposal before its bad mode: {Describe(exception)}.");
        var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("mode", outOfRange.ParamName);
    }
}
