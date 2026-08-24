namespace LiteDocumentStore;

/// <summary>
/// The optional DDL an index can carry: uniqueness, a collation, a descending direction and a
/// partial-index filter.
/// </summary>
/// <remarks>
/// <para>
/// Passed to the options-bearing <c>CreateIndexAsync</c> / <c>CreateCompositeIndexAsync</c>
/// overloads. <see cref="Unique"/> is the load-bearing one: a unique constraint over a JSON
/// field (a unique email) is otherwise impossible through the API.
/// </para>
/// <para>
/// On a composite index <see cref="Collation"/> and <see cref="Descending"/> apply to
/// <b>every</b> indexed column. A mixed per-column direction stays an <c>ExecuteRawAsync</c>
/// job rather than a per-column options list.
/// </para>
/// <para>
/// Creation skips an index whose name already exists, options and all, so changing the options
/// of an existing index means dropping it first (<c>DropIndexAsync</c>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await store.CreateIndexAsync&lt;Customer&gt;(
///     x => x.Email,
///     indexName: null,
///     new IndexOptions
///     {
///         Unique = true,
///         Collation = "NOCASE",
///         Filter = IndexFilter.IsNotNull("$.Email")
///     });
/// </code>
/// </example>
public sealed class IndexOptions
{
    /// <summary>
    /// Creates the index as <c>CREATE UNIQUE INDEX</c>, so a duplicate value fails the write.
    /// </summary>
    public bool Unique { get; init; }

    /// <summary>
    /// The collation the indexed expression is compared under, e.g. <c>NOCASE</c> or
    /// <c>BINARY</c>, or null for SQLite's default. A custom collation name is accepted; it is
    /// validated as a SQL identifier.
    /// </summary>
    public string? Collation { get; init; }

    /// <summary>
    /// Indexes the expression in descending order (<c>DESC</c>). The default ascending order
    /// emits no direction at all, leaving SQLite's own default.
    /// </summary>
    public bool Descending { get; init; }

    /// <summary>
    /// Restricts the index to the rows the filter matches (a partial index), or null to index
    /// every row.
    /// </summary>
    public IndexFilter? Filter { get; init; }
}
