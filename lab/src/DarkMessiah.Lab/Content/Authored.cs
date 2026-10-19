using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rusty.Engine;

namespace DarkMessiah.Lab.Content;

/// <summary>
/// Reads one authored content file through Engine Content into its domain's typed record; every error names the
/// file. Each domain declares its own strict JSON context (missing constructor values, nulls in non-nullable fields
/// and unknown members fail) and validates what parses with <see cref="Require"/> and the helpers below.
/// Provenance: rusty-riders <c>Content/Authored.cs</c>, trimmed.
/// </summary>
internal static class Authored
{
    internal static T Read<T>(IEngineContext engine, string path, JsonTypeInfo<T> type) where T : class
    {
        using ContentReference content = engine.Content.OpenReference(new ContentOpenRequest(path));
        ulong length = engine.Content.ReadReferenceInfo(content).Span[0].ByteLength;
        ReadOnlyMemory<byte> bytes = engine.Content.ReadBytes(new ContentReadBytesRequest(content, 0, checked((uint)length)));
        try
        {
            return JsonSerializer.Deserialize(bytes.Span, type) ?? throw new JsonException("The file holds no value.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"content/{path}: {error.Message}", error);
        }
    }

    /// <summary>Fails authored data that parses but cannot be used.</summary>
    internal static void Require(bool condition, string path, string field, string problem)
    {
        if (!condition) throw new InvalidOperationException($"content/{path} {field}: {problem}");
    }

    internal static void Positive(string path, string field, float value) =>
        Require(float.IsFinite(value) && value > 0, path, field, $"must be a positive number; found {value}.");

    internal static void AtLeast(string path, string field, float value, float minimum) =>
        Require(float.IsFinite(value) && value >= minimum, path, field, $"must be at least {minimum}; found {value}.");

    internal static void Within(string path, string field, float value, float minimum, float maximum) =>
        Require(float.IsFinite(value) && value >= minimum && value <= maximum, path, field,
            $"must be between {minimum} and {maximum}; found {value}.");

    internal static Vector3 Vector(float[] xyz) => xyz.Length == 3
        ? new Vector3(xyz[0], xyz[1], xyz[2])
        : throw new InvalidOperationException("Coordinates must have three components.");

    internal static Color Color(float[] rgb) => rgb.Length == 3
        ? new Color(rgb[0], rgb[1], rgb[2], 1)
        : throw new InvalidOperationException("Colours must have three components.");
}
