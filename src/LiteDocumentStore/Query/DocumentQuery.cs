using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LiteDocumentStore;

/// <summary>
/// An immutable, composable filter over the documents of type <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every builder method returns a <b>new</b> instance, so a query is safe to share, reuse and
/// branch from. Predicates combine with <c>AND</c> only.
/// </para>
/// <para>
/// <typeparamref name="T"/> is a type marker that selects the table through the store's
/// <see cref="ITableNamingConvention"/>; nothing here reflects over it, so the whole API is
/// AOT/trim safe. Paths are written explicitly (<c>$.Email</c>) and property names map as-is
/// (PascalCase) to match the default System.Text.Json serialization.
/// </para>
/// <para>
/// Arguments are validated as the query is built, so a malformed path, a value of an
/// unsupported type or a nonsensical operator/value pairing throws at the call site rather
/// than at execution time.
/// </para>
/// <para>
/// A bound value is normalized to the representation System.Text.Json wrote into the document
/// — <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="Guid"/> and <c>byte[]</c>
/// as their JSON text, <see cref="decimal"/>, <see cref="float"/> and a <see cref="ulong"/>
/// above <see cref="long.MaxValue"/> as the double SQLite parsed — because ADO otherwise binds
/// a shape that silently matches nothing. This assumes the default serialization: a custom
/// converter for one of those types changes the stored text and the normalization no longer
/// lines up.
/// </para>
/// </remarks>
/// <typeparam name="T">The document type the query selects</typeparam>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "The entry points have to name the document type — DocumentQuery<Person>.Where(...) " +
                    "is the whole builder — and a non-generic factory has nothing to infer T from.")]
public sealed class DocumentQuery<T>
{
    private static readonly QueryPredicate[] NoPredicates = [];
    private static readonly QueryOrdering[] NoOrderings = [];
    private static readonly object?[] NoValues = [];
    private static readonly DocumentQuery<T> Empty = new(NoPredicates, NoOrderings, null, null);

    // The round-trip shapes System.Text.Json writes, per DateTimeKind.
    private const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";
    private const string UtcDateTimeFormat = DateTimeFormat + "'Z'";
    private const string OffsetDateTimeFormat = DateTimeFormat + "zzz";

    private readonly QueryPredicate[] _predicates;
    private readonly QueryOrdering[] _orderings;
    private readonly int? _skip;
    private readonly int? _take;

    private DocumentQuery(QueryPredicate[] predicates, QueryOrdering[] orderings, int? skip, int? take)
    {
        _predicates = predicates;
        _orderings = orderings;
        _skip = skip;
        _take = take;
    }

    /// <summary>The collected filters, combined with <c>AND</c>.</summary>
    internal IReadOnlyList<QueryPredicate> Predicates => _predicates;

    /// <summary>The collected orderings, in call order.</summary>
    internal IReadOnlyList<QueryOrdering> Orderings => _orderings;

    /// <summary>The <c>OFFSET</c> to apply, or null when none was requested.</summary>
    internal int? SkipCount => _skip;

    /// <summary>The <c>LIMIT</c> to apply, or null when none was requested.</summary>
    internal int? TakeCount => _take;

    /// <summary>
    /// Starts a query that matches every document of type <typeparamref name="T"/>.
    /// </summary>
    /// <returns>A query with no predicates</returns>
    public static DocumentQuery<T> All() => Empty;

    /// <summary>
    /// Starts a query with a single filter.
    /// </summary>
    /// <param name="jsonPath">The JSON path to filter on, e.g. <c>$.Email</c></param>
    /// <param name="op">The comparison to apply</param>
    /// <param name="value">
    /// The value to compare against; must be null for <see cref="QueryOperator.IsNull"/> and
    /// <see cref="QueryOperator.IsNotNull"/>, and non-null otherwise
    /// </param>
    /// <returns>A new query carrying the filter</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is null, empty or malformed, when the operator and value do not
    /// pair up, or when the value's type cannot be bound
    /// </exception>
    public static DocumentQuery<T> Where(string jsonPath, QueryOperator op, object? value) =>
        Empty.And(jsonPath, op, value);

    /// <summary>
    /// Adds a filter, combined with the existing ones using <c>AND</c>.
    /// </summary>
    /// <param name="jsonPath">The JSON path to filter on, e.g. <c>$.Email</c></param>
    /// <param name="op">The comparison to apply</param>
    /// <param name="value">
    /// The value to compare against; must be null for <see cref="QueryOperator.IsNull"/> and
    /// <see cref="QueryOperator.IsNotNull"/>, and non-null otherwise
    /// </param>
    /// <returns>A new query carrying the added filter</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is null, empty or malformed, when the operator and value do not
    /// pair up, or when the value's type cannot be bound
    /// </exception>
    public DocumentQuery<T> And(string jsonPath, QueryOperator op, object? value) =>
        WithPredicate(CreatePredicate(jsonPath, op, value));

    /// <summary>
    /// Starts a query filtering on a path that is JSON null or absent.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new query carrying the filter</returns>
    public static DocumentQuery<T> WhereIsNull(string jsonPath) =>
        Where(jsonPath, QueryOperator.IsNull, null);

    /// <summary>
    /// Adds an <c>IS NULL</c> filter, combined with <c>AND</c>.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new query carrying the added filter</returns>
    public DocumentQuery<T> AndIsNull(string jsonPath) =>
        And(jsonPath, QueryOperator.IsNull, null);

    /// <summary>
    /// Starts a query filtering on a path that resolves to a non-null value.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new query carrying the filter</returns>
    public static DocumentQuery<T> WhereIsNotNull(string jsonPath) =>
        Where(jsonPath, QueryOperator.IsNotNull, null);

    /// <summary>
    /// Adds an <c>IS NOT NULL</c> filter, combined with <c>AND</c>.
    /// </summary>
    /// <param name="jsonPath">The JSON path to test, e.g. <c>$.DeletedAt</c></param>
    /// <returns>A new query carrying the added filter</returns>
    public DocumentQuery<T> AndIsNotNull(string jsonPath) =>
        And(jsonPath, QueryOperator.IsNotNull, null);

    /// <summary>
    /// Starts a query filtering on a path whose value is one of <paramref name="values"/>.
    /// </summary>
    /// <param name="jsonPath">The JSON path to filter on, e.g. <c>$.Status</c></param>
    /// <param name="values">The values to match; at least one, none of them null</param>
    /// <returns>A new query carrying the filter</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is null</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="values"/> is empty, or holds a null or unbindable element
    /// </exception>
    public static DocumentQuery<T> WhereIn(string jsonPath, IEnumerable<object?> values) =>
        Empty.AndIn(jsonPath, values);

    /// <summary>
    /// Adds an <c>IN</c> filter, combined with <c>AND</c>.
    /// </summary>
    /// <param name="jsonPath">The JSON path to filter on, e.g. <c>$.Status</c></param>
    /// <param name="values">The values to match; at least one, none of them null</param>
    /// <returns>A new query carrying the added filter</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is null</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="values"/> is empty, or holds a null or unbindable element
    /// </exception>
    public DocumentQuery<T> AndIn(string jsonPath, IEnumerable<object?> values) =>
        WithPredicate(CreateInPredicate(jsonPath, values));

    /// <summary>
    /// Starts a query filtering on a JSON array at <paramref name="jsonPath"/> that contains
    /// <paramref name="value"/>.
    /// </summary>
    /// <param name="jsonPath">The JSON path of the array, e.g. <c>$.Tags</c></param>
    /// <param name="value">The element to look for</param>
    /// <returns>A new query carrying the filter</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="value"/> is null or its type cannot be bound
    /// </exception>
    public static DocumentQuery<T> WhereArrayContains(string jsonPath, object value) =>
        Empty.AndArrayContains(jsonPath, value);

    /// <summary>
    /// Adds an "array contains" filter, combined with <c>AND</c>.
    /// </summary>
    /// <param name="jsonPath">The JSON path of the array, e.g. <c>$.Tags</c></param>
    /// <param name="value">The element to look for</param>
    /// <returns>A new query carrying the added filter</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="value"/> is null or its type cannot be bound
    /// </exception>
    public DocumentQuery<T> AndArrayContains(string jsonPath, object value) =>
        And(jsonPath, QueryOperator.ArrayContains, value);

    /// <summary>
    /// Appends an ordering. Call it more than once to sort by several paths, in call order.
    /// </summary>
    /// <param name="jsonPath">The JSON path to sort on, e.g. <c>$.CreatedAt</c></param>
    /// <param name="descending">True to sort descending; the default is ascending</param>
    /// <returns>A new query carrying the added ordering</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public DocumentQuery<T> OrderBy(string jsonPath, bool descending = false)
    {
        var ordering = new QueryOrdering(NormalizePath(jsonPath), descending);
        return new DocumentQuery<T>(_predicates, [.. _orderings, ordering], _skip, _take);
    }

    /// <summary>
    /// Skips the first <paramref name="offset"/> matching documents (<c>OFFSET</c>).
    /// </summary>
    /// <param name="offset">The number of documents to skip; zero or more</param>
    /// <returns>A new query carrying the offset</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> is negative</exception>
    public DocumentQuery<T> Skip(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new DocumentQuery<T>(_predicates, _orderings, offset, _take);
    }

    /// <summary>
    /// Returns at most <paramref name="limit"/> matching documents (<c>LIMIT</c>).
    /// </summary>
    /// <param name="limit">The maximum number of documents to return; one or more</param>
    /// <returns>A new query carrying the limit</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="limit"/> is negative or zero — a zero-row query is meaningless
    /// </exception>
    public DocumentQuery<T> Take(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfZero(limit);
        return new DocumentQuery<T>(_predicates, _orderings, _skip, limit);
    }

    private DocumentQuery<T> WithPredicate(QueryPredicate predicate) =>
        new([.. _predicates, predicate], _orderings, _skip, _take);

    private static QueryPredicate CreatePredicate(string jsonPath, QueryOperator op, object? value)
    {
        var path = NormalizePath(jsonPath);

        if (!Enum.IsDefined(op))
        {
            throw new ArgumentException($"Unknown query operator '{op}'.", nameof(op));
        }

        if (op is QueryOperator.IsNull or QueryOperator.IsNotNull)
        {
            return value is null
                ? new QueryPredicate(path, op, null, NoValues)
                : throw new ArgumentException($"The '{op}' operator takes no value; pass null.", nameof(value));
        }

        if (op == QueryOperator.In)
        {
            throw new ArgumentException(
                "The 'In' operator needs a collection of values; use WhereIn or AndIn.",
                nameof(op));
        }

        if (value is null)
        {
            throw new ArgumentException(
                op is QueryOperator.Equal or QueryOperator.NotEqual
                    ? $"The '{op}' operator cannot compare against null; use IsNull or IsNotNull instead."
                    : $"The '{op}' operator requires a value.",
                nameof(value));
        }

        if ((op is QueryOperator.Like or QueryOperator.Glob) && value is not string)
        {
            throw new ArgumentException(
                $"The '{op}' operator requires a string pattern, but got '{value.GetType()}'.",
                nameof(value));
        }

        return new QueryPredicate(path, op, ValidateValue(value, nameof(value)), NoValues);
    }

    private static QueryPredicate CreateInPredicate(string jsonPath, IEnumerable<object?> values)
    {
        var path = NormalizePath(jsonPath);
        ArgumentNullException.ThrowIfNull(values);

        var materialized = values.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("The 'In' operator requires at least one value.", nameof(values));
        }

        for (var i = 0; i < materialized.Length; i++)
        {
            materialized[i] = ValidateValue(materialized[i], nameof(values));
        }

        return new QueryPredicate(path, QueryOperator.In, null, materialized);
    }

    // The path grammar lives in SqlGenerator.ValidateJsonPath, the single boundary guarding
    // interpolated paths. Calling it here only moves the failure forward to the call site.
    private static string NormalizePath(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be null or empty.", nameof(jsonPath));
        }

        return SqlGenerator.ValidateJsonPath(jsonPath, nameof(jsonPath));
    }

    // Only types Microsoft.Data.Sqlite binds directly. Anything else would either throw deep
    // inside ADO or, worse, round-trip through an unexpected representation.
    private static object ValidateValue(object? value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        if (value is not (string or bool or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal or DateTime or DateTimeOffset
            or Guid or byte[]))
        {
            throw new ArgumentException(
                $"Values of type '{value.GetType()}' cannot be bound. Supported types are string, bool, " +
                "the integral types, float, double, decimal, DateTime, DateTimeOffset, Guid and byte[].",
                paramName);
        }

        return NormalizeBoundValue(value);
    }

    // ADO binds several of those types differently from how System.Text.Json wrote them into
    // the document ("2024-03-01 00:00:00" vs "2024-03-01T00:00:00", a blob vs base64 text, a
    // decimal as TEXT vs the REAL json_extract yields), and the mismatch matches nothing
    // rather than failing. Rewrite the value into the stored shape; the rest measured clean.
    internal static object NormalizeBoundValue(object value) => value switch
    {
        DateTime dateTime => FormatDateTime(dateTime),
        DateTimeOffset offset => offset.ToString(OffsetDateTimeFormat, CultureInfo.InvariantCulture),
        Guid guid => guid.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        decimal number => (double)number,
        // The shortest round-trip text is what the serializer wrote, so re-parsing it gives the
        // double SQLite holds; widening the float directly does not (0.1f -> 0.100000001490116).
        float number => double.Parse(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        // Past long.MaxValue ADO wraps the value negative, and SQLite stored it as REAL anyway.
        ulong number when number > long.MaxValue => (double)number,
        _ => value
    };

    private static string FormatDateTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.ToString(UtcDateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeKind.Local => value.ToString(OffsetDateTimeFormat, CultureInfo.InvariantCulture),
        _ => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture)
    };
}
