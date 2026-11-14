using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>
/// Phase 0 (docs/ECONOMIC_MODEL_PLAN.md §4): D1 ageing applies to supply and demand alike; D2 demand is not
/// discounted for Provisional figures; the subsidy forecast only counts steel that reachable cities need.
/// Golden region: maxRegionSeqAge 10; city-a (spare 300) regionSeq 1, city-b (needs 500) regionSeq 2, city-c regionSeq 3.
/// </summary>
public class FreshnessAndDemandTests
{
    [Theory]
    [InlineData(0, 10, 1.0)]
    [InlineData(5, 10, 1.0)]          // full weight for the first half of the limit
    [InlineData(6, 10, 1 - 1.0 / 6)]
    [InlineData(10, 10, 1 - 5.0 / 6)] // still counts a little at the limit
    [InlineData(11, 10, 0.0)]         // stale
    [InlineData(0, 0, 1.0)]
    [InlineData(1, 0, 0.0)]
    [InlineData(1000, null, 1.0)]     // no limit, no ageing
    public void Fade_curve(int age, int? max, double expected) => Assert.Equal(expected, Freshness.Weight(age, max), 12);

    [Fact]
    public void Fade_is_monotone_and_ends_at_zero()
    {
        for (var max = 0; max <= 20; max++)
            for (var age = 0; age <= max + 2; age++)
                Assert.True(Freshness.Weight(age + 1, max) <= Freshness.Weight(age, max));
    }

    private static Fixture WithAges(int ageA, int ageB, bool scarcity = true)
    {
        var f = Fixture.GoldenBasic();
        f.CurrentRegionSeq = 100;
        f.Snapshots = f.Snapshots.Select(s => (s.Snapshot, s.Snapshot.CityId switch
        {
            "city-a" => 100 - ageA,
            "city-b" => 100 - ageB,
            _ => 100,
        })).ToList();
        if (scarcity) f.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = 0.25, MaxPremium = 1 };
        return f;
    }

    private static SupplierMarket Market(Fixture f, string city) =>
        new LandedCostEngine().Markets(f.State(), "Steel").Single(m => m.CityId == city);

    [Fact]
    public void Ageing_supplier_offers_less()
    {
        var a = WithAges(ageA: 8, ageB: 0).Quote().From("city-a");   // weight 0.5
        Assert.True(a.Eligible);
        Assert.Equal(150, a.EffectiveRegionalOfferPerGameDay!.Value, 9);
        Assert.Contains("ageing snapshot x0.5", SupplyRule.Evaluate(WithAges(8, 0).Steel("city-a"), 0.5).Notes);
    }

    [Fact]
    public void Ageing_buyer_needs_less()
    {
        Assert.Equal(500, Market(WithAges(0, ageB: 3), "city-a").Buyers.Single().Demand, 9);   // fresh: 500 > 300, premium
        Assert.Equal(6, WithAges(0, 3).Quote().From("city-a").ScarcityPremium!.Value, 9);
        Assert.Equal(250, Market(WithAges(0, ageB: 8), "city-a").Buyers.Single().Demand, 9);   // weight 0.5
        Assert.Equal(0, WithAges(0, 8).Quote().From("city-a").ScarcityPremium);               // 250 < 300 spare
    }

    [Fact]
    public void Stale_buyer_no_longer_counts()
    {
        // Before Phase 0, a stale city's need kept pushing prices up after its supply had gone.
        var f = WithAges(0, ageB: 11);
        Assert.Empty(Market(f, "city-a").Buyers);
        Assert.Equal(0, f.Quote().From("city-a").ScarcityPremium);
    }

    [Fact]
    public void Provisional_demand_is_not_discounted()
    {
        // D2: city-b's figures are Provisional in the golden region; its 500 counts in full.
        var f = WithAges(0, 0);
        Assert.Equal(Confidence.Provisional, f.Steel("city-b").Confidence);
        Assert.Equal(500, Market(f, "city-a").Buyers.Single().Demand, 9);
    }

    // ---- Subsidy forecast: only steel that reachable cities need ----

    private static Fixture Subsidised(double budget)
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Policies.FreightSubsidies.Add(new FreightSubsidy
        {
            PolicyId = "s1", ResourceId = "Steel", Mode = TransportMode.Rail, PercentOfFreight = 25, BudgetPerGameDay = budget,
        });
        return f;
    }

    [Fact]
    public void Subsidy_exposure_is_capped_by_reachable_need()
    {
        var f = Subsidised(425);
        f.Steel("city-b").InputUsePerGameDay = 100;   // Carford needs only 100 of Ironvale's 300 spare
        var forecast = Assert.Single(new LandedCostEngine().Forecast(f.State()));
        Assert.Equal(100 * 8.5, forecast.ExposurePerGameDay!.Value, 9);
        Assert.Equal(0.5, forecast.Scale!.Value, 9);
        Assert.Equal(134 - 4.25, f.Quote().From("city-a").LandedPrice!.Value, 9);
    }

    [Fact]
    public void Unknown_need_falls_back_to_the_whole_offer()
    {
        var f = Subsidised(1e9);
        f.Steel("city-b").InputUsePerGameDay = null;
        Assert.Equal(300 * 8.5, Assert.Single(new LandedCostEngine().Forecast(f.State())).ExposurePerGameDay!.Value, 9);
    }

    [Fact]
    public void No_need_means_no_exposure()
    {
        var f = Subsidised(1);
        f.Steel("city-b").InputUsePerGameDay = 0;     // Carford makes 0 and uses 0: needs nothing
        var forecast = Assert.Single(new LandedCostEngine().Forecast(f.State()));
        Assert.Equal(0, forecast.ExposurePerGameDay);
        Assert.Equal(1, forecast.Scale);
    }
}
