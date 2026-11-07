using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Regions.Contracts.Tests;

public class GoldenContractTests
{
    private static readonly string Basic = Repo.Golden("v1-basic");
    private static string[] SnapshotFiles() =>
        Directory.GetFiles(System.IO.Path.Combine(Basic, "snapshots"), "*.json", SearchOption.AllDirectories)
                 .Where(f => System.IO.Path.GetFileName(f) != "index.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    private static JsonNode Raw(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    // ---- 1. Golden files are valid against the schemas (the source of truth) ----

    [Fact]
    public void Golden_files_validate_against_schemas()
    {
        foreach (var f in SnapshotFiles()) Schemas.AssertValid("city-snapshot.v1.schema.json", Raw(f), f);
        Schemas.AssertValid("region-manifest.v1.schema.json", Raw(System.IO.Path.Combine(Basic, "manifest.json")), "manifest");
        Schemas.AssertValid("store-index.v1.schema.json", Raw(System.IO.Path.Combine(Basic, "snapshots", "index.json")), "index");
        Schemas.AssertValid("quote-result.v1.schema.json", Raw(System.IO.Path.Combine(Basic, "expected", "quote.city-b.Steel.json")), "quote");
        Schemas.AssertValid("capability-report.v1.schema.json", Raw(Repo.Golden("samples", "capability-report.prototype0.json")), "capability");
    }

    // ---- 2. C# classes mirror the schemas: deserialize -> serialize is lossless and still valid ----

    [Fact]
    public void Round_trip_is_lossless_and_schema_valid()
    {
        foreach (var f in SnapshotFiles()) RoundTrip<CitySnapshot>(f, "city-snapshot.v1.schema.json");
        RoundTrip<RegionManifest>(System.IO.Path.Combine(Basic, "manifest.json"), "region-manifest.v1.schema.json");
        RoundTrip<StoreIndex>(System.IO.Path.Combine(Basic, "snapshots", "index.json"), "store-index.v1.schema.json");
        RoundTrip<QuoteResult>(System.IO.Path.Combine(Basic, "expected", "quote.city-b.Steel.json"), "quote-result.v1.schema.json");
        RoundTrip<CapabilityReport>(Repo.Golden("samples", "capability-report.prototype0.json"), "capability-report.v1.schema.json");
    }

    private static void RoundTrip<T>(string path, string schema)
    {
        var original = Raw(path);
        var reserialized = ContractJson.ToNode(ContractJson.Read<T>(path));
        Schemas.AssertValid(schema, reserialized, $"re-serialized {path}");
        Assert.True(JsonNode.DeepEquals(original, reserialized), $"round-trip changed content of {path}:\n{reserialized.ToJsonString()}");
    }

    // ---- 3. Store index hashes are over exact file bytes ----

    [Fact]
    public void Index_hashes_match_exact_file_bytes()
    {
        var index = ContractJson.Read<StoreIndex>(System.IO.Path.Combine(Basic, "snapshots", "index.json"));
        Assert.Equal(SnapshotFiles().Length, index.Snapshots.Count);
        foreach (var s in index.Snapshots)
        {
            var file = System.IO.Path.Combine(Basic, "snapshots", s.CityId, $"{s.Seq}.json");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.True(hash == s.FileSha256,
                $"{file}: hash {hash} != index {s.FileSha256}. Line endings changed? (see .gitattributes)");
        }
        Assert.Equal(index.Snapshots.Max(s => s.RegionSeq), index.CurrentRegionSeq);
        Assert.Equal(index.Snapshots.Select(s => s.RegionSeq).Distinct().Count(), index.Snapshots.Count);
    }

    // ---- 4. null means unknown, never zero ----

    [Fact]
    public void Null_and_zero_are_distinct()
    {
        var b = ContractJson.Read<CitySnapshot>(System.IO.Path.Combine(Basic, "snapshots", "city-b", "1.json"));
        var steel = Assert.Single(b.Resources);
        Assert.Equal(0d, steel.RealizedOutputPerGameDay);   // measured zero
        Assert.Null(steel.CapacityEstimatePerGameDay);      // unknown
        Assert.Null(b.GameTime.GameDays);
        Assert.Contains("\"capacityEstimatePerGameDay\": null", JsonSerializer.Serialize(b, ContractJson.Options));
    }

    // ---- 5. Manifest references are consistent with snapshots ----

    [Fact]
    public void Manifest_links_and_gateways_reference_real_transport_endpoints()
    {
        var manifest = ContractJson.Read<RegionManifest>(System.IO.Path.Combine(Basic, "manifest.json"));
        var endpoints = SnapshotFiles().Select(ContractJson.Read<CitySnapshot>)
            .SelectMany(s => s.OutsideConnections.Select(oc => (s.CityId, oc)))
            .ToDictionary(x => (x.CityId, x.oc.EndpointKey), x => x.oc);

        Assert.Equal(manifest.Cities.Count, manifest.Cities.Select(c => c.CityId).Distinct().Count());
        foreach (var link in manifest.Links)
        {
            Assert.NotEqual(link.A.CityId, link.B.CityId);
            foreach (var end in new[] { link.A, link.B })
            {
                Assert.True(endpoints.TryGetValue((end.CityId, end.EndpointKey), out var oc), $"{link.LinkId}: unknown endpoint {end.CityId}/{end.EndpointKey}");
                Assert.Equal(ConnectionKind.Transport, oc!.Kind);
                Assert.Contains(link.Mode, oc.TransportModes);
            }
        }
        foreach (var gw in manifest.WorldGateways)
        {
            Assert.True(endpoints.TryGetValue((gw.CityId, gw.EndpointKey), out var oc), $"gateway {gw.CityId}/{gw.EndpointKey} unknown");
            Assert.Equal(ConnectionKind.Transport, oc!.Kind);
            Assert.Contains(gw.Mode, oc.TransportModes);
        }
    }

    // ---- 6. Documented sort orders hold, so identical state gives identical bytes ----

    [Fact]
    public void Documented_arrays_are_sorted()
    {
        foreach (var s in SnapshotFiles().Select(ContractJson.Read<CitySnapshot>))
        {
            AssertSorted(s.Resources.Select(r => r.ResourceId), $"{s.CityId}.resources");
            AssertSorted(s.OutsideConnections.Select(o => o.EndpointKey), $"{s.CityId}.outsideConnections");
        }
        var m = ContractJson.Read<RegionManifest>(System.IO.Path.Combine(Basic, "manifest.json"));
        AssertSorted(m.Cities.Select(c => c.CityId), "cities");
        AssertSorted(m.Links.Select(l => l.LinkId), "links");
        AssertSorted(m.WorldGateways.Select(g => g.CityId + "\u0000" + g.EndpointKey), "worldGateways");
        AssertSorted(m.Parameters.ResourcePrices.Select(p => p.ResourceId), "resourcePrices");
        Assert.Equal(m.Parameters.ModeCosts.Select(c => c.Mode).OrderBy(x => x), m.Parameters.ModeCosts.Select(c => c.Mode));
    }

    private static void AssertSorted(IEnumerable<string> keys, string label)
    {
        var list = keys.ToList();
        Assert.True(list.SequenceEqual(list.OrderBy(k => k, StringComparer.Ordinal)), $"{label} not ordinal-sorted");
        Assert.True(list.Distinct().Count() == list.Count, $"{label} has duplicates");
    }

    // ---- 7. Expected quote obeys the contract's arithmetic (README "Quote semantics") ----

    [Fact]
    public void Expected_quote_is_internally_consistent()
    {
        var q = ContractJson.Read<QuoteResult>(System.IO.Path.Combine(Basic, "expected", "quote.city-b.Steel.json"));
        var m = ContractJson.Read<RegionManifest>(System.IO.Path.Combine(Basic, "manifest.json"));
        Assert.Equal(ReportedAs.ModeledPriceQuoteNotPhysicalTrade, q.ReportedAs);
        Assert.False(q.AppliedToGame);

        foreach (var o in q.Offers.Where(o => o.Eligible))
            Assert.Equal(o.LandedPrice!.Value, o.ProducerPrice!.Value + o.Freight!.Value + o.Tariff!.Value - o.Subsidy!.Value, 9);

        // Freight recomputed from manifest parameters for city offers.
        var cost = m.Parameters.ModeCosts.ToDictionary(c => c.Mode);
        var links = m.Links.ToDictionary(l => l.LinkId);
        foreach (var o in q.Offers.Where(o => o.Eligible && o.OriginKind == OriginKind.City))
        {
            var freight = o.Path.Select(id => links[id]).Sum(l => cost[l.Mode].FixedCost + cost[l.Mode].CostPerKm * l.DistanceKm);
            Assert.Equal(freight, o.Freight!.Value, 9);
        }

        var eligible = q.Offers.Select((o, i) => (o, i)).Where(x => x.o.Eligible).ToList();
        var best = eligible.OrderBy(x => x.o.LandedPrice).ThenBy(x => x.i).First().i;
        Assert.Equal(best, q.SelectedOfferIndex);

        // G01-style: the large isolated supplier is excluded and has no price.
        var c = q.Offers.Single(o => o.OriginCityId == "city-c");
        Assert.False(c.Eligible);
        Assert.Equal(ExcludedReason.NoPath, c.ExcludedReason);
        Assert.Null(c.LandedPrice);
    }

    // ---- 8. C# enums and schema enums cannot drift ----

    [Theory]
    [InlineData(typeof(TransportMode), "transportMode")]
    [InlineData(typeof(ConnectionKind), "connectionKind")]
    [InlineData(typeof(Confidence), "confidence")]
    [InlineData(typeof(LinkStatus), "linkStatus")]
    [InlineData(typeof(MapProfile), "mapProfile")]
    [InlineData(typeof(EconomyProfile), "economyProfile")]
    [InlineData(typeof(MobilityProfile), "mobilityProfile")]
    [InlineData(typeof(OccupancyProfile), "occupancyProfile")]
    [InlineData(typeof(CompanyControlProfile), "companyControlProfile")]
    [InlineData(typeof(FactoryProductionProfile), "factoryProductionProfile")]
    [InlineData(typeof(OriginKind), "originKind")]
    [InlineData(typeof(ExcludedReason), "excludedReason")]
    [InlineData(typeof(ReportedAs), "reportedAs")]
    [InlineData(typeof(Feature), "feature")]
    [InlineData(typeof(FeatureStatus), "featureStatus")]
    public void Csharp_enum_matches_schema_enum(Type enumType, string defName)
    {
        var common = Raw(Repo.Path("Contracts", "schema", "common.v1.schema.json"));
        var schemaValues = common["$defs"]![defName]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());
        Assert.Equal(Enum.GetNames(enumType), schemaValues);
    }

    // ---- 8b. The optional policies block (added within v1 by A7) ----

    [Fact]
    public void Manifest_without_policies_is_still_valid_v1()
    {
        var doc = Raw(System.IO.Path.Combine(Basic, "manifest.json"));
        doc["parameters"]!.AsObject().Remove("policies");
        Schemas.AssertValid("region-manifest.v1.schema.json", doc, "manifest without policies");
        var m = JsonSerializer.Deserialize<RegionManifest>(doc.ToJsonString(), ContractJson.Options)!;
        Assert.Empty(m.Parameters.Policies.ImportTariffs);
        Assert.Empty(m.Parameters.Policies.FreightSubsidies);
    }

    [Theory]
    [InlineData("/parameters/policies/freightSubsidies", "[{\"policyId\":\"s\",\"resourceId\":\"Steel\",\"mode\":\"Rail\",\"linkIds\":null,\"percentOfFreight\":101,\"budgetPerGameDay\":0}]")]
    [InlineData("/parameters/policies/importTariffs", "[{\"policyId\":\"t\",\"resourceId\":\"Steel\",\"adValoremPercent\":-1,\"specificPerUnit\":0}]")]
    [InlineData("/parameters/policies/importTariffs", "[{\"policyId\":\"t\",\"resourceId\":\"Steel\",\"adValoremPercent\":5}]")]
    public void Manifest_schema_rejects_bad_policies(string pointer, string json)
    {
        var doc = Raw(System.IO.Path.Combine(Basic, "manifest.json"));
        Set(doc, pointer, JsonNode.Parse(json));
        Assert.NotEmpty(Schemas.Errors("region-manifest.v1.schema.json", doc));
    }

    // ---- 9. Schemas actually reject contract violations ----

    [Theory]
    [InlineData("unknown property", "/extra", "1")]
    [InlineData("integer enum", "/profiles/map", "0")]
    [InlineData("negative output", "/resources/0/realizedOutputPerGameDay", "-1")]
    [InlineData("seq zero", "/seq", "0")]
    [InlineData("utility with transport mode", "/outsideConnections/0/transportModes", "[\"Road\"]")]
    public void Snapshot_schema_rejects_violations(string label, string pointer, string jsonValue)
    {
        var doc = Raw(System.IO.Path.Combine(Basic, "snapshots", "city-a", "1.json"));
        Set(doc, pointer, JsonNode.Parse(jsonValue));
        Assert.True(Schemas.Errors("city-snapshot.v1.schema.json", doc).Count > 0, $"schema accepted: {label}");
    }

    [Fact]
    public void Snapshot_schema_rejects_omitted_null_field()
    {
        var doc = Raw(System.IO.Path.Combine(Basic, "snapshots", "city-b", "1.json"));
        doc["resources"]![0]!.AsObject().Remove("capacityEstimatePerGameDay");
        Assert.NotEmpty(Schemas.Errors("city-snapshot.v1.schema.json", doc));
    }

    [Fact]
    public void Deserializer_rejects_unknown_property_and_integer_enum()
    {
        var text = File.ReadAllText(System.IO.Path.Combine(Basic, "snapshots", "city-a", "1.json"));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<CitySnapshot>(text.Replace("\"seq\": 1,", "\"seq\": 1, \"bogus\": 1,"), ContractJson.Options));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<CitySnapshot>(text.Replace("\"map\": \"Vanilla\"", "\"map\": 0"), ContractJson.Options));
    }

    private static void Set(JsonNode root, string pointer, JsonNode? value)
    {
        var parts = pointer.Trim('/').Split('/');
        var parent = root;
        foreach (var p in parts[..^1]) parent = int.TryParse(p, out var i) ? parent[i]! : parent[p]!;
        var last = parts[^1];
        if (int.TryParse(last, out var idx)) parent[idx] = value; else parent[last] = value;
    }
}
