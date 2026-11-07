using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Regions.Contracts.Tests;

/// <summary>Test helpers over the production serializer settings, which RegionStore owns (Regions.Store.ContractJson).</summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Regions.Store.ContractJson.Options;

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"null document: {path}");

    public static JsonNode ToNode<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, Options))!;
}

public static class Repo
{
    public static readonly string Root = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
    public static string Golden(params string[] parts) => Path(["Contracts", "golden", .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (CLAUDE.md) not found");
    }
}

public static class Schemas
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, JsonSchema> Loaded = new();

    /// <summary>Loads every schema once and registers it so relative $refs to common.v1 resolve by $id.</summary>
    public static JsonSchema Get(string fileName)
    {
        lock (Gate)
        {
            if (Loaded.Count == 0)
            {
                foreach (var file in Directory.GetFiles(Repo.Path("Contracts", "schema"), "*.schema.json"))
                {
                    var schema = JsonSchema.FromFile(file);
                    SchemaRegistry.Global.Register(schema);
                    Loaded[System.IO.Path.GetFileName(file)] = schema;
                }
            }
            return Loaded[fileName];
        }
    }

    public static IReadOnlyList<string> Errors(string schemaFile, JsonNode? instance)
    {
        var result = Get(schemaFile).Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return result.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}"))
            .ToList();
    }

    public static void AssertValid(string schemaFile, JsonNode? instance, string label)
    {
        var errors = Errors(schemaFile, instance);
        Xunit.Assert.True(errors.Count == 0, $"{label} violates {schemaFile}:\n" + string.Join("\n", errors));
    }
}
