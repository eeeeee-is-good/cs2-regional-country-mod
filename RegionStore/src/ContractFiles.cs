using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Regions.Store;

public class StoreException : Exception
{
    public StoreException(string message) : base(message) { }
}

/// <summary>A contract file that cannot be read: malformed (Corrupt) or valid JSON that breaks its schema.</summary>
public sealed class ContractFileException : StoreException
{
    public ContractFileException(string message, bool corrupt) : base(message) => IsCorrupt = corrupt;
    public bool IsCorrupt { get; }
}

/// <summary>The store's own files disagree with each other (hash mismatch, missing file). Never repaired silently.</summary>
public sealed class StoreCorruptException : StoreException
{
    public StoreCorruptException(string message) : base(message) { }
}

/// <summary>Optimistic concurrency failure on manifest.json.</summary>
public sealed class ManifestConflictException : StoreException
{
    public ManifestConflictException(string message) : base(message) { }
}

/// <summary>Schema validation, hashing and safe file writes for contract documents.</summary>
public static class ContractFiles
{
    public const string CitySnapshotSchema = "city-snapshot.v1.schema.json";
    public const string ManifestSchema = "region-manifest.v1.schema.json";
    public const string IndexSchema = "store-index.v1.schema.json";
    public const string QuoteSchema = "quote-result.v1.schema.json";

    /// <summary>Lowercase hex SHA-256 of the exact bytes (Contracts/README.md: hashing is file-level).</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Parses, validates against the schema (the source of truth), then deserializes.</summary>
    public static T Parse<T>(byte[] bytes, string schemaFile, string label)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(bytes); }
        catch (JsonException ex) { throw new ContractFileException($"{label}: not valid JSON ({ex.Message})", corrupt: true); }

        var errors = SchemaErrors(node, schemaFile);
        if (errors.Count > 0)
            throw new ContractFileException($"{label} does not match {schemaFile}:\n" + string.Join("\n", errors.Take(10).Select(e => "  " + e)), corrupt: false);

        try { return ContractJson.FromBytes<T>(bytes); }
        catch (JsonException ex) { throw new ContractFileException($"{label}: {ex.Message}", corrupt: false); }
    }

    public static T ReadFile<T>(string path, string schemaFile)
    {
        if (!File.Exists(path)) throw new StoreCorruptException($"missing {path}");
        return Parse<T>(File.ReadAllBytes(path), schemaFile, path);
    }

    /// <summary>Serializes and validates. Nothing invalid is ever written.</summary>
    public static byte[] Serialize<T>(T value, string schemaFile, string label)
    {
        var bytes = ContractJson.ToBytes(value);
        var errors = SchemaErrors(JsonNode.Parse(bytes), schemaFile);
        if (errors.Count > 0)
            throw new ContractFileException($"refusing to write {label}: it does not match {schemaFile}:\n" + string.Join("\n", errors.Take(10).Select(e => "  " + e)), corrupt: false);
        return bytes;
    }

    public static IReadOnlyList<string> SchemaErrors(JsonNode? node, string schemaFile)
    {
        var result = Schemas.Value[schemaFile].Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return Array.Empty<string>();
        return result.Details.Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .ToList();
    }

    /// <summary>
    /// Replaces a file atomically: write a sibling temp file, then rename over the target.
    /// Single writer per store is assumed; a stale temp file from a crash is simply overwritten.
    /// </summary>
    public static void AtomicReplace(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Creates an immutable file. If it already exists with the same bytes (a crash after writing it but before
    /// updating the index), that is fine. If it exists with different bytes, the store is corrupt.
    /// </summary>
    public static void CreateImmutable(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new StoreCorruptException($"{path} already exists with different content. Immutable files are never overwritten.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: false);
    }

    // Contracts/schema/*.json is copied next to every assembly that references Contracts.
    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "schema");
        if (!Directory.Exists(dir)) throw new StoreException($"contract schemas not found in {dir}");
        var all = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.schema.json"))
        {
            var schema = JsonSchema.FromFile(file);
            SchemaRegistry.Global.Register(schema); // relative $refs to common.v1 resolve by $id
            all[Path.GetFileName(file)] = schema;
        }
        return all;
    });
}
