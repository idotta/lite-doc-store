namespace LiteDocumentStore;

/// <summary>
/// One <c>ORDER BY</c> term of a <see cref="DocumentQuery{T}"/>.
/// </summary>
/// <param name="JsonPath">The JSON path to sort on, e.g. <c>$.CreatedAt</c></param>
/// <param name="Descending">True to sort descending, false to sort ascending</param>
internal sealed record QueryOrdering(string JsonPath, bool Descending);
