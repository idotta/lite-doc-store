using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LiteDocumentStore;

/// <summary>
/// An immutable, composable set of field-level changes to a document of type
/// <typeparamref name="T"/>, applied by <c>PatchAsync</c> as a single statement.
/// </summary>
/// <remarks>
/// <para>
/// Every builder method returns a <b>new</b> instance, so a patch is safe to share, reuse and
/// branch from. Several operations in one patch become one round trip and one version bump —
/// which is the point: a read-modify-write of the whole document clobbers a concurrent
/// writer's edits to the fields it did not mean to touch.
/// </para>
/// <para>
/// Values are scalars. A nested object or array stays an <c>ExecuteRawAsync</c> job, so a patch
/// never needs a <c>JsonTypeInfo</c> for the value type and the whole API stays AOT/trim safe.
/// Paths are written explicitly (<c>$.Email</c>) and property names map as-is (PascalCase) to
/// match the default System.Text.Json serialization.
/// </para>
/// <para>
/// A value is normalized to the representation System.Text.Json wrote into the document, the
/// same way <see cref="DocumentQuery{T}"/> normalizes a bound value, so a patched field still
/// matches a query over it. <see cref="bool"/>, <see cref="decimal"/> and a <see cref="ulong"/>
/// above <see cref="long.MaxValue"/> are additionally written as JSON text: SQLite has no
/// boolean type, so a bound <c>true</c> would store the number <c>1</c>, and both wide numeric
/// types would round through a REAL and lose digits.
/// </para>
/// <para>
/// Arguments are validated as the patch is built, so a malformed path, an unsupported value
/// type or a path touched twice throws at the call site rather than at execution time.
/// </para>
/// <para>
/// Only an exactly repeated path is rejected, and SQLite applies the paths in call order, each
/// seeing the document as the previous ones left it. <i>Related</i> paths therefore compose:
/// removing <c>$.Items[0]</c> before <c>$.Items[1]</c> shifts the array under the second path,
/// and setting <c>$.A</c> before <c>$.A.B</c> writes into the value the first set just
/// installed. Sets always run before removes, whatever order they were added in.
/// </para>
/// </remarks>
/// <typeparam name="T">The document type the patch applies to</typeparam>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "The entry points have to name the document type — DocumentPatch<Person>.Set(...) " +
                    "is the whole builder — and a non-generic factory has nothing to infer T from.")]
public sealed class DocumentPatch<T>
{
    private static readonly PatchOperation[] NoOperations = [];
    private static readonly DocumentPatch<T> Empty = new(NoOperations);

    private readonly PatchOperation[] _operations;

    private DocumentPatch(PatchOperation[] operations) => _operations = operations;

    /// <summary>The collected operations, in call order.</summary>
    internal IReadOnlyList<PatchOperation> Operations => _operations;

    /// <summary>
    /// Starts a patch that writes <paramref name="value"/> at <paramref name="jsonPath"/>,
    /// creating the field when it is absent.
    /// </summary>
    /// <param name="jsonPath">The JSON path to write, e.g. <c>$.Email</c></param>
    /// <param name="value">
    /// The value to store; null writes JSON null, which keeps the field present — use
    /// <see cref="Remove(string)"/> to drop it instead
    /// </param>
    /// <returns>A new patch carrying the operation</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is null, empty or malformed, or when the value's type cannot be stored
    /// </exception>
    public static DocumentPatch<T> Set(string jsonPath, object? value) =>
        Empty.AndSet(jsonPath, value);

    /// <summary>
    /// Adds a write to the patch.
    /// </summary>
    /// <param name="jsonPath">The JSON path to write, e.g. <c>$.Email</c></param>
    /// <param name="value">
    /// The value to store; null writes JSON null, which keeps the field present — use
    /// <see cref="AndRemove(string)"/> to drop it instead
    /// </param>
    /// <returns>A new patch carrying the added operation</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is null, empty or malformed, when the value's type cannot be stored,
    /// or when the patch already touches that path
    /// </exception>
    public DocumentPatch<T> AndSet(string jsonPath, object? value) =>
        WithOperation(CreateSet(jsonPath, value));

    /// <summary>
    /// Starts a patch that removes <paramref name="jsonPath"/> from the document.
    /// </summary>
    /// <param name="jsonPath">The JSON path to remove, e.g. <c>$.Nickname</c></param>
    /// <returns>A new patch carrying the operation</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty or malformed</exception>
    public static DocumentPatch<T> Remove(string jsonPath) => Empty.AndRemove(jsonPath);

    /// <summary>
    /// Adds a removal to the patch.
    /// </summary>
    /// <param name="jsonPath">The JSON path to remove, e.g. <c>$.Nickname</c></param>
    /// <returns>A new patch carrying the added operation</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is null, empty or malformed, or when the patch already touches that path
    /// </exception>
    public DocumentPatch<T> AndRemove(string jsonPath) =>
        WithOperation(new PatchOperation(
            DocumentQuery<T>.NormalizePath(jsonPath), PatchOperationKind.Remove, null, AsJson: false));

    // Touching one path twice is a caller bug — a Set plus a Remove of the same path has no
    // defensible meaning, and two Sets silently drop one. UpsertManyAsync rejects duplicate
    // ids for the same reason.
    private DocumentPatch<T> WithOperation(PatchOperation operation)
    {
        for (var i = 0; i < _operations.Length; i++)
        {
            if (string.Equals(_operations[i].JsonPath, operation.JsonPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The patch already has an operation on '{operation.JsonPath}'; each path may be " +
                    "set or removed once.",
                    nameof(operation));
            }
        }

        return new DocumentPatch<T>([.. _operations, operation]);
    }

    private static PatchOperation CreateSet(string jsonPath, object? value)
    {
        var path = DocumentQuery<T>.NormalizePath(jsonPath);

        // SQL NULL reaches jsonb_set as JSON null, so an explicit null needs no conversion —
        // only a way past the non-null value validation below.
        if (value is null)
        {
            return new PatchOperation(path, PatchOperationKind.Set, null, AsJson: false);
        }

        // The types SQLite cannot store as themselves: bool has no storage class of its own
        // (a bound true writes 1), and decimal / a ulong past long.MaxValue would round
        // through REAL. Their JSON text, wrapped in json(...), lands in the document exactly.
        var asJson = value switch
        {
            bool flag => flag ? "true" : "false",
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            ulong number when number > long.MaxValue => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };

        return asJson is not null
            ? new PatchOperation(path, PatchOperationKind.Set, asJson, AsJson: true)
            : new PatchOperation(
                path,
                PatchOperationKind.Set,
                DocumentQuery<T>.ValidateValue(value, nameof(value)),
                AsJson: false);
    }
}
