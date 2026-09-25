using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

/// <summary>
/// Reads a flag whichever way the server wrote it: true/false, 1/0, or either as a string.
/// </summary>
/// <remarks>
/// Plex's JSON is generated from its XML, and several attributes that are booleans in meaning
/// arrive as numbers on some endpoints and as booleans on others.
/// </remarks>
public sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.TryGetInt64(out var number) && number != 0,
            JsonTokenType.String => reader.GetString() is { } text
                                    && (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)),
            JsonTokenType.Null => false,
            _ => throw new JsonException($"Expected a boolean, found {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBooleanValue(value);
    }
}

/// <summary>
/// Reads a value the server sends as one object on some endpoints and as a list of one on others
/// (an item's UltraBlur colours: an object in its metadata, a list in a playback decision).
/// </summary>
public sealed class ObjectOrFirstConverter<T> : JsonConverter<T>
    where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartArray:
                T? first = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var value = JsonSerializer.Deserialize(ref reader, info);
                    first ??= value;
                }

                return first;
            default:
                return JsonSerializer.Deserialize(ref reader, info);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JsonSerializer.Serialize(writer, value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
    }
}

[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(FlexibleBooleanConverter)])]
[JsonSerializable(typeof(PlexEnvelope))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(PlexPin))]
[JsonSerializable(typeof(PlexUser))]
[JsonSerializable(typeof(List<PlexResource>))]
public sealed partial class PlexJsonContext : JsonSerializerContext;
