using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

/// <summary>
/// Supply and demand behave the way the textbook says, on random regions with scarcity pricing on:
/// more supply never raises a price, more demand never lowers one, premiums stay within the cap.
/// </summary>
public class ScarcityPropertyTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 150).Select(i => new object[] { i });

    /// <summary>
    /// Random region with scarcity on. Freight subsidies are removed by default: a budget-capped subsidy is spread
    /// thinner when more volume qualifies, which can legitimately raise a price when supply grows. That is the budget
    /// cap at work, tested separately (pre-support prices) below.
    /// </summary>
    private static RegionScenario Region(int seed, bool keepSubsidies = false)
    {
        var region = InvariantCheckerTests.RandomRegion(seed);
        var rng = new Random(seed + 10_000);
        region.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = new[] { 0.1, 0.25, 1.0, 5.0 }[rng.Next(4)], MaxPremium = new[] { 0.2, 1.0, 3.0 }[rng.Next(3)] };
        if (!keepSubsidies) region.Manifest.Parameters.Policies.FreightSubsidies.Clear();
        return region;
    }

    /// <summary>The price each city pays: its selected offer, or +infinity when nothing reaches it.
    /// With preSupport, the cheapest offer before freight subsidies instead.</summary>
    private static Dictionary<string, double> Prices(RegionState state, bool preSupport = false)
    {
        var engine = new LandedCostEngine();
        return state.Manifest.Cities.ToDictionary(c => c.CityId, c =>
        {
            var q = engine.Quote(state, new QuoteRequest { DestCityId = c.CityId, ResourceId = "Steel" });
            if (preSupport)
                return q.Offers.Where(o => o.Eligible).Select(o => o.LandedPrice!.Value + o.Subsidy!.Value).DefaultIfEmpty(double.PositiveInfinity).Min();
            return q.SelectedOfferIndex is int i ? q.Offers[i].LandedPrice!.Value : double.PositiveInfinity;
        });
    }

    private static ResourceObservation? Steel(RegionScenario r, string city) =>
        r.LatestSnapshots.Single(p => p.Snapshot.CityId == city).Snapshot.Resources.FirstOrDefault();

    [Theory]
    [MemberData(nameof(Seeds))]
    public void More_supply_never_raises_any_price(int seed)
    {
        var region = Region(seed);
        var before = Prices(region.ToState());
        foreach (var city in region.Manifest.Cities.Select(c => c.CityId))
        {
            var steel = Steel(region, city);
            if (steel?.RealizedOutputPerGameDay is not double output) continue;
            steel.RealizedOutputPerGameDay = output + 400;
            var after = Prices(region.ToState());
            steel.RealizedOutputPerGameDay = output;
            foreach (var (c, p) in after)
                Assert.True(p <= before[c] + 1e-6, $"seed {seed}: +400 output in {city} raised {c} from {before[c]} to {p}");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void More_demand_never_lowers_any_price(int seed)
    {
        var region = Region(seed);
        var before = Prices(region.ToState());
        foreach (var city in region.Manifest.Cities.Select(c => c.CityId))
        {
            var steel = Steel(region, city);
            if (steel?.InputUsePerGameDay is not double use) continue;
            steel.InputUsePerGameDay = use + 400;
            var after = Prices(region.ToState());
            steel.InputUsePerGameDay = use;
            foreach (var (c, p) in after)
                Assert.True(p >= before[c] - 1e-6, $"seed {seed}: +400 use in {city} lowered {c} from {before[c]} to {p}");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Premiums_respect_the_cap_and_the_rules_hold(int seed)
    {
        var region = Region(seed);
        var state = region.ToState();
        var rule = region.Manifest.Parameters.Scarcity!;
        foreach (var m in new LandedCostEngine().Markets(state, "Steel"))
        {
            Assert.InRange(m.Premium, 0, rule.MaxPremium * m.BasePrice + 1e-9);
            Assert.True(m.Pressure >= 0 && double.IsFinite(m.Pressure));
            if (m.Buyers.All(b => b.Status == BuyerStatus.PricedOut)) Assert.Equal(0, m.Premium, 9);
        }
        var report = new LandedCostEngine().Check(state);
        Assert.True(report.Passed, $"seed {seed}:\n" + string.Join("\n", report.Violations));
        var withPolicies = new LandedCostEngine().Check(Region(seed, keepSubsidies: true).ToState());
        Assert.True(withPolicies.Passed, $"seed {seed} with subsidies:\n" + string.Join("\n", withPolicies.Violations));
    }

    /// <summary>With budget-capped subsidies kept, the price before subsidies still follows supply and demand.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void With_subsidies_prices_before_support_still_follow_supply(int seed)
    {
        var region = Region(seed, keepSubsidies: true);
        var before = Prices(region.ToState(), preSupport: true);
        foreach (var city in region.Manifest.Cities.Select(c => c.CityId))
        {
            var steel = Steel(region, city);
            if (steel?.RealizedOutputPerGameDay is not double output) continue;
            steel.RealizedOutputPerGameDay = output + 400;
            var after = Prices(region.ToState(), preSupport: true);
            steel.RealizedOutputPerGameDay = output;
            foreach (var (c, p) in after)
                Assert.True(p <= before[c] + 1e-6, $"seed {seed}: +400 output in {city} raised {c} before subsidies from {before[c]} to {p}");
        }
    }

    [Fact]
    public void Solving_is_deterministic()
    {
        for (var seed = 0; seed < 20; seed++)
            Assert.Equal(Prices(Region(seed).ToState()), Prices(Region(seed).ToState()));
    }
}
