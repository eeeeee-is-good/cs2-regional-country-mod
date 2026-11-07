using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

/// <summary>Expected outcome of every built-in scenario (log §18.6), plus G06.</summary>
public class ScenarioCatalogTests
{
    private static QuoteResult Quote(RegionScenario s) =>
        new LandedCostEngine().Quote(s.ToState(), new QuoteRequest { DestCityId = Scenarios.Destination, ResourceId = Scenarios.Resource });

    private static Offer From(QuoteResult q, string city) => q.Offers.Single(o => o.OriginCityId == city);
    private static Offer World(QuoteResult q) => q.Offers.Single(o => o.OriginKind == OriginKind.World);

    public static IEnumerable<object[]> Names() => Scenarios.All.Select(e => new object[] { e.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_scenario_passes_the_invariant_checker(string name)
    {
        var report = new LandedCostEngine().Check(Scenarios.Build(name).ToState());
        Assert.True(report.Passed, string.Join("\n", report.Violations));
        Assert.True(report.QuotesChecked > 0);
    }

    [Fact]
    public void G01_no_link_no_benefit()
    {
        var q = Quote(Scenarios.Build("G01"));
        Assert.Equal(ExcludedReason.NoPath, From(q, "ironvale").ExcludedReason);
        Assert.Equal(140, World(q).LandedPrice);
        Assert.Same(World(q), q.Offers[q.SelectedOfferIndex!.Value]);
    }

    [Fact]
    public void G02_road_link_gives_eligible_offer()
    {
        var q = Quote(Scenarios.Build("G02"));
        var a = From(q, "ironvale");
        Assert.True(a.Eligible);
        Assert.Equal(107, a.LandedPrice!.Value, 9);
        Assert.Same(a, q.Offers[q.SelectedOfferIndex!.Value]);
    }

    [Fact]
    public void G03_rail_beats_road_and_world_and_matches_the_golden_fixture()
    {
        var q = Quote(Scenarios.Build("G03"));
        var a = From(q, "ironvale");
        Assert.Equal(new[] { "ic-rail-01" }, a.Path);
        Assert.Equal(134, a.LandedPrice!.Value, 9);
        Assert.Equal(140, World(q).LandedPrice!.Value, 9);
        Assert.Same(a, q.Offers[q.SelectedOfferIndex!.Value]);

        var noRail = Scenarios.Build("G03");
        noRail.Manifest.Links.RemoveAll(l => l.LinkId == "ic-rail-01");
        Assert.Equal(142, From(Quote(noRail), "ironvale").LandedPrice!.Value, 9);
    }

    [Fact]
    public void G10_edge_cases_stay_finite_and_respect_direction()
    {
        var q = Quote(Scenarios.Build("G10"));
        var a = From(q, "ironvale");
        Assert.Equal(new[] { "e01-zero", "e02-zero" }, a.Path); // the one-way rail e03 points into ironvale
        Assert.Equal(104, a.LandedPrice!.Value, 9);              // two zero-length road links: 2 + 2
        Assert.Equal(130, World(q).LandedPrice!.Value, 9);       // zero-distance gateway: fixed cost only

        var far = From(q, "farreach");
        Assert.True(far.Eligible);
        Assert.Equal(new[] { "e05-huge", "e02-zero" }, far.Path); // the Air link has no mode cost, so it is unusable
        Assert.True(double.IsFinite(far.LandedPrice!.Value));
        Assert.Same(a, q.Offers[q.SelectedOfferIndex!.Value]);
    }

    [Fact]
    public void Corridor_transit_through_an_unloaded_city()
    {
        var q = Quote(Scenarios.Build("corridor"));
        var a = From(q, "ironvale");
        Assert.Equal(new[] { "im-rail-01", "mc-road-01" }, a.Path);
        Assert.Equal(138, a.LandedPrice!.Value, 9); // 100 + (18 + 0.04 x 300) + (2 + 0.1 x 60)
        Assert.Equal(ExcludedReason.NoPath, From(q, "lonepeak").ExcludedReason);
        Assert.Same(a, q.Offers[q.SelectedOfferIndex!.Value]);
    }

    [Fact]
    public void Scenarios_are_deterministic()
    {
        foreach (var e in Scenarios.All)
            Assert.Equal(Quote(e.Build()).Provenance.InputsSha256, Quote(e.Build()).Provenance.InputsSha256);
    }

    /// <summary>
    /// G06 at the quote layer: republishing an unchanged city 10 times leaves its market contribution unchanged.
    /// (Rejecting an identical (cityId, seq, hash) as a no-op is RegionStore's job, tested with A2.)
    /// </summary>
    [Fact]
    public void G06_republishing_an_unchanged_snapshot_changes_nothing()
    {
        var region = Scenarios.Build("G03");
        var ironvale = Scenarios.Ironvale();
        ironvale.Capture(); // seq 1 was already published by the scenario
        var baseline = Decision(Quote(region));

        for (var i = 0; i < 10; i++)
        {
            region.Publish(ironvale.Capture());
            var q = Quote(region);
            Assert.Equal(baseline, Decision(q));
            Assert.Equal(2 + i, From(q, "ironvale").SupplierSnapshot!.Seq);
        }
        Assert.Equal(2 + 10, region.CurrentRegionSeq); // two cities published by the scenario, then 10 republishes
    }

    /// <summary>Offers with the supplier snapshot reference removed, since its seq legitimately changes.</summary>
    private static string Decision(QuoteResult q) =>
        string.Join("|", q.Offers.Select(o => $"{o.OriginCityId}:{o.Eligible}:{o.ExcludedReason}:{string.Join(">", o.Path)}:{o.LandedPrice}:{o.EffectiveRegionalOfferPerGameDay}"))
        + $"|{q.SelectedOfferIndex}";
}
