using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Regions.ScenarioRunner;

/// <summary>
/// Serializer settings implementing Contracts/README.md "Serialization rules".
/// Same options as Tests/Contracts.Tests/TestSupport.cs; RegionStore (A2) will own the single production copy.
/// </summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,     // rule 3: write explicit nulls
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,             // rule 4: no NaN/Infinity strings
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }, // rule 2
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Rule 1: UTF-8 without BOM, LF line endings, trailing newline.</summary>
    public static byte[] ToBytes<T>(T value) =>
        Utf8NoBom.GetBytes(JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n") + "\n");

    public static T FromBytes<T>(byte[] bytes, string label) =>
        JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new RegionFolderException($"{label}: empty document");
}
