namespace LiteDocumentStore;

/// <summary>
/// One validated term of an <see cref="IndexFilter"/>: a JSON path and whether it must be null.
/// </summary>
/// <remarks>
/// Structured for the reason <see cref="QueryPredicate"/> is: <see cref="SqlGenerator"/> never
/// accepts a caller-supplied SQL fragment.
/// </remarks>
/// <param name="JsonPath">The JSON path the term tests, e.g. <c>$.Email</c></param>
/// <param name="RequiresNull">True for <c>IS NULL</c>, false for <c>IS NOT NULL</c></param>
internal sealed record IndexFilterTerm(string JsonPath, bool RequiresNull);
