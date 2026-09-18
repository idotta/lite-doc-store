using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LiteDocumentStore.Exceptions;

namespace LiteDocumentStore;

/// <summary>
/// Internal helper for JSON serialization optimized for SQLite JSONB storage.
/// All (de)serialization goes through the AOT-safe <see cref="JsonTypeInfo{T}"/> overloads,
/// resolving the type metadata from the caller-provided <see cref="JsonSerializerOptions"/>.
/// AOT consumers supply a source-generated <see cref="JsonSerializerContext"/> via
/// <see cref="DocumentStoreOptions.SerializerOptions"/>; when none is supplied the store
/// falls back to <see cref="CreateDefaultReflectionOptions"/>, which
/// <see cref="DocumentStoreOptions.ThrowIfSerializerOptionsUnusable"/> makes unreachable where
/// dynamic code is unsupported, by refusing a null
/// <see cref="DocumentStoreOptions.SerializerOptions"/> both at validation and at construction.
/// </summary>
internal static class JsonHelper
{
    /// <summary>
    /// Builds the reflection-based fallback options used when the consumer does not supply
    /// their own <see cref="JsonSerializerOptions"/>. This is the single quarantined spot for
    /// reflection-based serialization: it is not AOT/trim safe and is only reached on the
    /// fallback path, which <see cref="DocumentStoreOptions.ThrowIfSerializerOptionsUnusable"/>
    /// refuses under Native AOT — at validation and again in the constructor that calls this.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DocumentStoreOptions.ThrowIfSerializerOptionsUnusable refuses a null SerializerOptions when RuntimeFeature.IsDynamicCodeSupported is false, and runs both in Validate() and in the DocumentStore constructor that calls this helper, so it is unreachable under Native AOT; AOT consumers supply a source-generated JsonSerializerContext instead.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "DocumentStoreOptions.ThrowIfSerializerOptionsUnusable refuses a null SerializerOptions when RuntimeFeature.IsDynamicCodeSupported is false, and runs both in Validate() and in the DocumentStore constructor that calls this helper, so it is unreachable under Native AOT; AOT consumers supply a source-generated JsonSerializerContext instead.")]
    public static JsonSerializerOptions CreateDefaultReflectionOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
    }

    /// <summary>
    /// Serializes an object to UTF-8 encoded JSON bytes for JSONB storage.
    /// </summary>
    /// <exception cref="DocumentSerializationException">Thrown when serialization fails</exception>
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions options)
    {
        try
        {
            var typeInfo = ResolveTypeInfo<T>(options, "serialize");
            return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new DocumentSerializationException(
                $"Failed to serialize object of type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DocumentSerializationException(
                UnsupportedTypeMessage<T>("serialize"),
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// Deserializes UTF-8 encoded JSON bytes to a typed object.
    /// </summary>
    /// <exception cref="DocumentSerializationException">Thrown when deserialization fails</exception>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonSerializerOptions options)
    {
        if (utf8Json.IsEmpty)
        {
            return default;
        }

        try
        {
            var typeInfo = ResolveTypeInfo<T>(options, "deserialize");
            return JsonSerializer.Deserialize(utf8Json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new DocumentSerializationException(
                $"Failed to deserialize JSON to type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DocumentSerializationException(
                UnsupportedTypeMessage<T>("deserialize"),
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// Deserializes a JSON string to a typed object.
    /// This overload handles string data from the database.
    /// </summary>
    /// <exception cref="DocumentSerializationException">Thrown when deserialization fails</exception>
    public static T? Deserialize<T>(string? json, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        try
        {
            var typeInfo = ResolveTypeInfo<T>(options, "deserialize");
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new DocumentSerializationException(
                $"Failed to deserialize JSON to type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DocumentSerializationException(
                UnsupportedTypeMessage<T>("deserialize"),
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// The message for a <see cref="NotSupportedException"/> out of
    /// <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> — overwhelmingly a type the configured
    /// resolver does not cover, which the framework's own wording buries under source-generation
    /// advice.
    /// </summary>
    private static string UnsupportedTypeMessage<T>(string verb) =>
        $"Cannot {verb} type {typeof(T).Name} with the configured JsonSerializerOptions: the type " +
        "has no JsonTypeInfo metadata (register it with the source-generated JsonSerializerContext, " +
        "or supply a TypeInfoResolver that covers it), or it is not serializable.";

    /// <summary>
    /// Resolves the type metadata, and is the <em>only</em> statement whose
    /// <see cref="InvalidOperationException"/> is translated. System.Text.Json propagates exceptions
    /// other than <see cref="JsonException"/> and <see cref="NotSupportedException"/> unchanged from
    /// a custom <see cref="JsonConverter{T}"/>, so wrapping the <see cref="JsonSerializer"/> call in
    /// the same clause would relabel a converter's own failure as a metadata one. The callers keep
    /// their <see cref="JsonException"/> and <see cref="NotSupportedException"/> clauses around both
    /// statements, because both legitimately arrive from either side — a resolver that is present
    /// but does not cover <typeparamref name="T"/> answers <see cref="NotSupportedException"/> here,
    /// while a converter can answer it at the serializer call. The
    /// <see cref="DocumentSerializationException"/> thrown below passes through those clauses
    /// untouched, since it derives from neither.
    /// </summary>
    private static JsonTypeInfo<T> ResolveTypeInfo<T>(JsonSerializerOptions options, string verb)
    {
        try
        {
            return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        }
        catch (InvalidOperationException ex)
        {
            throw new DocumentSerializationException(
                InvalidMetadataMessage<T>(verb),
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// The message for an <see cref="InvalidOperationException"/> raised while the configured
    /// <see cref="JsonSerializerOptions"/> build or resolve the type's metadata — an invalid
    /// contract on the type itself, or a <see cref="IJsonTypeInfoResolver"/> that fails.
    /// </summary>
    private static string InvalidMetadataMessage<T>(string verb) =>
        $"Cannot {verb} type {typeof(T).Name} with the configured JsonSerializerOptions: its JSON " +
        "type metadata is invalid or could not be resolved (for example two members mapping to the " +
        "same JSON property name, an ambiguous [JsonConstructor], or a failing TypeInfoResolver).";
}
