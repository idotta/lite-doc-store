namespace LiteDocumentStore;

/// <summary>
/// An immutable set of null tests restricting a partial index to the rows it matches, combined
/// with <c>AND</c>.
/// </summary>
/// <remarks>
/// <para>
/// Value-free on purpose. SQLite forbids bound parameters in a partial index's <c>WHERE</c>, so
/// a filter comparing against a value would have to inline it as a SQL literal — a new
/// injection surface with its own escaping rules. <c>IS NULL</c> / <c>IS NOT NULL</c> covers
/// what partial indexes are actually wanted for (a unique email among the rows that have one)
/// with nothing new crossing the parameterization boundary. A richer filter stays an
/// <c>ExecuteRawAsync</c> job.
/// </para>
/// <para>
/// Every builder method returns a <b>new</b> instance, and paths are validated as the filter is
/// built, so a malformed path throws at the call site.
/// </para>
/// </remarks>
public sealed class IndexFilter
{
    private readonly IndexFilterTerm[] _terms;

    internal IndexFilter(params IndexFilterTerm[] terms) => _terms = terms;

    /// <summary>The collected terms, in call order.</summary>
    internal IReadOnlyList<IndexFilterTerm> Terms => _terms;

    /// <summary>
    /// Starts a filter matching the rows where <paramref name="jsonPath"/> is JSON null or absent.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new filter carrying the term</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public static IndexFilter IsNull(string jsonPath) => Create(jsonPath, requiresNull: true);

    /// <summary>
    /// Starts a filter matching the rows where <paramref name="jsonPath"/> holds a value.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.Email</c></param>
    /// <returns>A new filter carrying the term</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public static IndexFilter IsNotNull(string jsonPath) => Create(jsonPath, requiresNull: false);

    /// <summary>
    /// Adds an <c>IS NULL</c> test to the filter.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new filter carrying the added term</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public IndexFilter AndIsNull(string jsonPath) => With(NewTerm(jsonPath, requiresNull: true));

    /// <summary>
    /// Adds an <c>IS NOT NULL</c> test to the filter.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.Email</c></param>
    /// <returns>A new filter carrying the added term</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public IndexFilter AndIsNotNull(string jsonPath) => With(NewTerm(jsonPath, requiresNull: false));

    private static IndexFilter Create(string jsonPath, bool requiresNull) =>
        new([NewTerm(jsonPath, requiresNull)]);

    private IndexFilter With(IndexFilterTerm term) => new([.. _terms, term]);

    private static IndexFilterTerm NewTerm(string jsonPath, bool requiresNull)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be null or empty.", nameof(jsonPath));
        }

        return new IndexFilterTerm(SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath)), requiresNull);
    }
}
