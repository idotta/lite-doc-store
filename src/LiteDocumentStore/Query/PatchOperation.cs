namespace LiteDocumentStore;

/// <summary>
/// The kind of change a <see cref="PatchOperation"/> applies.
/// </summary>
internal enum PatchOperationKind
{
    /// <summary>Writes a value at the path, creating it when absent.</summary>
    Set,

    /// <summary>Removes the path from the document.</summary>
    Remove
}

/// <summary>
/// One validated change of a <see cref="DocumentPatch{T}"/>: a JSON path, what to do to it and
/// the value it binds.
/// </summary>
/// <remarks>
/// Structured on purpose, for the reason <see cref="QueryPredicate"/> is: <see cref="SqlGenerator"/>
/// never accepts a caller-supplied SQL fragment.
/// </remarks>
/// <param name="JsonPath">The JSON path the operation targets, e.g. <c>$.Email</c></param>
/// <param name="Kind">Whether the path is written or removed</param>
/// <param name="Value">
/// The value to bind for <see cref="PatchOperationKind.Set"/> — null writes JSON null; always
/// null for <see cref="PatchOperationKind.Remove"/>
/// </param>
/// <param name="AsJson">
/// True when the bound parameter carries JSON text that must be wrapped in <c>json(...)</c>
/// rather than stored as the value itself. SQLite has no boolean and no exact decimal, so
/// <see cref="bool"/>, <see cref="decimal"/> and a <see cref="ulong"/> above
/// <see cref="long.MaxValue"/> travel as their JSON text to land in the document unchanged.
/// </param>
internal sealed record PatchOperation(
    string JsonPath,
    PatchOperationKind Kind,
    object? Value,
    bool AsJson);
