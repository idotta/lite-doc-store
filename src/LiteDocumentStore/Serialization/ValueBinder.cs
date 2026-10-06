using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;

namespace LiteDocumentStore;

/// <summary>
/// Binds a query or patch value in the shape the serializer wrote at that path.
/// </summary>
/// <remarks>
/// <para>
/// The builders cannot see the serializer, so <see cref="DocumentQuery{T}.NormalizeBoundValue"/>
/// guesses the stored shape from the value's type alone — right under the default serialization,
/// silently wrong under a converter: a string enum compared as its integer, a
/// <see cref="DateTime"/> written as epoch millis compared as text. Each match nothing.
/// </para>
/// <para>
/// At execution the path is resolved through the same <see cref="JsonTypeInfo"/> metadata the
/// documents were written through (<see cref="JsonPathResolver.ResolvePathLeaf"/>), and a value
/// of the leaf's own type is serialized by it — a converter declared on the property, on the
/// type or in the options included. The JSON that comes out is what the document holds, so the
/// value is bound as that: a string, an integer, a double, or 1/0 for a boolean, which is what
/// <c>json_extract</c> yields for each. A patch binds a string or an integer the same way and
/// writes anything else — a boolean, a number whose exact text matters — as the JSON text itself.
/// </para>
/// <para>
/// Anything else falls back to the builder's normalization — a path the metadata does not
/// describe (a polymorphic-only key, an <c>object</c> or dictionary value), or a value whose type
/// is not the leaf's. An <see cref="Enum"/> is the exception: its stored form is a number or a
/// name depending on a converter only the metadata can name, so an enum that cannot be resolved
/// is refused rather than bound as a guess.
/// </para>
/// <para>
/// Resolution is cached per serializer options and per <c>(document type, path)</c>, failures
/// included, and the writer is reused per thread, so steady-state cost is a lookup plus serializing
/// one scalar into an existing buffer. All of it goes through
/// the non-generic <see cref="JsonSerializer.Serialize(Utf8JsonWriter, object?, JsonTypeInfo)"/>
/// overload, which is AOT-safe because the metadata comes from the configured resolver.
/// </para>
/// </remarks>
internal static class ValueBinder
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<BindingKey, JsonTypeInfo?>> Cache = new();

    // Serialization is synchronous, so one writer and buffer per thread serve every bind; a
    // buffer grown past this by an unusually large value is dropped rather than kept alive.
    private const int RetainedBufferLimit = 4096;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _buffer;

    [ThreadStatic]
    private static Utf8JsonWriter? _writer;

    private readonly record struct BindingKey(Type Root, string JsonPath, bool Element);

    /// <summary>
    /// Returns the value to bind for a query predicate.
    /// </summary>
    /// <param name="root">The document type the path is relative to</param>
    /// <param name="jsonPath">The validated path</param>
    /// <param name="element">
    /// True when <paramref name="value"/> is compared with the <em>elements</em> of the array at
    /// the path (<see cref="QueryOperator.ArrayContains"/>) rather than with the path itself
    /// </param>
    /// <param name="value">The caller's value, as passed</param>
    /// <param name="normalized">The builder's normalization of it — the fallback</param>
    /// <param name="ranged">True for a range operator, where only an order-preserving shape works</param>
    /// <param name="serializerOptions">The store's serializer options</param>
    /// <param name="paramName">The caller-facing parameter a refusal is reported against</param>
    internal static object BindQueryValue(
        Type root,
        string jsonPath,
        bool element,
        object value,
        object normalized,
        bool ranged,
        JsonSerializerOptions serializerOptions,
        string paramName)
    {
        if (!TrySerialize(root, jsonPath, element, value, serializerOptions, out var json))
        {
            return Fallback(value, normalized, jsonPath, root, paramName);
        }

        var reader = new Utf8JsonReader(json);
        reader.Read();
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                // A name sorts alphabetically, not by the enum's value, so a range over it would
                // silently admit and drop the wrong documents.
                if (ranged && value is Enum)
                {
                    throw new ArgumentException(
                        $"A range comparison at '{jsonPath}' cannot use the enum value '{value}': the " +
                        "serializer stores this enum as its name, and names do not sort by value. " +
                        "Compare with Equal or In, or store the enum as a number.",
                        paramName);
                }

                return reader.GetString()!;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var integer) ? integer : (object)reader.GetDouble();
            case JsonTokenType.True:
                return 1L;
            case JsonTokenType.False:
                return 0L;
            default:
                throw new ArgumentException(
                    $"The value '{value}' compared at '{jsonPath}' serializes to a JSON " +
                    $"{Describe(reader.TokenType)}, and only a string, number or boolean can be compared.",
                    paramName);
        }
    }

    /// <summary>
    /// Returns the patch operation to execute: its value replaced by the JSON the serializer
    /// writes for it when the path resolves, or unchanged otherwise.
    /// </summary>
    internal static PatchOperation BindPatchValue(
        Type root,
        PatchOperation operation,
        JsonSerializerOptions serializerOptions,
        string paramName)
    {
        if (operation is not { Kind: PatchOperationKind.Set, RawValue: { } value })
        {
            return operation;
        }

        if (!TrySerialize(root, operation.JsonPath, element: false, value, serializerOptions, out var json))
        {
            Fallback(value, operation.Value!, operation.JsonPath, root, paramName);
            return operation;
        }

        // A string or an integer lands in the document unchanged when bound as itself, which
        // spares SQLite parsing JSON text; everything else — a boolean, a number whose exact text
        // matters, a structure — travels as that text inside json(...).
        var reader = new Utf8JsonReader(json);
        reader.Read();
        return reader.TokenType switch
        {
            JsonTokenType.String => operation with { Value = reader.GetString()!, AsJson = false },
            JsonTokenType.Number when reader.TryGetInt64(out var integer) => operation with { Value = integer, AsJson = false },
            _ => operation with { Value = Encoding.UTF8.GetString(json), AsJson = true }
        };
    }

    private static object Fallback(object value, object normalized, string jsonPath, Type root, string paramName) =>
        value is Enum
            ? throw new ArgumentException(
                $"The enum value '{value}' cannot be bound at '{jsonPath}': the path does not resolve " +
                $"through the serializer's metadata for '{root}' to a member of type '{value.GetType()}', " +
                "so whether the document stores it as a number or as its name is unknown. Bind the stored " +
                "form instead — the underlying integer or the name.",
                paramName)
            : normalized;

    private static bool TrySerialize(
        Type root,
        string jsonPath,
        bool element,
        object value,
        JsonSerializerOptions serializerOptions,
        out ReadOnlySpan<byte> json)
    {
        json = default;
        var typeInfo = Cache
            .GetValue(serializerOptions, static _ => new ConcurrentDictionary<BindingKey, JsonTypeInfo?>())
            .GetOrAdd(new BindingKey(root, jsonPath, element), static (key, options) => Resolve(key, options), serializerOptions);

        // Only a value of the leaf's own type is the serializer's to shape; an int passed for a
        // long member, say, keeps the builder's normalization, as it always has.
        if (typeInfo is null
            || (value.GetType() != typeInfo.Type && value.GetType() != Nullable.GetUnderlyingType(typeInfo.Type)))
        {
            return false;
        }

        var buffer = _buffer ??= new ArrayBufferWriter<byte>(64);
        buffer.ResetWrittenCount();
        var writer = _writer ??= new Utf8JsonWriter(buffer);
        writer.Reset(buffer);
        try
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new DocumentSerializationException(
                $"Serializing the value '{value}' bound at '{jsonPath}' failed: {ex.Message}", ex);
        }

        json = buffer.WrittenSpan;
        if (buffer.Capacity > RetainedBufferLimit)
        {
            // The span stays valid: the dropped buffer is still referenced through it until the
            // caller is done, and only the next bind on this thread would have reused it.
            _buffer = null;
            _writer = null;
        }

        return true;
    }

    private static JsonTypeInfo? Resolve(BindingKey key, JsonSerializerOptions serializerOptions)
    {
        if (JsonPathResolver.ResolvePathLeaf(key.Root, key.JsonPath, serializerOptions) is not { } leaf)
        {
            return null;
        }

        try
        {
            if (key.Element)
            {
                // A converter on the array property writes the whole array, elements included.
                if (leaf.PropertyConverter is not null)
                {
                    return null;
                }

                var array = serializerOptions.GetTypeInfo(leaf.Type);
                return array is { Kind: JsonTypeInfoKind.Enumerable, ElementType: { } elementType }
                    ? serializerOptions.GetTypeInfo(Nullable.GetUnderlyingType(elementType) ?? elementType)
                    : null;
            }

            if (leaf.PropertyConverter is not { } converter)
            {
                // The built-in Nullable<T> converter writes a non-null value as T's converter does.
                return serializerOptions.GetTypeInfo(Nullable.GetUnderlyingType(leaf.Type) ?? leaf.Type);
            }

            // A property-level converter is not in the options, so it is placed there — on a copy,
            // since the store's options are shared and read-only — to obtain metadata that writes
            // through it. Cached, so the copy is made once per path.
            var target = converter.CanConvert(leaf.Type)
                ? leaf.Type
                : Nullable.GetUnderlyingType(leaf.Type) is { } underlying && converter.CanConvert(underlying)
                    ? underlying
                    : null;
            if (target is null)
            {
                return null;
            }

            var withConverter = new JsonSerializerOptions(serializerOptions);
            withConverter.Converters.Insert(0, converter);
            return withConverter.GetTypeInfo(target);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string Describe(JsonTokenType tokenType) => tokenType switch
    {
        JsonTokenType.StartObject => "object",
        JsonTokenType.StartArray => "array",
        JsonTokenType.Null => "null",
        _ => tokenType.ToString()
    };
}
