// AOT verification: proves the library works end to end under Native AOT by supplying a
// source-generated JsonSerializerContext, so no reflection-based JSON serialization is used.

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;
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

// C46, at the other boundary: the factory validates, then builds a logger, then constructs the
// store, and caller code runs in that window. A logger factory that nulls SerializerOptions stands
// in for any such writer (another thread setting the property reaches the same window). The factory
// now snapshots the options *before* CreateLogger runs, so the mutation lands on the caller's object
// and the store never reads it — it is ignored, not refused, because nothing bad arrives.
// Measured before any of these guards existed: the store was built on the reflection fallback and a
// published AOT binary silently serialized this model as {}. The round-trip below is the positive
// evidence that the validated AppJsonContext.Default was used — surviving fields mean the mutated
// value was not. A regression here does not actually look like {}, though: reverting only the
// factory snapshot leaves the constructor cloning the *mutated* options and rejecting them in its
// own Validate(), so the shape fails at resolution. Reaching {} needs both guards gone, and only
// for this nulled half — resolver-less options fail inside JsonHelper's GetTypeInfo instead.
{
    var hostileOptions = new DocumentStoreOptionsBuilder()
        .UseInMemory()
        .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
        .Build();

    // Through the DI registration, which resolves ILoggerFactory from the container and hands it
    // to DocumentStoreFactory — the same window, on the same options object it captured.
    var hostileServices = new ServiceCollection();
    hostileServices.AddSingleton<ILoggerFactory>(new OptionsNullingLoggerFactory(hostileOptions));
    hostileServices.AddLiteDocumentStore(hostileOptions);
    await using var hostileProvider = hostileServices.BuildServiceProvider();
    var store2 = hostileProvider.GetRequiredService<IDocumentStore>();

    if (hostileOptions.SerializerOptions is not null)
    {
        throw new InvalidOperationException(
            "Expected the hostile logger factory to have nulled the caller's SerializerOptions.");
    }

    await store2.CreateTableAsync<Person>();
    await store2.UpsertAsync("nulled", new Person("nulled", "Ada Lovelace", "ada@example.com", 36));
    var roundTripped = await store2.GetAsync<Person>("nulled");

    if (roundTripped?.Name != "Ada Lovelace" || roundTripped.Age != 36)
    {
        throw new InvalidOperationException(
            "Expected the validated source-generated context to be used, but the document did not " +
            $"round-trip (got {roundTripped?.Name ?? "null"}).");
    }

    Console.WriteLine("Nulled after Validate => ignored (validated options used)");
}

// The same window, the other half of the paired check: replaced with options that are non-null but
// carry no TypeInfoResolver. Measured before the snapshot, with the constructor check alone: the
// store refused; before even that, construction succeeded and the first serialization failed with a
// metadata error. Now the replacement never reaches the store, so the round-trip is again what
// proves which options were used.
{
    var hostileOptions2 = new DocumentStoreOptionsBuilder()
        .UseInMemory()
        .WithSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })
        .Build();

    var hostileServices2 = new ServiceCollection();
    hostileServices2.AddSingleton<ILoggerFactory>(new OptionsReplacingLoggerFactory(hostileOptions2));
    hostileServices2.AddLiteDocumentStore(hostileOptions2);
    await using var hostileProvider2 = hostileServices2.BuildServiceProvider();
    var store3 = hostileProvider2.GetRequiredService<IDocumentStore>();

    if (hostileOptions2.SerializerOptions?.TypeInfoResolver is not null)
    {
        throw new InvalidOperationException(
            "Expected the hostile logger factory to have replaced the caller's SerializerOptions " +
            "with resolver-less ones.");
    }

    await store3.CreateTableAsync<Person>();
    await store3.UpsertAsync("dropped", new Person("dropped", "Grace Hopper", "grace@example.com", 85));
    var roundTripped2 = await store3.GetAsync<Person>("dropped");

    if (roundTripped2?.Name != "Grace Hopper" || roundTripped2.Age != 85)
    {
        throw new InvalidOperationException(
            "Expected the validated source-generated context to be used, but the document did not " +
            $"round-trip (got {roundTripped2?.Name ?? "null"}).");
    }

    Console.WriteLine("Resolver dropped after Validate => ignored (validated options used)");
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
