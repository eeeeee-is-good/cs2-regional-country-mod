using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>PROTOTYPE_PLAN acceptance criterion 2: identical inputs give a byte-identical QuoteResult and inputsSha256.</summary>
public class ReplayTests
{
    [Fact]
    public void Replay_is_byte_identical()
    {
        var first = Q.Bytes(Fixture.GoldenBasic().Quote());
        for (var i = 0; i < 5; i++) Assert.Equal(first, Q.Bytes(Fixture.GoldenBasic().Quote()));
    }

    [Fact]
    public void Input_order_does_not_change_a_single_byte()
    {
        var baseline = Q.Bytes(Fixture.GoldenBasic().Quote());

        var f = Fixture.GoldenBasic();
        f.Manifest.Cities.Reverse();
        f.Manifest.Links.Reverse();
        f.Manifest.WorldGateways.Reverse();
        f.Manifest.Parameters.ModeCosts.Reverse();
        f.Manifest.Parameters.ResourcePrices.Reverse();
        f.Snapshots.Reverse();
        foreach (var (s, _) in f.Snapshots) { s.Resources.Reverse(); s.OutsideConnections.Reverse(); }
        f.Resources.Reverse();

        Assert.Equal(baseline, Q.Bytes(f.Quote()));
    }

    [Fact]
    public void Wall_clock_field_is_ignored()
    {
        var baseline = Q.Bytes(Fixture.GoldenBasic().Quote());
        var f = Fixture.GoldenBasic();
        f.City("city-a").ExportedAtUtc = "2030-01-01T00:00:00Z";
        Assert.Equal(baseline, Q.Bytes(f.Quote()));
    }

    [Fact]
    public void Any_input_change_changes_the_inputs_hash()
    {
        var baseline = Fixture.GoldenBasic().Quote().Provenance.InputsSha256;
        var mutations = new List<Action<Fixture>>
        {
            f => f.Link("ab-road-01").DistanceKm = 400.0000001,
            f => f.Steel("city-c").Source = "changed",
            f => f.Manifest.Revision = 2,
            f => f.CurrentRegionSeq = 4,
            f => f.City("city-b").OutsideConnections[0].CarriesPassengers = true,
            f => f.SetSteelModes(TransportMode.Road),
        };
        foreach (var mutate in mutations)
        {
            var f = Fixture.GoldenBasic();
            mutate(f);
            Assert.NotEqual(baseline, f.Quote().Provenance.InputsSha256);
        }
    }

    [Fact]
    public void Quoting_does_not_mutate_its_inputs()
    {
        var f = Fixture.GoldenBasic();
        var state = f.State();
        var before = InputHasher.Hash(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" });
        new LandedCostEngine().Quote(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" });
        Assert.Equal(before, InputHasher.Hash(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" }));
    }
}
