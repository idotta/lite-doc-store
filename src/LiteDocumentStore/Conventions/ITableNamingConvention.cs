namespace LiteDocumentStore;

/// <summary>
/// Defines the contract for customizable table naming conventions.
/// Supports pluralization, snake_case, PascalCase, custom prefixes/suffixes, and more.
/// </summary>
/// <remarks>
/// An implementation must be deterministic: the same type must map to the same name for the
/// lifetime of a store, because every table name, every derived index name and the collision
/// guard's claim of a name for a type already depend on it. A convention is a behaviour object and
/// is never snapshotted, so a store holds the instance it was given rather than a copy of it.
/// </remarks>
public interface ITableNamingConvention
{
    /// <summary>
    /// Gets the table name for a given type.
    /// </summary>
    /// <typeparam name="T">The type to get the table name for</typeparam>
    /// <returns>The table name to use in SQL statements</returns>
    string GetTableName<T>();

    /// <summary>
    /// Gets the table name for a given type.
    /// </summary>
    /// <param name="type">The type to get the table name for</param>
    /// <returns>The table name to use in SQL statements</returns>
    string GetTableName(Type type);
}
