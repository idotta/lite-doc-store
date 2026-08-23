namespace LiteDocumentStore;

/// <summary>
/// Default implementation of <see cref="ITableNamingConvention"/> that uses the type name as-is.
/// </summary>
internal sealed class DefaultTableNamingConvention : ITableNamingConvention
{
    /// <inheritdoc/>
    public string GetTableName<T>()
    {
        return GetTableName(typeof(T));
    }

    /// <inheritdoc/>
    public string GetTableName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Name;
    }
}
