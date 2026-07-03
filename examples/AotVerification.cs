#!/usr/bin/env dotnet run
// AOT Verification Example - Prove the library works under Native AOT
//
// Run (JIT):     dotnet run AotVerification.cs
// Publish (AOT): dotnet publish AotVerification.cs -r win-x64   (use your RID)
//
// This example supplies a source-generated JsonSerializerContext so that NO reflection-based
// JSON serialization is used. It exercises the full surviving document-store surface end to end.

#:package Microsoft.Extensions.DependencyInjection@10.0.1

#:project ../src/LiteDocumentStore/LiteDocumentStore.csproj

#:property PublishAot=true

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDocumentStore;
using Microsoft.Extensions.DependencyInjection;

// Back the store with a source-generated context - the AOT-safe serialization path.
var serializerOptions = new JsonSerializerOptions
{
    TypeInfoResolver = AppJsonContext.Default
};

var options = new DocumentStoreOptionsBuilder()
    .UseInMemory()
    .WithSerializerOptions(serializerOptions)
    .Build();

var services = new ServiceCollection();
services.AddLiteDocumentStore(options);
using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IDocumentStore>();

// Schema
await store.CreateTableAsync<Person>();

// Writes: single + bulk
await store.UpsertAsync("p1", new Person("p1", "Ada Lovelace", "ada@example.com", 36));
await store.UpsertManyAsync(new (string, Person)[]
{
    ("p2", new Person("p2", "Alan Turing", "alan@example.com", 41)),
    ("p3", new Person("p3", "Grace Hopper", "grace@example.com", 85)),
});

// Reads
var ada = await store.GetAsync<Person>("p1");
Console.WriteLine($"Get p1            => {ada?.Name}");

var all = (await store.GetAllAsync<Person>()).ToList();
Console.WriteLine($"GetAll            => {all.Count} people");

// Index + query by JSON path (equality)
await store.CreateIndexAsync<Person>(p => p.Email);
var byEmail = (await store.QueryAsync<Person, string>("$.Email", "grace@example.com")).ToList();
Console.WriteLine($"Query $.Email     => {byEmail.Count} ({byEmail.FirstOrDefault()?.Name})");

// Aggregates
Console.WriteLine($"Count             => {await store.CountAsync<Person>()}");
Console.WriteLine($"Exists p2         => {await store.ExistsAsync<Person>("p2")}");

// Delete
Console.WriteLine($"Delete p3         => {await store.DeleteAsync<Person>("p3")}");
Console.WriteLine($"Count after delete => {await store.CountAsync<Person>()}");

// Health
Console.WriteLine($"Healthy           => {await store.IsHealthyAsync()}");

Console.WriteLine("\n✓ AOT verification completed - all operations ran with source-generated JSON (no reflection).");

// Model + source-generated serialization context
record Person(string Id, string Name, string Email, int Age);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Person))]
internal partial class AppJsonContext : JsonSerializerContext;
