using System.Text.Json;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>
/// Phase 1 applied signals on the golden region (no scarcity): Carford (city-b) buys from Ironvale by rail at 134;
/// the world offer is 140. Default step: 2% of the producer price (100) = 2 per period.
/// </summary>
public class SignalTests
{
    private static readonly LandedCostEngine Engine = new();
    private static readonly SignalRule Rule = new();

    private static CitySignal Of(SignalState s, string city) => s.Signals.Single(x => x.CityId == city && x.ResourceId == "Steel");

    private static SignalState Step(Fixture f, SignalState? prev, params string[] inactive)
    {
        var active = inactive.Length == 0 ? null : new HashSet<string>(f.Manifest.Cities.Select(c => c.CityId).Except(inactive));
        return SignalPlanner.Step(prev, f.State(), Engine, Rule, active);
    }

    /// <summary>Push both A-B links far enough that the world (140) becomes the cheapest offer for Carford.</summary>
    private static void CutCheapSupply(Fixture f)
    {
        f.Link("ab-rail-01").DistanceKm = 5000;
        f.Link("ab-road-01").DistanceKm = 5000;
    }

    [Fact]
    public void First_period_starts_at_the_target()
    {
        var s = Step(Fixture.GoldenBasic(), null);
        Assert.Equal(1, s.Period);
        var b = Of(s, "city-b");
        Assert.Equal((134.0, 134.0, SignalStatus.New), (b.Buy.Target!.Value, b.Buy.Applied!.Value, b.Buy.Status));
        Assert.Null(b.Sell.Target);                                       // Carford does not sell steel
        var a = Of(s, "city-a");
        Assert.Equal(SignalStatus.NoTarget, a.Buy.Status);                // nothing reaches Ironvale
        Assert.Equal(100, a.Sell.Applied);                                // a producer sells at the base price (no scarcity rule)
    }

    [Fact]
    public void A_shock_is_followed_two_per_period_and_settles_exactly()
    {
        var f = Fixture.GoldenBasic();
        var s = Step(f, null);
        CutCheapSupply(f);                                               // target jumps 134 -> 140
        var applied = new List<double>();
        for (var i = 0; i < 5; i++) { s = Step(f, s); applied.Add(Of(s, "city-b").Buy.Applied!.Value); }
        Assert.Equal(new[] { 136.0, 138, 140, 140, 140 }, applied);
        Assert.Equal(SignalStatus.Settled, Of(s, "city-b").Buy.Status);

        f = Fixture.GoldenBasic();                                       // shock removed: back down the same way
        applied.Clear();
        for (var i = 0; i < 4; i++) { s = Step(f, s); applied.Add(Of(s, "city-b").Buy.Applied!.Value); }
        Assert.Equal(new[] { 138.0, 136, 134, 134 }, applied);
    }

    [Fact]
    public void Step_size_follows_the_rule()
    {
        var f = Fixture.GoldenBasic();
        var s = SignalPlanner.Step(null, f.State(), Engine, Rule);
        CutCheapSupply(f);
        s = SignalPlanner.Step(s, f.State(), Engine, new SignalRule { MaxStepFraction = 0.05 });
        Assert.Equal(139, Of(s, "city-b").Buy.Applied);
        s = SignalPlanner.Step(s, f.State(), Engine, new SignalRule { MaxStepFraction = 0 });
        Assert.Equal(139, Of(s, "city-b").Buy.Applied);                  // a zero step never moves
        Assert.Throws<ArgumentException>(() => SignalPlanner.Step(s, f.State(), Engine, new SignalRule { MaxStepFraction = -1 }));
    }

    [Fact]
    public void Unloaded_city_is_frozen_then_converges_without_backlog()
    {
        var f = Fixture.GoldenBasic();
        var s = Step(f, null);
        CutCheapSupply(f);
        for (var i = 0; i < 10; i++) s = Step(f, s, "city-b");          // Carford unloaded for 10 periods
        var b = Of(s, "city-b");
        Assert.Equal((140.0, 134.0, SignalStatus.Frozen), (b.Buy.Target!.Value, b.Buy.Applied!.Value, b.Buy.Status));

        s = Step(f, s);                                                  // loaded again: one bounded step, not a jump
        Assert.Equal(136, Of(s, "city-b").Buy.Applied);
        Assert.Equal(12, s.Period);
    }

    [Fact]
    public void No_target_keeps_the_last_applied_price()
    {
        var f = Fixture.GoldenBasic();
        var s = Step(f, null);
        f.Manifest.Links.Clear();
        f.Manifest.WorldGateways.Clear();                                 // nothing reaches Carford any more
        s = Step(f, s);
        var b = Of(s, "city-b");
        Assert.Equal((SignalStatus.NoTarget, 134.0), (b.Buy.Status, b.Buy.Applied!.Value));
        Assert.Null(b.Buy.Target);
    }

    [Fact]
    public void Reference_price_is_taken_when_a_city_publishes()
    {
        var f = Fixture.GoldenBasic();
        var s = Step(f, null);
        Assert.Equal((2, 134.0), (Of(s, "city-b").ReferenceRegionSeq!.Value, Of(s, "city-b").ReferenceBuyPrice!.Value));
        CutCheapSupply(f);
        s = Step(f, s);                                                  // applied 136, same snapshot: reference unchanged
        Assert.Equal(134, Of(s, "city-b").ReferenceBuyPrice);

        f.CurrentRegionSeq = 4;                                          // Carford republishes
        f.Snapshots = f.Snapshots.Select(x => (x.Snapshot, x.Snapshot.CityId == "city-b" ? 4 : x.RegionSeq)).ToList();
        s = Step(f, s);
        Assert.Equal((4, 138.0), (Of(s, "city-b").ReferenceRegionSeq!.Value, Of(s, "city-b").ReferenceBuyPrice!.Value));
    }

    [Fact]
    public void Signals_are_deterministic_and_do_not_change_quotes()
    {
        var f = Fixture.GoldenBasic();
        var quoteBefore = Q.Bytes(f.Quote());
        string Run()
        {
            SignalState? s = null;
            for (var i = 0; i < 3; i++) s = Step(f, s, i == 1 ? new[] { "city-b" } : Array.Empty<string>());
            return JsonSerializer.Serialize(s, ContractJson.Options);
        }
        Assert.Equal(Run(), Run());
        Assert.Equal(quoteBefore, Q.Bytes(f.Quote()));                   // signals are a separate layer
    }

    [Fact]
    public void Scarcity_gate_price_is_the_sell_target()
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1 };
        var a = Of(Step(f, null), "city-a");
        Assert.Equal(106, a.Sell.Target!.Value, 9);                      // overloaded: priced up to the world ceiling
    }
}
