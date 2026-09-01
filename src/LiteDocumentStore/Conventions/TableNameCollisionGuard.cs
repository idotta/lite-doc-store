using System.Collections.Concurrent;

namespace LiteDocumentStore;

/// <summary>
/// Wraps an <see cref="ITableNamingConvention"/> and refuses to hand the same table name to two
/// different types.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a convention that maps two types onto one table is silent data loss, not an error:
/// measured against real SQLite, the second type's write overwrote the first type's row, a read of
/// the first type deserialized the other type's JSON into a fabricated document with nulls in
/// non-nullable members, and <c>GetAllAsync&lt;T&gt;</c> returned the other type's documents. No
/// exception anywhere. The guard turns that into a throw on the second type's first operation.
/// </para>
/// <para>
/// It exists because <see cref="DefaultTableNamingConvention"/>'s fold is deliberately
/// collision-resistant rather than injective, and because a caller-supplied convention can collide in
/// ways no encoding in this library could prevent. The check is per store instance and in process: two
/// processes opening one file with colliding types are not covered, which needs both processes to use
/// both types before any damage is possible.
/// </para>
/// <para>
/// One <see cref="ConcurrentDictionary{TKey, TValue}"/> hit per operation, on a path whose cheapest
/// operation is ~4.5 µs.
/// </para>
/// </remarks>
internal sealed class TableNameCollisionGuard(ITableNamingConvention inner) : ITableNamingConvention
{
    private readonly ITableNamingConvention _inner = inner;
    private readonly ConcurrentDictionary<string, Type> _claims = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string GetTableName<T>() => Claim(_inner.GetTableName<T>(), typeof(T));

    /// <inheritdoc/>
    public string GetTableName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Claim(_inner.GetTableName(type), type);
    }

    private string Claim(string tableName, Type type)
    {
        var owner = _claims.GetOrAdd(tableName, type);

        if (owner != type)
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' is already mapped to '{owner}', so '{type}' cannot use it. " +
                $"Two types sharing one table overwrite each other's documents silently. " +
                $"Supply an {nameof(ITableNamingConvention)} that gives them distinct names.");
        }

        return tableName;
    }
}
