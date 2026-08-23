namespace LiteDocumentStore;

/// <summary>
/// One validated filter of a <see cref="DocumentQuery{T}"/>: a JSON path, an operator and the
/// value(s) it binds.
/// </summary>
/// <remarks>
/// Structured on purpose — <see cref="SqlGenerator"/> never accepts a caller-supplied SQL
/// fragment, so every predicate reaches it as data it can validate and parameterize itself.
/// </remarks>
/// <param name="JsonPath">The JSON path the predicate filters on, e.g. <c>$.Email</c></param>
/// <param name="Operator">The comparison to apply</param>
/// <param name="Value">
/// The single bound value, or null for <see cref="QueryOperator.IsNull"/>,
/// <see cref="QueryOperator.IsNotNull"/> and <see cref="QueryOperator.In"/>
/// </param>
/// <param name="Values">
/// The bound values for <see cref="QueryOperator.In"/>; empty for every other operator
/// </param>
internal sealed record QueryPredicate(
    string JsonPath,
    QueryOperator Operator,
    object? Value,
    IReadOnlyList<object?> Values);
