using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LiteDocumentStore;

/// <summary>
/// Turns a property-access expression into the JSON path the store's own serializer writes.
/// </summary>
/// <remarks>
/// <para>
/// Every segment is resolved through <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> — the
/// same metadata <see cref="JsonHelper"/> serializes through — so the path an index is created
/// over is by construction the path the documents carry. Reading the CLR member name instead
/// makes a <c>[JsonPropertyName]</c> or a naming policy produce an index over an expression that
/// is NULL in every row, which SQLite accepts: a UNIQUE index over it then enforces nothing.
/// </para>
/// <para>
/// Only member names are read from the expression tree and only <see cref="JsonTypeInfo"/> is
/// consulted, so this stays AOT/trim safe.
/// </para>
/// </remarks>
internal static class JsonPathResolver
{
    /// <summary>
    /// Resolves the serialized JSON path for a property-access expression, e.g.
    /// <c>x => x.Email</c> or <c>x => x.Address.City</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the expression is not a property access, when the serializer has no metadata
    /// for a type along the path, when a member is not serialized, or when its serialized name
    /// cannot be expressed as a JSON path.
    /// </exception>
    internal static string Resolve<T>(
        Expression<Func<T, object>> expression,
        JsonSerializerOptions serializerOptions,
        string paramName)
    {
        var body = expression.Body;

        // Handle convert expressions (when boxing value types to object)
        if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            body = unary.Operand;
        }

        // Collected innermost-first, so the walk out of the expression tree can be reversed into
        // document order without re-resolving anything.
        var hops = new List<(Type Container, string Member)>();
        var current = body;

        while (current is MemberExpression memberExpr)
        {
            var container = memberExpr.Expression?.Type ?? memberExpr.Member.DeclaringType;
            if (container is null)
            {
                break;
            }

            hops.Add((container, memberExpr.Member.Name));
            current = memberExpr.Expression;
        }

        if (hops.Count == 0)
        {
            throw new ArgumentException(
                "Expression must be a property access (e.g., x => x.Email or x => x.Address.City).",
                paramName);
        }

        var path = new StringBuilder("$");

        for (var i = hops.Count - 1; i >= 0; i--)
        {
            var (container, member) = hops[i];
            path.Append('.').Append(SerializedName(container, member, serializerOptions, paramName));
        }

        return path.ToString();
    }

    private static string SerializedName(
        Type container,
        string memberName,
        JsonSerializerOptions serializerOptions,
        string paramName)
    {
        JsonTypeInfo typeInfo;
        try
        {
            typeInfo = serializerOptions.GetTypeInfo(container);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException(
                $"The configured JsonSerializerOptions provide no metadata for '{container.Name}', " +
                $"so the serialized name of '{memberName}' cannot be resolved. Register the type with " +
                "the source-generated JsonSerializerContext, or pass the JSON path as a string.",
                paramName,
                ex);
        }

        JsonPropertyInfo? property = null;
        foreach (var candidate in typeInfo.Properties)
        {
            if ((candidate.AttributeProvider as MemberInfo)?.Name == memberName)
            {
                property = candidate;
                break;
            }
        }

        if (property is null)
        {
            throw new ArgumentException(
                $"'{container.Name}.{memberName}' has no serialized counterpart in the configured " +
                "JsonSerializerOptions, so no stored document carries it. Pass the JSON path as a string " +
                "if the field is written by something other than this type's serializer.",
                paramName);
        }

        // A [JsonIgnore]d member keeps its JsonPropertyInfo but loses its getter, so it is never
        // written: an index over it would be NULL in every row.
        if (property.Get is null)
        {
            throw new ArgumentException(
                $"'{container.Name}.{memberName}' is not serialized (it is [JsonIgnore]d or has no getter), " +
                "so no stored document carries it.",
                paramName);
        }

        return ValidPathMember(property.Name, container, memberName, paramName);
    }

    /// <summary>
    /// Re-checks the resolved name against the path grammar <see cref="SqlGenerator.ValidateJsonPath"/>
    /// enforces, so a serialized name the grammar cannot express is reported against the member that
    /// produced it rather than as an opaque bad path.
    /// </summary>
    private static string ValidPathMember(string name, Type container, string memberName, string paramName)
    {
        var valid = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_');

        for (var i = 1; valid && i < name.Length; i++)
        {
            valid = char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_';
        }

        if (!valid)
        {
            throw new ArgumentException(
                $"'{container.Name}.{memberName}' serializes as '{name}', which is not expressible as a " +
                "JSON path member (only ASCII letters, digits and underscores are supported, and the first " +
                "character cannot be a digit). Index it through ExecuteRawAsync.",
                paramName);
        }

        return name;
    }
}
