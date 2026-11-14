using Regions.Contracts;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

/// <summary>
/// Phase 4 controlled tests (docs/ECONOMIC_MODEL_PLAN.md §7): jobs, commuting, migration, business entry and exit,
/// and spending, as aggregate groups in the synthetic simulator. Pass criteria are fixed before running.
/// Corridor: Ironvale -rail 300 km- Midway -road 60 km- Carford; Lonepeak is isolated.
/// </summary>
public class PeopleTests
{
    private static RegionSimulation Sim(string scenario, Action<RegionScenario>? setup = null)
    {
        var s = Scenarios.Build(scenario);
        s.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1.0 };
        s.Manifest.Parameters.Staleness.MaxRegionSeqAge = null;
        setup?.Invoke(s);
        var sim = RegionSimulation.From(s);
        PeopleDynamics.Attach(sim);
        return sim;
    }

    /// <summary>Invariants that must hold after every period of every people test.</summary>
    private static void AssertLedger(RegionSimulation sim, double initialTotal)
    {
        var people = sim.People.Values.ToList();
        foreach (var p in people)
        {
            Assert.All(p.Population, x => Assert.True(x >= -1e-9, $"{p.CityId}: negative population"));
            Assert.True(p.TotalPopulation <= p.Housing + 1e-6, $"{p.CityId}: {p.TotalPopulation} people in {p.Housing} homes");
            Assert.All(p.Unemployed, x => Assert.True(x >= -1e-9));
            Assert.All(p.Vacancies, x => Assert.True(x >= -1e-9));
        }
        // Domestic moves cancel (§26.7 #3); the country changes only through international flows.
        Assert.Equal(people.Sum(p => p.InternalArrivals), people.Sum(p => p.InternalDepartures), 6);
        var international = people.Sum(p => p.InternationalArrivals - p.InternationalDepartures);
        Assert.Equal(initialTotal + international, people.Sum(p => p.TotalPopulation), 6);
    }

    private static double Total(RegionSimulation sim) => sim.People.Values.Sum(p => p.TotalPopulation);

    [Fact]
    public void Unemployment_shock_moves_people_along_links_only()
    {
        var sim = Sim("corridor");
        var start = Total(sim);
        sim.Run(10);
        var ironvale = sim.People["ironvale"];
        var before = ironvale.UnemploymentRate;
        var consumerBefore = sim.GoodsOf("ironvale").BaseConsumerUse;

        var mill = sim.GoodsOf("ironvale");                              // the mill halves: jobs go
        mill.Capacity *= 0.5; mill.BaseOutput *= 0.5; mill.SpaceCapacity *= 0.5;
        sim.Run(1);
        Assert.True(ironvale.UnemploymentRate > before + 0.02, $"unemployment {before} -> {ironvale.UnemploymentRate}");
        Assert.True(sim.GoodsOf("ironvale").BaseConsumerUse < consumerBefore);   // less income, less spending

        for (var i = 0; i < 30; i++) { sim.Run(1); AssertLedger(sim, start); }
        Assert.True(ironvale.InternalDepartures > 0);
        Assert.True(sim.People["midway"].InternalArrivals > 0);           // directly linked to Ironvale
        Assert.Equal(0, sim.People["lonepeak"].InternalArrivals);         // isolated: nobody can move there
        Assert.Equal(0, sim.People["lonepeak"].InternalDepartures);
    }

    [Fact]
    public void Commuting_needs_a_short_link_and_moves_nobody()
    {
        var sim = Sim("corridor");
        var carford = sim.People["carford"];
        for (var g = 0; g < 3; g++) carford.OtherJobs[g] += 200;          // a new business park in Carford
        var midwayPopulation = sim.People["midway"].TotalPopulation;
        sim.Run(1);
        Assert.True(carford.CommutersIn > 0);                              // Midway is 60 km away: workers commute
        Assert.Equal(carford.CommutersIn, sim.People["midway"].CommutersOut, 9);
        Assert.Equal(0, sim.People["ironvale"].CommutersOut);              // 300 km: too far to commute
        Assert.True(sim.People["midway"].TotalPopulation >= midwayPopulation - sim.People["midway"].InternalDepartures - 1e-9);
    }

    [Fact]
    public void Shortage_attracts_investment_until_the_shortage_ends()
    {
        var sim = Sim("G03");                                              // Ironvale overloaded: sells at 106
        var mill = sim.GoodsOf("ironvale");
        var startCapacity = mill.Capacity;
        sim.Run(60);
        Assert.True(mill.Capacity > startCapacity);
        Assert.True(mill.Capacity <= mill.SpaceCapacity + 1e-9);           // only in existing industrial space
        Assert.True(sim.History[^1].Of("carford").BuyApplied < 140);       // the shortage price has eased
    }

    [Fact]
    public void No_idle_workers_means_no_new_capacity()
    {
        var sim = Sim("G03");
        foreach (var p in sim.People.Values) for (var g = 0; g < 3; g++) p.OtherJobs[g] += 10_000; // everyone employed
        var mill = sim.GoodsOf("ironvale");
        var startCapacity = mill.Capacity;
        sim.Run(20);
        Assert.Equal(startCapacity, mill.Capacity, 9);
        Assert.Equal(140, sim.History[^1].Of("carford").BuyApplied!.Value, 6); // the shortage stays
    }

    [Fact]
    public void Idle_capacity_closes_gradually_and_never_below_output()
    {
        var sim = Sim("G03", s => s.LatestSnapshots.Single(p => p.Snapshot.CityId == "ironvale").Snapshot.Resources[0].CapacityEstimatePerGameDay = 3000);
        var mill = sim.GoodsOf("ironvale");
        var capacities = new List<double>();
        for (var i = 0; i < 40; i++) { sim.Run(1); capacities.Add(mill.Capacity); Assert.True(mill.Capacity >= mill.Output - 1e-9); }
        Assert.True(capacities[^1] < 3000);
        Assert.True(capacities.Zip(capacities.Skip(1)).All(x => x.Second <= x.First + 1e-9)); // only shrinks
        Assert.True(capacities.Zip(capacities.Skip(1)).All(x => x.First - x.Second <= 0.03 * x.First + 1e-9)); // at most 3% a period
    }

    [Fact]
    public void Frozen_city_takes_no_part()
    {
        var sim = Sim("corridor");
        sim.Frozen.Add("midway");
        var midway = sim.People["midway"];
        var population = midway.Population.ToArray();
        var mill = sim.GoodsOf("ironvale");
        mill.Capacity *= 0.5; mill.BaseOutput *= 0.5; mill.SpaceCapacity *= 0.5;
        sim.Run(20);
        Assert.Equal(population, midway.Population);                       // nobody arrives in or leaves an unloaded city
        Assert.Equal(0, midway.InternalArrivals);
        Assert.Equal(0, sim.People["ironvale"].InternalDepartures);        // its only link leads to the unloaded city
    }

    [Fact]
    public void People_simulation_is_deterministic()
    {
        string Run()
        {
            var sim = Sim("corridor");
            sim.Run(30);
            return string.Join("|", sim.People.Values.Select(p => string.Join(",", p.Population.Select(x => x.ToString("R")))))
                + string.Join("|", sim.History[^1].Cities.Select(c => $"{c.BuyApplied:R}:{c.Capacity:R}"));
        }
        Assert.Equal(Run(), Run());
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 100).Select(i => new object[] { i });

    /// <summary>Runaway feedback check with people on: bounded, ledger consistent, oscillations never grow.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_regions_with_people_stay_bounded(int seed)
    {
        var region = InvariantCheckerTests.RandomRegion(seed);
        region.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1.0 };
        region.Manifest.Parameters.Staleness.MaxRegionSeqAge = null;
        var sim = RegionSimulation.From(region);
        PeopleDynamics.Attach(sim);
        var start = Total(sim);
        for (var i = 0; i < 60; i++) { sim.Run(1); AssertLedger(sim, start); }

        foreach (var g in sim.Goods.Values)
        {
            Assert.True(g.Capacity >= 0 && g.Capacity <= Math.Max(g.SpaceCapacity, 0) + 1e-6 || g.SpaceCapacity == 0, $"seed {seed}: capacity {g.Capacity} > space {g.SpaceCapacity}");
            var series = sim.History.Select(r => r.Of(g.CityId, g.ResourceId).BuyApplied ?? double.NaN).Where(double.IsFinite).ToArray();
            if (series.Length < 60) continue;
            double Amp(int from, int to) => series[from..to].Max() - series[from..to].Min();
            var noise = 0.002 * series.Average(); // swings under 0.2% of the price level are not a runaway
            Assert.True(Amp(45, 60) <= Amp(30, 45) + noise, $"seed {seed} {g.CityId}: oscillation grew from {Amp(30, 45)} to {Amp(45, 60)}");
        }
    }
}
