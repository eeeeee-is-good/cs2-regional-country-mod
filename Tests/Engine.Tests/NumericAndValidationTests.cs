using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>G10 (zero/huge distance, missing mode, cycles) and fail-loud input validation (log §18.4 #7).</summary>
public class NumericAndValidationTests
{
    [Fact]
    public void G10_zero_distance_charges_only_fixed_cost()
    {
        var f = Fixture.GoldenBasic();
        f.Link("ab-rail-01").DistanceKm = 0;
        f.Link("ab-road-01").DistanceKm = 0;
        var a = f.Quote().From("city-a");
        Q.AssertEligible(a, 102, "ab-road-01");
        Assert.Equal(2, a.Freight);
    }

    [Fact]
    public void G10_huge_distance_stays_finite()
    {
        var f = Fixture.GoldenBasic();
        f.Link("ab-rail-01").DistanceKm = 1e300;
        f.Link("ab-road-01").DistanceKm = 1e300;
        var q = f.Quote();
        var a = q.From("city-a");
        Assert.True(a.Eligible);
        Assert.True(double.IsFinite(a.LandedPrice!.Value));
        Assert.Equal(new[] { "ab-rail-01" }, a.Path); // 0.04/km beats 0.10/km
        Assert.Same(q.World(), q.Selected());
    }

    [Fact]
    public void G10_overflowing_freight_fails_loudly_instead_of_clamping()
    {
        var f = Fixture.GoldenBasic();
        f.Cost(TransportMode.Road).CostPerKm = 1e10;
        f.Link("ab-road-01").DistanceKm = 1e300;
        var ex = Assert.Throws<RegionStateException>(() => f.Quote());
        Assert.Contains("ab-road-01", ex.Message);
    }

    [Fact]
    public void G10_cycles_terminate_and_pick_the_cheapest_simple_path()
    {
        var f = Fixture.GoldenBasic();
        ScenarioTests.AddCityD(f);
        // a <-> d <-> b plus the existing a <-> b links: several cycles.
        f.Manifest.Links.Add(ScenarioTests.RoadLink("ad-road-01", "city-a", "oc-road-east-01", "city-d", "oc-road-1", 150));
        f.Manifest.Links.Add(ScenarioTests.RoadLink("db-road-01", "city-d", "oc-road-2", "city-b", "oc-road-west-01", 150));
        var a = f.Quote().From("city-a");
        Q.AssertEligible(a, 134, "ab-rail-01"); // via d: 100 + 17 + 17 = 134 too; ordinal tie-break keeps ab-rail-01

        f.Link("ad-road-01").DistanceKm = 100; // via d: 100 + 12 + 17 = 129
        Q.AssertEligible(f.Quote().From("city-a"), 129, "ad-road-01", "db-road-01");
    }

    [Fact]
    public void G10_zero_cost_cycle_terminates()
    {
        var f = Fixture.GoldenBasic();
        ScenarioTests.AddCityD(f);
        foreach (var c in f.Manifest.Parameters.ModeCosts) { c.FixedCost = 0; c.CostPerKm = 0; }
        f.Manifest.Links.Add(ScenarioTests.RoadLink("ad-road-01", "city-a", "oc-road-east-01", "city-d", "oc-road-1", 0));
        f.Manifest.Links.Add(ScenarioTests.RoadLink("db-road-01", "city-d", "oc-road-2", "city-b", "oc-road-west-01", 0));
        var a = f.Quote().From("city-a");
        Q.AssertEligible(a, 100, "ab-rail-01"); // every path costs 0; ordinal tie-break
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_link_distance_is_rejected(double km)
    {
        var f = Fixture.GoldenBasic();
        f.Link("ab-road-01").DistanceKm = km;
        var ex = Assert.Throws<RegionStateException>(() => f.Quote());
        Assert.Contains("links[ab-road-01]", ex.Message);
    }

    [Fact]
    public void Invalid_numbers_are_rejected_everywhere()
    {
        var f = Fixture.GoldenBasic();
        f.Cost(TransportMode.Rail).FixedCost = -1;
        f.Manifest.Parameters.ResourcePrices[0].WorldProducerPriceIndex = double.NaN;
        f.Manifest.WorldGateways[0].DistanceKm = -5;
        f.Steel("city-a").CapacityEstimatePerGameDay = -1;
        var ex = Assert.Throws<RegionStateException>(() => f.Quote());
        Assert.Equal(4, ex.Problems.Count);
    }

    [Fact]
    public void Two_snapshots_for_one_city_are_rejected()
    {
        var f = Fixture.GoldenBasic();
        f.Snapshots.Add((f.City("city-a"), 2));
        Assert.Contains(Assert.Throws<RegionStateException>(() => f.Quote()).Problems, p => p.Contains("more than one latest snapshot"));
    }

    [Fact]
    public void Snapshot_from_a_city_outside_the_manifest_is_rejected()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Cities.RemoveAll(c => c.CityId == "city-c");
        Assert.Contains(Assert.Throws<RegionStateException>(() => f.Quote()).Problems, p => p.Contains("not in the manifest"));
    }

    [Fact]
    public void Region_seq_beyond_current_is_rejected()
    {
        var f = Fixture.GoldenBasic();
        f.CurrentRegionSeq = 2; // city-c has regionSeq 3
        Assert.Throws<RegionStateException>(() => f.Quote());
    }

    [Fact]
    public void Unknown_destination_is_rejected()
    {
        Assert.Throws<RegionStateException>(() => Fixture.GoldenBasic().Quote(dest: "city-z"));
    }
}
