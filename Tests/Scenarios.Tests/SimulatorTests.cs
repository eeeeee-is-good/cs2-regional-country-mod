using System.Text.Json;
using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Regions.Engine;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

public class SimulatorTests
{
    [Fact]
    public void Capture_publishes_increasing_seq_with_parent()
    {
        var city = Scenarios.Ironvale();
        var s1 = city.Capture();
        var s2 = city.Capture();
        var s3 = city.Capture();
        Assert.Equal((1, (int?)null), (s1.Seq, s1.ParentSeq));
        Assert.Equal((2, (int?)1), (s2.Seq, s2.ParentSeq));
        Assert.Equal((3, (int?)2), (s3.Seq, s3.ParentSeq));
        Assert.Equal(3, city.LastSeq);
    }

    [Fact]
    public void Captured_snapshots_are_schema_valid_and_carry_no_wall_clock()
    {
        foreach (var e in Scenarios.All)
            foreach (var pub in e.Build().LatestSnapshots)
            {
                Schemas.AssertValid("city-snapshot.v1.schema.json", ContractJson.ToNode(pub.Snapshot), $"{e.Name}/{pub.Snapshot.CityId}");
                Assert.Null(pub.Snapshot.ExportedAtUtc);
                Assert.Equal(SyntheticCity.ToolName, pub.Snapshot.Producer.Tool);
                Assert.Null(pub.Snapshot.Producer.GameBuild);
            }
    }

    [Fact]
    public void Lists_are_sorted_whatever_the_declaration_order()
    {
        var s = new SyntheticCity("x")
            .Gate("oc-z", TransportMode.Road).Gate("oc-a", TransportMode.Rail).Utility("oc-m", ConnectionKind.Water)
            .Resource("Wood", 1, 1, 1).Resource("Steel", 1, 1, 1)
            .Capture();
        Assert.Equal(new[] { "oc-a", "oc-m", "oc-z" }, s.OutsideConnections.Select(o => o.EndpointKey));
        Assert.Equal(new[] { "Steel", "Wood" }, s.Resources.Select(r => r.ResourceId));
    }

    [Fact]
    public void A_capture_is_not_changed_by_later_edits_to_the_city()
    {
        var city = Scenarios.Ironvale();
        var s1 = city.Capture();
        var before = JsonSerializer.Serialize(s1, ContractJson.Options);
        city.Resource("Steel", 1, 1, 1).Gate("oc-new", TransportMode.Ship).Advance(30);
        Assert.Equal(before, JsonSerializer.Serialize(s1, ContractJson.Options));
    }

    [Fact]
    public void Null_means_unknown_and_survives_capture()
    {
        var s = Scenarios.Carford().Capture();
        var steel = Assert.Single(s.Resources);
        Assert.Null(steel.CapacityEstimatePerGameDay);
        Assert.Equal(0d, steel.RealizedOutputPerGameDay); // measured zero, not unknown
        Assert.Contains("\"capacityEstimatePerGameDay\": null", JsonSerializer.Serialize(s, ContractJson.Options));
    }

    [Fact]
    public void Clock_only_moves_forward_and_nothing_accumulates()
    {
        var city = Scenarios.Ironvale();
        var s1 = city.Capture();
        city.Advance(30);
        var s2 = city.Capture();
        Assert.Equal(30, s2.GameTime.GameDays);
        // Log §16.6 / N5: no production between captures.
        Assert.Equal(s1.Resources[0].RealizedOutputPerGameDay, s2.Resources[0].RealizedOutputPerGameDay);
        Assert.Throws<ArgumentOutOfRangeException>(() => { city.Advance(-1); });

        var unknownClock = new SyntheticCity("u") { GameDays = null };
        unknownClock.Advance(5);
        Assert.Null(unknownClock.Capture().GameTime.GameDays);
    }

    [Fact]
    public void Identical_cities_produce_identical_bytes()
    {
        Assert.Equal(JsonSerializer.Serialize(Scenarios.Ironvale().Capture(), ContractJson.Options),
                     JsonSerializer.Serialize(Scenarios.Ironvale().Capture(), ContractJson.Options));
    }

    [Fact]
    public void Default_resource_table_matches_contracts_file()
    {
        var file = JsonNode.Parse(File.ReadAllText(Repo.Path("Contracts", "resources", "resource-ids.v1.json")))!["resources"]!.AsArray()
            .Select(r => (r!["resourceId"]!.GetValue<string>(),
                          string.Join(",", r["allowedFreightModes"]!.AsArray().Select(m => m!.GetValue<string>()).OrderBy(Enum.Parse<TransportMode>))));
        var code = RegionScenario.DefaultResources().Select(r => (r.ResourceId, string.Join(",", r.AllowedFreightModes)));
        Assert.Equal(file, code);
    }

    [Fact]
    public void Publish_assigns_increasing_region_seq()
    {
        var region = new RegionScenario("t", "t").City("ironvale", "I").City("carford", "C");
        Assert.Equal(1, region.Publish(Scenarios.Ironvale().Capture()));
        Assert.Equal(2, region.Publish(Scenarios.Carford().Capture()));
        Assert.Equal(2, region.CurrentRegionSeq);
        Assert.Equal(new[] { "carford", "ironvale" }, region.LatestSnapshots.Select(p => p.Snapshot.CityId));
    }

    [Fact]
    public void Sorted_manifest_is_schema_valid()
    {
        foreach (var e in Scenarios.All)
            Schemas.AssertValid("region-manifest.v1.schema.json", ContractJson.ToNode(e.Build().SortManifest().Manifest), e.Name);
    }
}
