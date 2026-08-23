namespace LiteDocumentStore;

/// <summary>
/// A generated statement and the values its <c>@p0..@pN</c> parameters bind, in the order the
/// generator emitted them.
/// </summary>
/// <remarks>
/// SQL and parameters are produced by the same left-to-right pass and returned together, so the
/// two cannot drift: <c>ParameterValues[i]</c> is always <c>@p{i}</c>.
/// </remarks>
/// <param name="Sql">The generated SQL statement</param>
/// <param name="ParameterValues">The values to bind, positionally</param>
internal sealed record GeneratedQuery(string Sql, IReadOnlyList<object?> ParameterValues);
