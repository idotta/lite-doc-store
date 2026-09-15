// AOT verification: proves the library works end to end under Native AOT by supplying a
// source-generated JsonSerializerContext, so no reflection-based JSON serialization is used.

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

var serializerOptions = new JsonSerializerOptions
{
    TypeInfoResolver = AppJsonContext.Default,
};

// C12: under Native AOT the reflection fallback is unreachable, so options that would
// land on it must be refused at validation rather than silently mis-serializing. The assertion
// is unconditional because PublishAot puts the IsDynamicCodeSupported feature switch in this
// project's runtimeconfig, so the property is false on `dotnet run` as well as in a published
// binary and this always runs.
try
{
    _ = new DocumentStoreOptionsBuilder().UseInMemory().Build();
    throw new InvalidOperationException("Expected resolver-less options to be refused under Native AOT.");
}
catch (ArgumentException ex) when (ex.ParamName == "SerializerOptions")
{
    Console.WriteLine($"Reflection fallback => refused ({ex.ParamName})");
}

// C12 again, at the other boundary: the factory validates, then builds a logger, then constructs
// the store, and caller code runs in that window. A logger factory that nulls SerializerOptions
// stands in for any such writer (another thread setting the property reaches the same window).
// Measured before the constructor check existed: the store was built on the reflection fallback
// and a published AOT binary silently serialized this model as {}.
try
{
    var hostileOptions = new DocumentStoreOptionsBuilder()
        .UseInMemory()
        .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
        .Build();

    using var store2 = new DocumentStoreFactory(
        new UnusedConnectionFactory(),
        null,
        new OptionsNullingLoggerFactory(hostileOptions)).Create(hostileOptions);

    throw new InvalidOperationException(
        "Expected the store constructor to refuse options nulled after validation.");
}
catch (ArgumentException ex) when (ex.ParamName == "SerializerOptions")
{
    Console.WriteLine($"Nulled after Validate => refused ({ex.ParamName})");
}

// The same window, the other half of the paired check: replaced with options that are non-null
// but carry no TypeInfoResolver. Measured before the constructor re-ran this half: construction
// succeeded and the first serialization failed with a metadata error instead.
try
{
    var hostileOptions2 = new DocumentStoreOptionsBuilder()
        .UseInMemory()
        .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
        .Build();

    using var store3 = new DocumentStoreFactory(
        new UnusedConnectionFactory(),
        null,
        new OptionsReplacingLoggerFactory(hostileOptions2)).Create(hostileOptions2);

    throw new InvalidOperationException(
        "Expected the store constructor to refuse resolver-less options swapped in after validation.");
}
catch (ArgumentException ex) when (ex.ParamName == "SerializerOptions")
{
    Console.WriteLine($"Resolver dropped after Validate => refused ({ex.ParamName})");
}

var options = new DocumentStoreOptionsBuilder()
    .UseInMemory()
    .WithSerializerOptions(serializerOptions)
    .Build();

var services = new ServiceCollection();
services.AddLiteDocumentStore(options);
await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IDocumentStore>();

await store.CreateTableAsync<Person>();

await store.UpsertAsync("p1", new Person("p1", "Ada Lovelace", "ada@example.com", 36));
await store.UpsertManyAsync(
[
    ("p2", new Person("p2", "Alan Turing", "alan@example.com", 41)),
    ("p3", new Person("p3", "Grace Hopper", "grace@example.com", 85)),
]);

var ada = await store.GetAsync<Person>("p1");
Console.WriteLine($"Get p1             => {ada?.Name}");

var all = (await store.GetAllAsync<Person>()).ToList();
Console.WriteLine($"GetAll             => {all.Count} people");

var many = await store.GetManyAsync<Person>(["p1", "p2", "missing"]);
Console.WriteLine($"GetMany 3 ids      => {many.Count} found ({string.Join(", ", many.Keys)})");

await store.CreateIndexAsync<Person>(p => p.Email);

// The options overload carries plain data and walks the same expression for member names only.
await store.CreateIndexAsync<Person>(
    p => p.Email,
    "idx_person_email_unique",
    new IndexOptions { Unique = true, Collation = "NOCASE", Filter = IndexFilter.IsNotNull("$.Email") });
Console.WriteLine("Unique NOCASE index => created");

var byEmail = (await store.QueryAsync<Person, string>("$.Email", "grace@example.com")).ToList();
Console.WriteLine($"Query $.Email      => {byEmail.Count} ({byEmail.FirstOrDefault()?.Name})");

Console.WriteLine($"Count              => {await store.CountAsync<Person>()}");
Console.WriteLine($"Exists p2          => {await store.ExistsAsync<Person>("p2")}");
Console.WriteLine($"Any @example.com   => " +
    $"{await store.ExistsAsync(DocumentQuery<Person>.Where("$.Email", QueryOperator.Like, "%@example.com"))}");

// A patch binds scalars, so no JsonTypeInfo for the value type is needed - part of what
// keeps it AOT-safe.
var patched = await store.PatchAsync("p1", DocumentPatch<Person>.Set("$.Age", 37));
Console.WriteLine($"Patch p1 age       => v{patched} ({(await store.GetAsync<Person>("p1"))?.Age})");

Console.WriteLine($"Delete p3          => {await store.DeleteAsync<Person>("p3")}");
Console.WriteLine($"Count after delete => {await store.CountAsync<Person>()}");

Console.WriteLine($"Healthy            => {await store.IsHealthyAsync()}");

// The expression overload is walked for member names only, never compiled - this gate proves it.
await store.DropIndexAsync<Person>(p => p.Email);
Console.WriteLine($"DeleteAll          => {await store.DeleteAllAsync<Person>()} rows");
await store.DropTableAsync<Person>();
Console.WriteLine("DropTable          => done");

Console.WriteLine("\nAOT verification completed - all operations ran with source-generated JSON (no reflection).");

sealed record Person(string Id, string Name, string Email, int Age);

/// <summary>
/// A tripwire, never called: the constructor refuses the swapped-in options before the pool opens
/// anything. If either guard regresses, the store reaches this instead and the gate dies loudly
/// rather than passing quietly. Shared by both logger-factory assertions below.
/// </summary>
internal sealed class UnusedConnectionFactory : IConnectionFactory
{
    private static InvalidOperationException Unexpected() =>
        new("The store must not reach the connection factory with options it has to refuse.");

    public SqliteConnection CreateConnection(DocumentStoreOptions options) => throw Unexpected();

    public Task<SqliteConnection> CreateConnectionAsync(
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default) => throw Unexpected();

    public void ConfigureConnection(SqliteConnection connection, DocumentStoreOptions options) =>
        throw Unexpected();

    public Task ConfigureConnectionAsync(
        SqliteConnection connection,
        DocumentStoreOptions options,
        CancellationToken cancellationToken = default) => throw Unexpected();
}

/// <summary>
/// Stands in for arbitrary caller code running between <c>Validate()</c> and the store's
/// construction: <c>DocumentStoreFactory.CreateStore</c> calls <c>CreateLogger</c> in exactly
/// that window, on the same mutable options object the caller passed in.
/// </summary>
internal sealed class OptionsNullingLoggerFactory(DocumentStoreOptions options) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        options.SerializerOptions = null;
        return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}

/// <summary>
/// The same window as <see cref="OptionsNullingLoggerFactory"/>, swapping in options that are
/// present but carry no resolver — the other half of the paired serializer check.
/// </summary>
internal sealed class OptionsReplacingLoggerFactory(DocumentStoreOptions options) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        options.SerializerOptions = new JsonSerializerOptions();
        return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Person))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
