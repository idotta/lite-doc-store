// AOT verification: proves the library works end to end under Native AOT by supplying a
// source-generated JsonSerializerContext, so no reflection-based JSON serialization is used.

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;

var serializerOptions = new JsonSerializerOptions
{
    TypeInfoResolver = AppJsonContext.Default,
};

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

await store.CreateIndexAsync<Person>(p => p.Email);
var byEmail = (await store.QueryAsync<Person, string>("$.Email", "grace@example.com")).ToList();
Console.WriteLine($"Query $.Email      => {byEmail.Count} ({byEmail.FirstOrDefault()?.Name})");

Console.WriteLine($"Count              => {await store.CountAsync<Person>()}");
Console.WriteLine($"Exists p2          => {await store.ExistsAsync<Person>("p2")}");

Console.WriteLine($"Delete p3          => {await store.DeleteAsync<Person>("p3")}");
Console.WriteLine($"Count after delete => {await store.CountAsync<Person>()}");

Console.WriteLine($"Healthy            => {await store.IsHealthyAsync()}");

Console.WriteLine("\nAOT verification completed - all operations ran with source-generated JSON (no reflection).");

record Person(string Id, string Name, string Email, int Age);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Person))]
internal partial class AppJsonContext : JsonSerializerContext;
