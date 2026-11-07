using System.Text.Json;
using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>A mutable copy of a region fixture. Tests change it, then quote.</summary>
public sealed class Fixture
{
    public RegionManifest Manifest { get; set; } = new();
    public List<(CitySnapshot Snapshot, int RegionSeq)> Snapshots { get; set; } = new();
    public int CurrentRegionSeq { get; set; }
    public List<ResourceDefinition> Resources { get; set; } = new();

    /// <summary>Contracts/golden/v1-basic, loaded fresh each call.</summary>
    public static Fixture GoldenBasic()
    {
        var dir = Repo.Golden("v1-basic");
        var index = ContractJson.Read<StoreIndex>(Path.Combine(dir, "snapshots", "index.json"));
        return new Fixture
        {
            Manifest = ContractJson.Read<RegionManifest>(Path.Combine(dir, "manifest.json")),
            Snapshots = index.Snapshots
                .Select(s => (ContractJson.Read<CitySnapshot>(Path.Combine(dir, "snapshots", s.CityId, $"{s.Seq}.json")), s.RegionSeq))
                .ToList(),
            CurrentRegionSeq = index.CurrentRegionSeq,
            Resources = LoadResourceTable(),
        };
    }

    public static List<ResourceDefinition> LoadResourceTable()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Repo.Path("Contracts", "resources", "resource-ids.v1.json")))!;
        return doc["resources"]!.AsArray()
            .Select(r => new ResourceDefinition(
                r!["resourceId"]!.GetValue<string>(),
                r["allowedFreightModes"]!.AsArray().Select(m => Enum.Parse<TransportMode>(m!.GetValue<string>()))))
            .ToList();
    }

    public RegionState State() => new(Manifest, Snapshots.Select(s => new PublishedSnapshot(s.Snapshot, s.RegionSeq)), CurrentRegionSeq, Resources);

    public QuoteResult Quote(string dest = "city-b", string resource = "Steel") =>
        new LandedCostEngine().Quote(State(), new QuoteRequest { DestCityId = dest, ResourceId = resource });

    public CitySnapshot City(string cityId) => Snapshots.Single(s => s.Snapshot.CityId == cityId).Snapshot;
    public ResourceObservation Steel(string cityId) => City(cityId).Resources.Single(r => r.ResourceId == "Steel");
    public LinkEntry Link(string linkId) => Manifest.Links.Single(l => l.LinkId == linkId);
    public ModeCost Cost(TransportMode mode) => Manifest.Parameters.ModeCosts.Single(c => c.Mode == mode);
    public void RemoveLink(string linkId) => Manifest.Links.RemoveAll(l => l.LinkId == linkId);
    public void SetSteelModes(params TransportMode[] modes) =>
        Resources = Resources.Select(r => r.ResourceId == "Steel" ? new ResourceDefinition("Steel", modes) : r).ToList();
}

public static class Q
{
    public static Offer From(this QuoteResult q, string cityId) => q.Offers.Single(o => o.OriginCityId == cityId);
    public static Offer World(this QuoteResult q) => q.Offers.Single(o => o.OriginKind == OriginKind.World);
    public static Offer? Selected(this QuoteResult q) => q.SelectedOfferIndex is int i ? q.Offers[i] : null;

    public static byte[] Bytes(QuoteResult q) => JsonSerializer.SerializeToUtf8Bytes(q, ContractJson.Options);

    /// <summary>Offers and selection only (no provenance), for comparing quotes whose inputs legitimately differ.</summary>
    public static string Decision(QuoteResult q) =>
        JsonSerializer.Serialize(new { q.Offers, q.SelectedOfferIndex }, ContractJson.Options);

    public static void AssertExcluded(Offer o, ExcludedReason reason)
    {
        Assert.False(o.Eligible);
        Assert.Equal(reason, o.ExcludedReason);
        Assert.Empty(o.Path);
        Assert.Null(o.ProducerPrice);
        Assert.Null(o.Freight);
        Assert.Null(o.LandedPrice);
    }

    public static void AssertEligible(Offer o, double landed, params string[] path)
    {
        Assert.True(o.Eligible, $"expected eligible, got {o.ExcludedReason}");
        Assert.Null(o.ExcludedReason);
        Assert.Equal(landed, o.LandedPrice!.Value, 9);
        if (o.OriginKind == OriginKind.City) Assert.Equal(path, o.Path);
    }
}

/// <summary>Deep JSON equality with the 1e-9 number tolerance from Contracts/README.md rule 7.</summary>
public static class JsonTolerance
{
    public static void AssertEquivalent(JsonNode? expected, JsonNode? actual, string at = "$")
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"{at}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
            return;
        }

        switch (expected)
        {
            case JsonObject eo:
                var ao = Assert.IsType<JsonObject>(actual);
                Assert.True(eo.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal)
                              .SequenceEqual(ao.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal)),
                    $"{at}: property sets differ");
                foreach (var (key, value) in eo) AssertEquivalent(value, ao[key], $"{at}.{key}");
                break;
            case JsonArray ea:
                var aa = Assert.IsType<JsonArray>(actual);
                Assert.True(ea.Count == aa.Count, $"{at}: expected {ea.Count} items, got {aa.Count}");
                for (var i = 0; i < ea.Count; i++) AssertEquivalent(ea[i], aa[i], $"{at}[{i}]");
                break;
            default:
                if (expected.GetValueKind() == JsonValueKind.Number && actual.GetValueKind() == JsonValueKind.Number)
                {
                    var e = expected.GetValue<double>();
                    var a = actual.GetValue<double>();
                    Assert.True(Math.Abs(e - a) <= 1e-9, $"{at}: expected {e}, got {a}");
                }
                else
                {
                    Assert.True(expected.ToJsonString() == actual.ToJsonString(), $"{at}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
                }
                break;
        }
    }
}
