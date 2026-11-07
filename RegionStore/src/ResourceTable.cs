using System.Text.Json;
using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Engine;

namespace Regions.Store;

/// <summary>Reads Contracts/resources/resource-ids.v1.json (or a file of the same shape) into engine definitions.</summary>
public static class ResourceTable
{
    /// <summary>The copy of Contracts/resources/resource-ids.v1.json placed next to the running assembly.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "resources", "resource-ids.v1.json");

    public static List<ResourceDefinition> Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) throw new StoreException($"resource table not found: {path}");
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            return doc["resources"]!.AsArray().Select(r => new ResourceDefinition(
                r!["resourceId"]!.GetValue<string>(),
                r["allowedFreightModes"]!.AsArray().Select(m => Enum.Parse<TransportMode>(m!.GetValue<string>())))).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException or InvalidOperationException)
        {
            throw new StoreException($"{path}: not a valid resource table ({ex.Message})");
        }
    }
}
