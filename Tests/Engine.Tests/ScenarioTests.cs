using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>Golden scenarios G01-G03 (log §18.6), route/mode rules (§16, §18.4 #1-3), staleness, supply and precedence.</summary>
public class ScenarioTests
{
    // ---- G01: no link means no benefit ----

    [Fact]
    public void G01_no_link_means_NoPath_and_world_fallback()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        var q = f.Quote();
        Q.AssertExcluded(q.From("city-a"), ExcludedReason.NoPath);
        Assert.Same(q.World(), q.Selected());
        Assert.Equal(140, q.Selected()!.LandedPrice);
    }

    [Fact]
    public void G01_without_world_gateway_nothing_is_selected()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        f.Manifest.WorldGateways.Clear();
        var q = f.Quote();
        Assert.All(q.Offers, o => Assert.False(o.Eligible));
        Assert.DoesNotContain(q.Offers, o => o.OriginKind == OriginKind.World);
        Assert.Null(q.SelectedOfferIndex);
    }

    [Fact]
    public void Invariant1_raising_unreachable_supply_does_not_change_quote()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        var before = Q.Decision(f.Quote());
        f.Steel("city-a").RealizedOutputPerGameDay = 1e6;
        f.Steel("city-a").CapacityEstimatePerGameDay = 1e6;
        Assert.Equal(before, Q.Decision(f.Quote()));
    }

    // ---- G02: a road link means an eligible offer ----

    [Fact]
    public void G02_road_link_gives_eligible_offer_but_world_wins_at_400km()
    {
        var f = Fixture.GoldenBasic();
        f.RemoveLink("ab-rail-01");
        var q = f.Quote();
        Q.AssertEligible(q.From("city-a"), 142, "ab-road-01");
        Assert.Same(q.World(), q.Selected()); // log §16.5: 142 > 140
    }

    [Fact]
    public void G02_nearby_road_supplier_wins()
    {
        var f = Fixture.GoldenBasic();
        f.RemoveLink("ab-rail-01");
        f.Link("ab-road-01").DistanceKm = 50;
        var q = f.Quote();
        Q.AssertEligible(q.From("city-a"), 107, "ab-road-01"); // log §16.5: 100 + 2 + 0.10 x 50
        Assert.Same(q.From("city-a"), q.Selected());
    }

    // ---- G03: road vs rail ----

    [Fact]
    public void G03_rail_wins_long_haul_road_wins_short_haul()
    {
        var f = Fixture.GoldenBasic();
        Q.AssertEligible(f.Quote().From("city-a"), 134, "ab-rail-01");

        f.Link("ab-rail-01").DistanceKm = 50;
        f.Link("ab-road-01").DistanceKm = 50;
        Q.AssertEligible(f.Quote().From("city-a"), 107, "ab-road-01"); // road 7 < rail 20
    }

    [Fact]
    public void G03_landed_price_follows_declared_formula_and_never_falls_with_distance()
    {
        var previous = double.NegativeInfinity;
        foreach (var d in new[] { 0, 1, 10, 50, 100, 266, 267, 400, 1000, 5000 })
        {
            var f = Fixture.GoldenBasic();
            f.Link("ab-rail-01").DistanceKm = d;
            f.Link("ab-road-01").DistanceKm = d;
            var landed = f.Quote().From("city-a").LandedPrice!.Value;
            Assert.Equal(100 + Math.Min(2 + 0.1 * d, 18 + 0.04 * d), landed, 9);
            Assert.True(landed >= previous, $"landed price fell from {previous} to {landed} at {d} km");
            previous = landed;
        }
    }

    [Fact]
    public void Network_test_severing_rail_removes_benefit_and_reconnecting_restores_it()
    {
        var f = Fixture.GoldenBasic();
        var baseline = Q.Decision(f.Quote());
        var rail = f.Link("ab-rail-01");
        f.RemoveLink("ab-rail-01");
        Assert.Equal(142, f.Quote().From("city-a").LandedPrice);
        f.Manifest.Links.Add(rail);
        Assert.Equal(baseline, Q.Decision(f.Quote()));
    }

    // ---- Path correctness (§18.4 #2) ----

    [Theory]
    [InlineData(LinkStatus.Broken)]
    [InlineData(LinkStatus.Unresolved)]
    public void Broken_or_unresolved_links_are_not_usable(LinkStatus status)
    {
        var f = Fixture.GoldenBasic();
        f.Link("ab-rail-01").Status = status;
        Q.AssertEligible(f.Quote().From("city-a"), 142, "ab-road-01");
    }

    [Fact]
    public void Verified_links_are_usable()
    {
        var f = Fixture.GoldenBasic();
        f.Link("ab-rail-01").Status = LinkStatus.Verified;
        Q.AssertEligible(f.Quote().From("city-a"), 134, "ab-rail-01");
    }

    [Fact]
    public void Utility_connection_cannot_carry_freight()
    {
        var f = Fixture.GoldenBasic();
        f.RemoveLink("ab-road-01");
        f.Link("ab-rail-01").A.EndpointKey = "oc-power-north-01";
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.NoPath);
    }

    [Fact]
    public void Endpoint_that_does_not_list_the_mode_is_not_usable()
    {
        var f = Fixture.GoldenBasic();
        f.RemoveLink("ab-road-01");
        f.Link("ab-rail-01").B.EndpointKey = "oc-road-west-01"; // a road gate used for a rail link
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.NoPath);
    }

    [Fact]
    public void Endpoint_reporting_no_cargo_is_not_usable()
    {
        var f = Fixture.GoldenBasic();
        f.City("city-b").OutsideConnections.Single(o => o.EndpointKey == "oc-rail-west-01").CarriesCargo = false;
        Q.AssertEligible(f.Quote().From("city-a"), 142, "ab-road-01");
    }

    [Fact]
    public void One_way_link_only_carries_goods_in_its_direction()
    {
        var f = Fixture.GoldenBasic();
        f.RemoveLink("ab-road-01");
        var rail = f.Link("ab-rail-01");
        rail.Bidirectional = false; // a = city-a, b = city-b: A -> B only
        Q.AssertEligible(f.Quote().From("city-a"), 134, "ab-rail-01");

        (rail.A, rail.B) = (rail.B, rail.A); // now B -> A only
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.NoPath);
    }

    [Fact]
    public void Resource_mode_restriction_gives_ModeIncompatible()
    {
        var f = Fixture.GoldenBasic();
        f.SetSteelModes(TransportMode.Ship);
        var q = f.Quote();
        Q.AssertExcluded(q.From("city-a"), ExcludedReason.ModeIncompatible);
        Q.AssertExcluded(q.From("city-c"), ExcludedReason.NoPath); // no path in any mode stays NoPath
        Q.AssertExcluded(q.World(), ExcludedReason.ModeIncompatible);
        Assert.Null(q.SelectedOfferIndex);
    }

    [Fact]
    public void Resource_mode_restriction_picks_the_allowed_mode_even_if_dearer()
    {
        var f = Fixture.GoldenBasic();
        f.SetSteelModes(TransportMode.Road);
        Q.AssertEligible(f.Quote().From("city-a"), 142, "ab-road-01");
    }

    [Fact]
    public void Transit_through_an_intermediate_city_is_allowed()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        AddCityD(f);
        f.Manifest.Links.Add(RoadLink("ad-road-01", "city-a", "oc-road-east-01", "city-d", "oc-road-1", 10));
        f.Manifest.Links.Add(RoadLink("db-road-01", "city-d", "oc-road-2", "city-b", "oc-road-west-01", 10));
        var q = f.Quote();
        Q.AssertEligible(q.From("city-a"), 106, "ad-road-01", "db-road-01");
    }

    [Fact]
    public void Transit_needs_the_intermediate_city_snapshot()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        AddCityD(f);
        f.Manifest.Links.Add(RoadLink("ad-road-01", "city-a", "oc-road-east-01", "city-d", "oc-road-1", 10));
        f.Manifest.Links.Add(RoadLink("db-road-01", "city-d", "oc-road-2", "city-b", "oc-road-west-01", 10));
        f.Snapshots.RemoveAll(s => s.Snapshot.CityId == "city-d");
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.NoPath);
    }

    [Fact]
    public void Missing_mode_cost_makes_that_mode_unusable()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.ModeCosts.RemoveAll(c => c.Mode == TransportMode.Rail);
        Q.AssertEligible(f.Quote().From("city-a"), 142, "ab-road-01");

        f.Manifest.Parameters.ModeCosts.RemoveAll(c => c.Mode == TransportMode.Road);
        var q = f.Quote();
        Q.AssertExcluded(q.From("city-a"), ExcludedReason.NoPath);
        Q.AssertExcluded(q.World(), ExcludedReason.NoWorldGateway);
        Assert.Null(q.SelectedOfferIndex);
    }

    [Fact]
    public void World_gateway_must_resolve_to_a_destination_transport_endpoint()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.WorldGateways[0].EndpointKey = "oc-water-west-01"; // a utility connection
        Q.AssertExcluded(f.Quote().World(), ExcludedReason.NoWorldGateway);
    }

    // ---- Staleness ----

    [Fact]
    public void Stale_supplier_is_excluded_by_region_seq_age()
    {
        var f = Fixture.GoldenBasic(); // maxRegionSeqAge = 10, city-a regionSeq = 1
        f.CurrentRegionSeq = 11;          // age 10: not stale
        Assert.True(f.Quote().From("city-a").Eligible);

        f.CurrentRegionSeq = 12;          // age 11: stale
        var q = f.Quote();
        Q.AssertExcluded(q.From("city-a"), ExcludedReason.StaleSnapshot);
        Assert.Same(q.World(), q.Selected());
    }

    [Fact]
    public void Null_max_age_means_never_stale()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Staleness.MaxRegionSeqAge = null;
        f.CurrentRegionSeq = 1000;
        Assert.True(f.Quote().From("city-a").Eligible);
    }

    // ---- Supply (A4 rule, SupplyRule.cs) ----

    [Theory]
    [InlineData(Confidence.Unknown, 1000.0, 700.0, 1200.0, ExcludedReason.UnknownSupply, null)]
    [InlineData(Confidence.Validated, null, 700.0, 1200.0, ExcludedReason.UnknownSupply, null)]
    [InlineData(Confidence.Provisional, null, null, null, ExcludedReason.UnknownSupply, null)]
    [InlineData(Confidence.Validated, 700.0, 700.0, 1200.0, ExcludedReason.NoSupply, 0.0)]
    [InlineData(Confidence.Validated, 500.0, 700.0, 1200.0, ExcludedReason.NoSupply, -200.0)]
    [InlineData(Confidence.Validated, 1000.0, 700.0, 0.0, ExcludedReason.NoSupply, 0.0)]
    [InlineData(Confidence.Provisional, 0.0, 500.0, null, ExcludedReason.NoSupply, -125.0)] // deficit stays a deficit when downgraded
    [InlineData(Confidence.Validated, 0.0, null, null, ExcludedReason.NoSupply, 0.0)]      // measured zero output
    public void Supply_exclusions(Confidence confidence, double? output, double? inputUse, double? capacity,
                                  ExcludedReason reason, double? effective)
    {
        var f = Fixture.GoldenBasic();
        var steel = f.Steel("city-a");
        (steel.Confidence, steel.RealizedOutputPerGameDay, steel.InputUsePerGameDay, steel.CapacityEstimatePerGameDay) =
            (confidence, output, inputUse, capacity);
        var a = f.Quote().From("city-a");
        Q.AssertExcluded(a, reason);
        Assert.Equal(effective, a.EffectiveRegionalOfferPerGameDay);
    }

    /// <summary>Unverified data is downgraded, not excluded (log §25.3). Golden city-a: output 1000, use 700, capacity 1200.</summary>
    [Theory]
    [InlineData(Confidence.Validated, 1000.0, 700.0, 1200.0, 300.0)]   // min(300, 1200) x 1
    [InlineData(Confidence.Provisional, 1000.0, 700.0, 1200.0, 150.0)] // x0.5 provisional
    [InlineData(Confidence.Validated, 1000.0, null, 1200.0, 500.0)]    // base = min(output 1000, 1200) x0.5 use unknown
    [InlineData(Confidence.Validated, 1000.0, 700.0, null, 150.0)]     // base = surplus 300 x0.5 capacity unknown
    [InlineData(Confidence.Validated, 1000.0, null, null, 250.0)]      // base = output 1000 x0.5 x0.5
    [InlineData(Confidence.Provisional, 1000.0, null, null, 125.0)]    // x0.5 x0.5 x0.5
    [InlineData(Confidence.Validated, 1000.0, null, 400.0, 200.0)]     // capacity still caps when use is unknown
    public void Supply_downgrades_keep_the_supplier_eligible(Confidence confidence, double? output, double? inputUse, double? capacity, double effective)
    {
        var f = Fixture.GoldenBasic();
        var steel = f.Steel("city-a");
        (steel.Confidence, steel.RealizedOutputPerGameDay, steel.InputUsePerGameDay, steel.CapacityEstimatePerGameDay) =
            (confidence, output, inputUse, capacity);
        var a = f.Quote().From("city-a");
        Q.AssertEligible(a, 134, "ab-rail-01"); // supply never changes the price in v1
        Assert.Equal(effective, a.EffectiveRegionalOfferPerGameDay!.Value, 9);
    }

    [Fact]
    public void Supply_notes_explain_every_factor()
    {
        var notes = SupplyRule.Evaluate(new ResourceObservation
        {
            ResourceId = "Steel", RealizedOutputPerGameDay = 1000, Confidence = Confidence.Provisional,
        }).Notes;
        Assert.Equal(new[] { "provisional x0.5", "local use unknown x0.5", "capacity unknown x0.5" }, notes);
        Assert.Empty(SupplyRule.Evaluate(Fixture.GoldenBasic().Steel("city-a")).Notes);
    }

    [Fact]
    public void Capacity_caps_the_effective_offer()
    {
        var f = Fixture.GoldenBasic();
        f.Steel("city-a").CapacityEstimatePerGameDay = 100;
        var a = f.Quote().From("city-a");
        Assert.True(a.Eligible);
        Assert.Equal(100, a.EffectiveRegionalOfferPerGameDay);
    }

    // ---- Exclusion precedence: UnknownResource -> NoPath/ModeIncompatible -> Stale -> UnknownSupply -> NoSupply ----

    [Fact]
    public void Precedence_NoPath_beats_Stale_and_UnknownSupply()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Links.Clear();
        f.CurrentRegionSeq = 100;
        f.Steel("city-a").Confidence = Confidence.Unknown;
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.NoPath);
    }

    [Fact]
    public void Precedence_Stale_beats_UnknownSupply()
    {
        var f = Fixture.GoldenBasic();
        f.CurrentRegionSeq = 100;
        f.Steel("city-a").Confidence = Confidence.Unknown;
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.StaleSnapshot);
    }

    [Fact]
    public void Precedence_UnknownSupply_beats_NoSupply()
    {
        var f = Fixture.GoldenBasic();
        f.Steel("city-a").InputUsePerGameDay = 5000;      // would be a deficit (NoSupply)
        f.Steel("city-a").Confidence = Confidence.Unknown; // but the figures are untrusted
        Q.AssertExcluded(f.Quote().From("city-a"), ExcludedReason.UnknownSupply);
    }

    [Fact]
    public void Unknown_resource_excludes_every_offer()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.ResourcePrices.Clear();
        var q = f.Quote();
        Assert.All(q.Offers, o => Q.AssertExcluded(o, ExcludedReason.UnknownResource));
        Assert.Null(q.SelectedOfferIndex);

        f = Fixture.GoldenBasic();
        f.Resources.Clear();
        Assert.All(f.Quote().Offers, o => Q.AssertExcluded(o, ExcludedReason.UnknownResource));
    }

    [Fact]
    public void Cities_without_the_resource_or_a_snapshot_make_no_offer()
    {
        var f = Fixture.GoldenBasic();
        f.Snapshots.RemoveAll(s => s.Snapshot.CityId == "city-c");
        f.City("city-a").Resources.Clear();
        var q = f.Quote();
        Assert.Equal(new[] { OriginKind.World }, q.Offers.Select(o => o.OriginKind));
    }

    // ---- helpers ----

    internal static void AddCityD(Fixture f)
    {
        f.Manifest.Cities.Add(new CityEntry { CityId = "city-d", DisplayName = "Midway (transit)" });
        var d = new CitySnapshot
        {
            CityId = "city-d",
            Seq = 1,
            Producer = new SnapshotProducer { Tool = "CitySimulator", ToolVersion = "test" },
            OutsideConnections =
            {
                new OutsideConnectionInfo { EndpointKey = "oc-road-1", Kind = ConnectionKind.Transport, TransportModes = { TransportMode.Road }, CarriesCargo = true },
                new OutsideConnectionInfo { EndpointKey = "oc-road-2", Kind = ConnectionKind.Transport, TransportModes = { TransportMode.Road }, CarriesCargo = true },
            },
        };
        f.CurrentRegionSeq += 1;
        f.Snapshots.Add((d, f.CurrentRegionSeq));
    }

    internal static LinkEntry RoadLink(string id, string cityA, string keyA, string cityB, string keyB, double km) => new()
    {
        LinkId = id,
        A = new EndpointRef { CityId = cityA, EndpointKey = keyA },
        B = new EndpointRef { CityId = cityB, EndpointKey = keyB },
        Mode = TransportMode.Road,
        DistanceKm = km,
        Bidirectional = true,
        Status = LinkStatus.Proposed,
    };
}
