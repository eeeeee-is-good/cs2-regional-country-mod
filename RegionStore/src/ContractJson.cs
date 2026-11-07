using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Regions.Store;

/// <summary>
/// The engine side's single copy of the serializer settings from Contracts/README.md "Serialization rules".
/// The in-game exporter must produce the same bytes by its own means (V-03).
/// </summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,                  // rule 1
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,                 // rule 3: write explicit nulls
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,       // unknown properties are invalid
        NumberHandling = JsonNumberHandling.Strict,                         // rule 4: no NaN/Infinity strings
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }, // rule 2
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Rule 1: UTF-8 without BOM, LF line endings, one trailing newline.</summary>
    public static byte[] ToBytes<T>(T value) =>
        Utf8NoBom.GetBytes(JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n") + "\n");

    /// <summary>Throws JsonException on malformed JSON, unknown properties or integer enums.</summary>
    public static T FromBytes<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new JsonException("document is null");
}
