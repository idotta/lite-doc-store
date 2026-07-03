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
/// falls back to <see cref="CreateDefaultReflectionOptions"/> (non-AOT only).
/// </summary>
internal static class JsonHelper
{
    /// <summary>
    /// Builds the reflection-based fallback options used when the consumer does not supply
    /// their own <see cref="JsonSerializerOptions"/>. This is the single quarantined spot for
    /// reflection-based serialization: it is not AOT/trim safe and is only reached on the
    /// fallback path. AOT consumers always provide a source-generated context and never hit it.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflection-based JSON is the documented non-AOT fallback; AOT consumers supply a source-generated JsonSerializerContext via DocumentStoreOptions.SerializerOptions.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reflection-based JSON is the documented non-AOT fallback; AOT consumers supply a source-generated JsonSerializerContext via DocumentStoreOptions.SerializerOptions.")]
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
    /// <exception cref="SerializationException">Thrown when serialization fails</exception>
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions options)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to serialize object of type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new SerializationException(
                $"Serialization not supported for type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// Deserializes UTF-8 encoded JSON bytes to a typed object.
    /// </summary>
    /// <exception cref="SerializationException">Thrown when deserialization fails</exception>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonSerializerOptions options)
    {
        if (utf8Json.IsEmpty)
        {
            return default;
        }

        try
        {
            var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(utf8Json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize JSON to type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
    }

    /// <summary>
    /// Deserializes a JSON string to a typed object.
    /// This overload handles string data from the database.
    /// </summary>
    /// <exception cref="SerializationException">Thrown when deserialization fails</exception>
    public static T? Deserialize<T>(string? json, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        try
        {
            var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize JSON to type {typeof(T).Name}.",
                typeof(T),
                ex);
        }
    }
}
