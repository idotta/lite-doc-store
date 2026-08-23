namespace LiteDocumentStore;

/// <summary>
/// The comparison a <see cref="DocumentQuery{T}"/> predicate applies to the value at a JSON path.
/// </summary>
public enum QueryOperator
{
    /// <summary>Matches when the value equals the bound value (<c>=</c>).</summary>
    Equal,

    /// <summary>Matches when the value differs from the bound value (<c>&lt;&gt;</c>).</summary>
    NotEqual,

    /// <summary>Matches when the value is greater than the bound value (<c>&gt;</c>).</summary>
    GreaterThan,

    /// <summary>Matches when the value is greater than or equal to the bound value (<c>&gt;=</c>).</summary>
    GreaterThanOrEqual,

    /// <summary>Matches when the value is less than the bound value (<c>&lt;</c>).</summary>
    LessThan,

    /// <summary>Matches when the value is less than or equal to the bound value (<c>&lt;=</c>).</summary>
    LessThanOrEqual,

    /// <summary>Matches SQLite's <c>LIKE</c> pattern (<c>%</c> and <c>_</c> wildcards, case-insensitive for ASCII).</summary>
    Like,

    /// <summary>Matches SQLite's <c>GLOB</c> pattern (<c>*</c> and <c>?</c> wildcards, case-sensitive).</summary>
    Glob,

    /// <summary>Matches when the value is one of a set of bound values (<c>IN</c>).</summary>
    In,

    /// <summary>Matches when the value at the path is JSON null or the path is absent (<c>IS NULL</c>).</summary>
    IsNull,

    /// <summary>Matches when the path resolves to a non-null value (<c>IS NOT NULL</c>).</summary>
    IsNotNull,

    /// <summary>Matches when the JSON array at the path contains the bound value.</summary>
    ArrayContains
}
