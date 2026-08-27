namespace LiteDocumentStore;

/// <summary>
/// The five values SQLite's <c>typeof()</c> can return, in its own lowercase spelling.
/// </summary>
/// <remarks>
/// Only <see cref="Blob"/> is a readable blob payload. The others name what a corrupt
/// <c>__store_blobs</c> row holds instead, and travel to the caller as
/// <see cref="Exceptions.CorruptDataException.StoredTypeName"/>.
/// </remarks>
internal static class SqliteStorageClass
{
    internal const string Blob = "blob";
    internal const string Null = "null";
    internal const string Text = "text";
    internal const string Integer = "integer";
    internal const string Real = "real";
}
