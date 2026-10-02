namespace LiteDocumentStore;

/// <summary>
/// One <c>ORDER BY</c> term of a <see cref="DocumentQuery{T}"/>.
/// </summary>
/// <param name="JsonPath">The JSON path to sort on, e.g. <c>$.CreatedAt</c></param>
/// <param name="Descending">True to sort descending, false to sort ascending</param>
/// <param name="Chronological">
/// True when the path holds a serialized <see cref="DateTime"/> or <see cref="DateTimeOffset"/>,
/// whose text does not sort chronologically. Set at execution, never by the builder, which cannot
/// see the type.
/// </param>
internal sealed record QueryOrdering(string JsonPath, bool Descending, bool Chronological = false);
