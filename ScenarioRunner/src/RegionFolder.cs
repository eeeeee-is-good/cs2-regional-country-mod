using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;

namespace Regions.ScenarioRunner;

public sealed class RegionFolderException : Exception
{
    public RegionFolderException(string message) : base(message) { }
}

/// <summary>
/// Reads and writes a region folder in the region-store layout (Contracts/README.md):
///   manifest.json, snapshots/index.json, snapshots/&lt;cityId&gt;/&lt;seq&gt;.json.
/// Read-only for existing folders. Writing only creates a new folder and never overwrites anything.
/// Inbox ingestion and conflict handling belong to RegionStore (A2), not here.
/// </summary>
public static class RegionFolder
{
    public static string DefaultResourceTable => Path.Combine(AppContext.BaseDirectory, "resources", "resource-ids.v1.json");

    public static RegionState Load(string folder, string? resourceTable = null)
    {
        if (!Directory.Exists(folder)) throw new RegionFolderException($"folder not found: {folder}");

        var manifest = Read<RegionManifest>(Path.Combine(folder, "manifest.json"), "region-manifest.v1.schema.json");
        var index = Read<StoreIndex>(Path.Combine(folder, "snapshots", "index.json"), "store-index.v1.schema.json");
        if (index.RegionId != manifest.RegionId)
            throw new RegionFolderException($"snapshots/index.json is for region '{index.RegionId}', manifest is '{manifest.RegionId}'");

        var latest = new List<PublishedSnapshot>();
        foreach (var entry in index.Snapshots.GroupBy(s => s.CityId).Select(g => g.OrderByDescending(s => s.Seq).First()))
        {
            var path = Path.Combine(folder, "snapshots", entry.CityId, $"{entry.Seq}.json");
            if (!File.Exists(path)) throw new RegionFolderException($"index lists {entry.CityId} seq {entry.Seq}, but {path} is missing");
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != entry.FileSha256)
                throw new RegionFolderException($"{path}: SHA-256 {hash} does not match the index ({entry.FileSha256}). The file was edited after ingestion, or its line endings changed.");
            var snapshot = Parse<CitySnapshot>(bytes, path, "city-snapshot.v1.schema.json");
            if (snapshot.CityId != entry.CityId || snapshot.Seq != entry.Seq)
                throw new RegionFolderException($"{path}: contains {snapshot.CityId} seq {snapshot.Seq}, index says {entry.CityId} seq {entry.Seq}");
            latest.Add(new PublishedSnapshot(snapshot, entry.RegionSeq));
        }

        return new RegionState(manifest, latest, index.CurrentRegionSeq, LoadResourceTable(resourceTable ?? DefaultResourceTable));
    }

    public static List<ResourceDefinition> LoadResourceTable(string path)
    {
        if (!File.Exists(path)) throw new RegionFolderException($"resource table not found: {path}");
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            return doc["resources"]!.AsArray().Select(r => new ResourceDefinition(
                r!["resourceId"]!.GetValue<string>(),
                r["allowedFreightModes"]!.AsArray().Select(m => Enum.Parse<TransportMode>(m!.GetValue<string>())))).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException or InvalidOperationException)
        {
            throw new RegionFolderException($"{path}: not a valid resource table ({ex.Message})");
        }
    }

    /// <summary>Writes a synthetic region as a new folder. Refuses to touch an existing non-empty folder.</summary>
    public static void Write(RegionScenario scenario, string folder)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new RegionFolderException($"{folder} already exists and is not empty. Choose a new folder; nothing is overwritten.");

        scenario.SortManifest();
        var index = new StoreIndex { RegionId = scenario.Manifest.RegionId, CurrentRegionSeq = scenario.CurrentRegionSeq };
        var files = new List<(string Path, byte[] Bytes)> { (Path.Combine(folder, "manifest.json"), ContractJson.ToBytes(scenario.Manifest)) };

        foreach (var pub in scenario.LatestSnapshots.OrderBy(p => p.Snapshot.CityId, StringComparer.Ordinal))
        {
            var bytes = ContractJson.ToBytes(pub.Snapshot);
            files.Add((Path.Combine(folder, "snapshots", pub.Snapshot.CityId, $"{pub.Snapshot.Seq}.json"), bytes));
            index.Snapshots.Add(new StoredSnapshotInfo
            {
                CityId = pub.Snapshot.CityId,
                Seq = pub.Snapshot.Seq,
                RegionSeq = pub.RegionSeq,
                FileSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            });
        }
        files.Add((Path.Combine(folder, "snapshots", "index.json"), ContractJson.ToBytes(index)));

        // Validate everything before writing anything.
        foreach (var (path, bytes) in files)
            Validate(JsonNode.Parse(bytes), SchemaFor(path), path);

        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
        }
    }

    public static void WriteQuote(QuoteResult quote, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"quote.{quote.DestCityId}.{quote.ResourceId}.json");
        var bytes = ContractJson.ToBytes(quote);
        Validate(JsonNode.Parse(bytes), "quote-result.v1.schema.json", path);
        File.WriteAllBytes(path, bytes);
    }

    private static string SchemaFor(string path) =>
        Path.GetFileName(path) switch
        {
            "manifest.json" => "region-manifest.v1.schema.json",
            "index.json" => "store-index.v1.schema.json",
            _ => "city-snapshot.v1.schema.json",
        };

    private static T Read<T>(string path, string schema)
    {
        if (!File.Exists(path)) throw new RegionFolderException($"missing {path}");
        return Parse<T>(File.ReadAllBytes(path), path, schema);
    }

    private static T Parse<T>(byte[] bytes, string label, string schema)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(bytes); }
        catch (JsonException ex) { throw new RegionFolderException($"{label}: not valid JSON ({ex.Message})"); }
        Validate(node, schema, label);
        try { return ContractJson.FromBytes<T>(bytes, label); }
        catch (JsonException ex) { throw new RegionFolderException($"{label}: {ex.Message}"); }
    }

    // ---- schema validation against Contracts/schema (copied next to the executable by Contracts.csproj) ----

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "schema");
        var all = new Dictionary<string, JsonSchema>();
        foreach (var file in Directory.GetFiles(dir, "*.schema.json"))
        {
            var schema = JsonSchema.FromFile(file);
            SchemaRegistry.Global.Register(schema); // so relative $refs to common.v1 resolve by $id
            all[Path.GetFileName(file)] = schema;
        }
        return all;
    });

    private static void Validate(JsonNode? node, string schemaFile, string label)
    {
        var schema = Schemas.Value[schemaFile];
        var result = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return;
        var errors = result.Details.Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"  {d.InstanceLocation}: {e.Value}")).Take(10);
        throw new RegionFolderException($"{label} does not match {schemaFile}:\n" + string.Join("\n", errors));
    }
}
