using LiteDocumentStore.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDocumentStore.Examples;

/// <summary>
/// Field-level updates with <see cref="DocumentPatch{T}"/>: several changes in one statement,
/// what they do that a read-modify-write cannot, and the compare-and-swap form.
/// </summary>
internal static class PatchExample
{
    internal sealed record Account(
        string Id,
        string Owner,
        string? Nickname,
        decimal Balance,
        bool Frozen,
        DateTime LastSeenAt);

    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLiteDocumentStore(DocumentStoreOptions.ForInMemory());

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.CreateTableAsync<Account>();

        var seed = new Account("a1", "Ada Lovelace", "Ada", 250.75m, Frozen: false, new DateTime(2024, 1, 1));
        await store.UpsertAsync(seed.Id, seed);
        await ShowAsync(store, "Seeded");

        // Several operations, one statement, one version bump.
        var version = await store.PatchAsync("a1", DocumentPatch<Account>
            .Set("$.Balance", 310.20m)
            .AndSet("$.Frozen", true)
            .AndSet("$.LastSeenAt", new DateTime(2024, 6, 1))
            .AndRemove("$.Nickname"));
        Console.WriteLine($"Patched 4 fields           => version {version}");
        await ShowAsync(store, "After patch");

        // Why it exists: another writer changes a field this caller never read.
        var stale = (await store.GetAsync<Account>("a1"))!;
        await store.PatchAsync("a1", DocumentPatch<Account>.Set("$.Balance", 999.99m));

        // The patch touches only Owner, so the other writer's balance survives.
        await store.PatchAsync("a1", DocumentPatch<Account>.Set("$.Owner", "A. Lovelace"));
        await ShowAsync(store, "Patch keeps 999.99");

        // The same edit as a read-modify-write puts the stale balance back.
        await store.UpsertAsync("a1", stale with { Owner = "A. Lovelace" });
        await ShowAsync(store, "Upsert clobbers it");

        // The compare-and-swap form refuses to patch a version someone else has moved on from.
        var current = (await store.GetWithVersionAsync<Account>("a1"))!;
        await store.PatchWithVersionAsync(
            "a1", DocumentPatch<Account>.Set("$.Frozen", false), current.Version);
        Console.WriteLine($"\nCAS patch at v{current.Version}            => applied");

        try
        {
            await store.PatchWithVersionAsync(
                "a1", DocumentPatch<Account>.Set("$.Frozen", true), current.Version);
        }
        catch (ConcurrencyException exception)
        {
            Console.WriteLine(
                $"CAS patch at v{current.Version} again      => {exception.Kind} " +
                $"(stored v{exception.ActualVersion})");
        }

        // A patch carries no document, so it cannot insert: a missing id is a conflict.
        try
        {
            await store.PatchAsync("nobody", DocumentPatch<Account>.Set("$.Owner", "Nobody"));
        }
        catch (ConcurrencyException exception)
        {
            Console.WriteLine($"Patch a missing id         => {exception.Kind}");
        }
    }

    private static async Task ShowAsync(IDocumentStore store, string label)
    {
        var account = (await store.GetAsync<Account>("a1"))!;
        Console.WriteLine(
            $"{label,-26} => {account.Owner}, {account.Balance:0.00}, " +
            $"frozen={account.Frozen}, nickname={account.Nickname ?? "(none)"}");
    }
}
