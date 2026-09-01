using System.Text;

namespace LiteDocumentStore;

/// <summary>
/// Default implementation of <see cref="ITableNamingConvention"/>: the type's namespace-qualified
/// name, with every separator folded to an underscore.
/// </summary>
/// <remarks>
/// <para>
/// <c>MyApp.Sales.Order</c> maps to <c>MyApp_Sales_Order</c>, a nested <c>MyApp.Outer+Inner</c> to
/// <c>MyApp_Outer_Inner</c>, and a type in the global namespace keeps its bare name. A constructed
/// generic type appends its arity and then each type argument, rendered by the same rule:
/// <c>MyApp.Box&lt;int&gt;</c> maps to <c>MyApp_Box_1_System_Int32</c>.
/// </para>
/// <para>
/// The fold is <b>collision-resistant, not injective, and that is deliberate.</b> Two families of
/// collision are knowingly accepted: a segment containing an underscore is indistinguishable from a
/// separator (namespace <c>A_</c> with type <c>B</c> and namespace <c>A</c> with type <c>_B</c> both
/// yield <c>A__B</c>), and an argument's extent is not recoverable (<c>Pair&lt;N.X, Y&gt;</c> and
/// <c>Pair&lt;N, X.Y&gt;</c> both yield <c>Pair_2_N_X_Y</c>). Closing them needs a self-delimiting
/// encoding — length-prefixed segments or reserved markers — whose output is not a name anyone would
/// type into a SQL client, and readable table names are the point of a store that stays open to raw
/// SQL. A <see cref="IDocumentStore"/> instead refuses to serve two different types that resolve to
/// the same table, so a residual collision is a loud failure at the call site rather than silent
/// cross-type overwriting; that guard also covers a custom convention, which no encoding here could.
/// </para>
/// <para>
/// Types the convention cannot name throw <see cref="NotSupportedException"/> rather than producing a
/// name the SQL identifier validator rejects against a parameter the caller never passed: open generic
/// definitions, generic parameters, arrays, pointers, by-ref types, types nested inside a generic type,
/// and types whose namespace or name is not expressible as an ASCII SQL identifier. Supply an
/// <see cref="ITableNamingConvention"/> of your own for any of those.
/// </para>
/// </remarks>
public sealed class DefaultTableNamingConvention : ITableNamingConvention
{
    /// <summary>
    /// Gets the shared instance. The convention is stateless, so nothing is gained by allocating more.
    /// </summary>
    public static DefaultTableNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string GetTableName<T>() => GetTableName(typeof(T));

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// The type has no expressible default table name — see the remarks on
    /// <see cref="DefaultTableNamingConvention"/> for the shapes that are refused.
    /// </exception>
    public string GetTableName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var builder = new StringBuilder();
        AppendName(builder, type, type);
        return builder.ToString();
    }

    /// <summary>
    /// Appends <paramref name="type"/>'s folded name, recursing through generic arguments.
    /// </summary>
    /// <param name="builder">Accumulates the folded name.</param>
    /// <param name="type">The type to render.</param>
    /// <param name="requested">
    /// The type the caller asked about, so a rejection deep inside a generic argument still names the
    /// type the call site actually passed alongside the offending one.
    /// </param>
    private static void AppendName(StringBuilder builder, Type type, Type requested)
    {
        RequireSupported(type, requested);

        // Outermost declaring type first, so a nested type reads in source order.
        var chain = new List<Type>();
        for (var link = type; link is not null; link = link.DeclaringType)
        {
            chain.Add(link);
        }

        chain.Reverse();

        if (chain[0].Namespace is { Length: > 0 } @namespace)
        {
            foreach (var part in @namespace.Split('.'))
            {
                AppendSegment(builder, part, type, requested);
            }
        }

        foreach (var link in chain)
        {
            AppendSegment(builder, StripArity(link.Name), type, requested);
        }

        if (!type.IsConstructedGenericType)
        {
            return;
        }

        // The arity distinguishes two same-named generics of different arity; it deliberately does
        // not delimit the arguments, which is the accepted collision documented on the class.
        var arguments = type.GenericTypeArguments;
        builder.Append('_').Append(arguments.Length);

        foreach (var argument in arguments)
        {
            // No separator here: the argument's first segment supplies it.
            AppendName(builder, argument, requested);
        }
    }

    private static void AppendSegment(StringBuilder builder, string segment, Type type, Type requested)
    {
        RequireIdentifierShaped(segment, type, requested);

        if (builder.Length > 0)
        {
            builder.Append('_');
        }

        builder.Append(segment);
    }

    /// <summary>
    /// Drops the <c>`N</c> arity suffix reflection puts on a generic type's simple name.
    /// </summary>
    private static string StripArity(string name)
    {
        var backtick = name.IndexOf('`', StringComparison.Ordinal);
        return backtick < 0 ? name : name[..backtick];
    }

    /// <summary>
    /// Refuses the type shapes the fold cannot name.
    /// </summary>
    /// <remarks>
    /// A type nested inside a generic type is refused as a scope decision, not because the rendering
    /// is impossible: <see cref="Type.GetGenericArguments"/> returns the chain's arguments flattened
    /// outermost-first, so each declaring level's arity would partition them. Document types of that
    /// shape are rare enough that refusing them beats freezing an on-disk encoding for them.
    /// </remarks>
    private static void RequireSupported(Type type, Type requested)
    {
        if (type.IsGenericParameter)
        {
            throw Unsupported(type, requested, "it is a generic parameter");
        }

        if (type.IsArray)
        {
            throw Unsupported(type, requested, "it is an array type");
        }

        if (type.IsPointer)
        {
            throw Unsupported(type, requested, "it is a pointer type");
        }

        if (type.IsByRef)
        {
            throw Unsupported(type, requested, "it is a by-ref type");
        }

        if (type.ContainsGenericParameters)
        {
            throw Unsupported(
                type,
                requested,
                "it is an open generic type; only a constructed generic type has a table name");
        }

        for (var declaring = type.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
        {
            if (declaring.IsGenericType)
            {
                throw Unsupported(
                    type,
                    requested,
                    $"it is nested inside the generic type '{declaring.Name}'");
            }
        }
    }

    /// <summary>
    /// Refuses a segment the SQL identifier grammar cannot carry — C# admits Unicode identifiers,
    /// the generator's validator does not.
    /// </summary>
    private static void RequireIdentifierShaped(string segment, Type type, Type requested)
    {
        var shaped = segment.Length > 0
            && (char.IsAsciiLetter(segment[0]) || segment[0] == '_');

        for (var i = 1; shaped && i < segment.Length; i++)
        {
            shaped = char.IsAsciiLetterOrDigit(segment[i]) || segment[i] == '_';
        }

        if (!shaped)
        {
            throw Unsupported(
                type,
                requested,
                $"the name segment '{segment}' is not expressible as an ASCII SQL identifier");
        }
    }

    private static NotSupportedException Unsupported(Type type, Type requested, string because)
    {
        var subject = type == requested
            ? $"Type '{type}'"
            : $"Type '{requested}' (through '{type}')";

        return new NotSupportedException(
            $"{subject} has no default table name because {because}. " +
            $"Supply an {nameof(ITableNamingConvention)} that names it.");
    }
}
