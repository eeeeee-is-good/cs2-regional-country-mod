using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

/// <summary>
/// Phase 2 controlled tests (docs/ECONOMIC_MODEL_PLAN.md §7). Synthetic cities react to applied prices; the engine,
/// signals and publishing are the real ones. Pass criteria are fixed before running.
/// </summary>
public class SimulationTests
{
    private static RegionSimulation Sim(string scenario, Action<RegionScenario>? setup = null)
    {
        var s = Scenarios.Build(scenario);
        s.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1.0 };
        s.Manifest.Parameters.Staleness.MaxRegionSeqAge = null; // republishing every period must not age anyone out
        setup?.Invoke(s);
        return RegionSimulation.From(s);
    }

    private static double[] BuyApplied(RegionSimulation sim, string city) =>
        sim.History.Select(r => r.Of(city).BuyApplied ?? double.NaN).ToArray();

    private static void AssertStepsAtMost(double[] series, double maxStep, int from = 0)
    {
        for (var i = from + 1; i < series.Length; i++)
            Assert.True(Math.Abs(series[i] - series[i - 1]) <= maxStep + 1e-9, $"period {i + 1}: moved {series[i] - series[i - 1]}");
    }

    [Fact]
    public void Quiet_region_settles_and_stays_settled()
    {
        var sim = Sim("corridor");
        sim.Run(40);
        foreach (var c in sim.History[^1].Cities)
        {
            var last = sim.History.Skip(30).Select(r => r.Of(c.CityId)).ToList();
            Assert.True(last.All(x => Math.Abs((x.BuyApplied ?? 0) - (last[0].BuyApplied ?? 0)) < 1e-6), c.CityId);
            Assert.InRange(c.Output, 0, c.Capacity + 1e-9);
            Assert.True(c.Use >= 0);
        }
    }

    [Fact]
    public void World_price_shock_is_followed_gradually_and_reversed()
    {
        // G01: Carford has no regional supplier and imports at 140 (128 + 12 freight).
        var sim = Sim("G01");
        sim.Run(5);
        var useBefore = sim.GoodsOf("carford").TotalUse;
        var price = sim.Scenario.Manifest.Parameters.ResourcePrices.Single();
        price.WorldProducerPriceIndex = 128 * 1.5;                     // +50%: target 204
        sim.Run(40);
        var series = BuyApplied(sim, "carford");
        AssertStepsAtMost(series, 2);                                  // 2% of the producer price (100) per period
        Assert.Equal(204, series[^1], 9);                              // reached: 64 up in 32 periods
        Assert.True(sim.GoodsOf("carford").TotalUse < useBefore);      // consumers buy less at the higher price
        var useDuringShock = sim.GoodsOf("carford").TotalUse;

        price.WorldProducerPriceIndex = 128;
        sim.Run(40);
        series = BuyApplied(sim, "carford");
        AssertStepsAtMost(series, 2);
        Assert.Equal(140, series[^1], 9);
        Assert.True(sim.GoodsOf("carford").TotalUse > useDuringShock);
        Assert.InRange(sim.GoodsOf("carford").TotalUse, useBefore * 0.995, useBefore + 1e-9); // and come back (gradually, by design)
    }

    [Fact]
    public void Supply_shock_raises_the_price_gradually_up_to_the_world_ceiling()
    {
        // G03 with Carford needing only 300: Ironvale's 300 spare covers it, so it pays 134 (no scarcity).
        var sim = Sim("G03", s => s.LatestSnapshots.Single(p => p.Snapshot.CityId == "carford").Snapshot.Resources[0].InputUsePerGameDay = 300);
        sim.Run(10);
        Assert.Equal(134, BuyApplied(sim, "carford")[^1], 6);

        var mill = sim.GoodsOf("ironvale");
        mill.Capacity = 850;                                           // a fire: 30% of capacity lost
        mill.BaseOutput = 850;
        sim.Run(40);
        var series = BuyApplied(sim, "carford");
        AssertStepsAtMost(series, 2);
        Assert.True(series.Skip(10).Zip(series.Skip(11)).All(p => p.Second >= p.First - 1e-9)); // only rises
        Assert.InRange(series[^1], 134 + 1e-6, 140 + 1e-9);            // scarcer steel, capped by the world price
        Assert.True(mill.Output <= 850 + 1e-9);
    }

    [Fact]
    public void Recession_lowers_prices_without_collapse_and_recovers()
    {
        var sim = Sim("G03");                                           // overloaded at the ceiling (140) to start
        sim.Run(10);
        var carford = sim.GoodsOf("carford");
        var normal = carford.BaseConsumerUse;
        carford.IndustrialUse *= 0.4;                                   // demand falls 60% (industrial part)
        carford.BaseConsumerUse *= 0.4;
        sim.Run(40);
        var low = BuyApplied(sim, "carford")[^1];
        Assert.True(low < 140, $"price stayed at {low}");
        Assert.True(low >= 134 - 1e-9);                                 // never below an unpressured mill
        Assert.True(sim.GoodsOf("ironvale").Output > 0);

        carford.IndustrialUse /= 0.4;
        carford.BaseConsumerUse = normal;
        sim.Run(40);
        Assert.Equal(140, BuyApplied(sim, "carford")[^1], 6);           // recovers to the ceiling
    }

    /// <summary>
    /// Long-unloaded city (§7): Carford is away for 30 periods while the world price rises 20%. Its applied price must
    /// stay frozen; once the other cities have published enough, its old snapshot ages out on both sides (D1); the
    /// exposure report says what it will meet; and on return its price moves at most one step per period.
    /// </summary>
    [Fact]
    public void Long_unloaded_city_freezes_ages_out_and_returns_gradually()
    {
        var sim = Sim("corridor", s => s.Manifest.Parameters.Staleness.MaxRegionSeqAge = 40);
        sim.Run(5);
        var appliedWhenLeft = sim.History[^1].Of("carford").BuyApplied!.Value;

        sim.Frozen.Add("carford");
        sim.Scenario.Manifest.Parameters.ResourcePrices.Single().WorldProducerPriceIndex = 128 * 1.2;
        sim.Run(30);
        Assert.All(sim.History.Skip(5), r => Assert.Equal(appliedWhenLeft, r.Of("carford").BuyApplied));
        Assert.All(sim.History.Skip(5), r => Assert.True(r.Of("carford").Frozen));

        // Three active cities publish every period: Carford's snapshot is now 90 ingestions old (> 40), so it is stale
        // and its demand no longer presses on Ironvale.
        var state = sim.Scenario.ToState();
        var engine = new LandedCostEngine();
        Assert.DoesNotContain(engine.Markets(state, "Steel").SelectMany(m => m.Buyers), b => b.CityId == "carford");

        var exposure = ExposureReport.Build(state, sim.Signals!, sim.SignalRule).Single(e => e.CityId == "carford");
        Assert.Equal("Forecast", exposure.ReportedAs);
        Assert.Equal(0, exposure.Freshness);
        Assert.Equal(appliedWhenLeft, exposure.AppliedNow);

        sim.Frozen.Remove("carford");                                  // loaded again
        sim.Run(30);
        var after = sim.History.Skip(34).Select(r => r.Of("carford").BuyApplied!.Value).ToArray();
        AssertStepsAtMost(after, 2);                                   // no jump on return, no backlog
        Assert.Equal(sim.History[^1].Of("carford").BuyTarget!.Value, after[^1], 6);
    }

    [Fact]
    public void Engine_demand_stays_vertical()
    {
        // Regression guard (§4.5): the engine must not respond to price with quantity. Only the simulator does.
        var s = Scenarios.Build("G03");
        s.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1.0 };
        var before = new LandedCostEngine().Markets(s.ToState(), "Steel").Single().Buyers.Single().Demand;
        s.Manifest.Parameters.ResourcePrices.Single().WorldProducerPriceIndex = 1000;
        var after = new LandedCostEngine().Markets(s.ToState(), "Steel").Single().Buyers.Single().Demand;
        Assert.Equal(before, after);
    }

    [Fact]
    public void Simulation_is_deterministic()
    {
        string Run()
        {
            var sim = Sim("corridor");
            sim.Run(25);
            return string.Join("|", sim.History.SelectMany(r => r.Cities.Select(c => $"{c.CityId}:{c.BuyApplied:R}:{c.SellApplied:R}:{c.Output:R}:{c.Use:R}")));
        }
        Assert.Equal(Run(), Run());
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 100).Select(i => new object[] { i });

    /// <summary>Runaway feedback check on random regions: bounded, and oscillations never grow.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_regions_stay_bounded(int seed)
    {
        var region = InvariantCheckerTests.RandomRegion(seed);
        region.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1.0 };
        region.Manifest.Parameters.Staleness.MaxRegionSeqAge = null;
        var sim = RegionSimulation.From(region);
        sim.Run(60);

        foreach (var c in sim.History.SelectMany(r => r.Cities))
        {
            Assert.True(c.Output >= 0 && c.Output <= c.Capacity + 1e-9 && double.IsFinite(c.Output), $"seed {seed}: output {c.Output}");
            Assert.True(c.Use >= 0 && double.IsFinite(c.Use), $"seed {seed}: use {c.Use}");
            foreach (var p in new[] { c.BuyApplied, c.SellApplied, c.BuyTarget, c.SellTarget })
                Assert.True(p == null || (double.IsFinite(p.Value) && p.Value >= 0), $"seed {seed}: price {p}");
        }

        foreach (var city in sim.Goods.Values.Select(g => g.CityId).Distinct())
        {
            var series = BuyApplied(sim, city).Where(double.IsFinite).ToArray();
            if (series.Length < 60) continue;
            double Amp(int from, int to) => series[from..to].Max() - series[from..to].Min();
            var noise = 0.002 * series.Average(); // swings under 0.2% of the price level are not a runaway
            Assert.True(Amp(45, 60) <= Amp(30, 45) + noise, $"seed {seed} {city}: oscillation grew from {Amp(30, 45)} to {Amp(45, 60)}");
        }
    }
}
