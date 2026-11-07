using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>
/// Scarcity pricing on the golden region. city-a (Ironvale) spares 300/day (output 1000 - use 700, capacity 1200);
/// city-b (Carford) needs 500/day (use 500 - output 0) and is 34 away by rail from city-a; the world delivers to
/// city-b at 140. Default rule: +25% of the producer price per 100% overload, at most +100%.
/// </summary>
public class ScarcityTests
{
    private static Fixture Golden(double slope = 0.25, double max = 1.0)
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = slope, MaxPremium = max };
        return f;
    }

    private static SupplierMarket Market(Fixture f, string city) =>
        new LandedCostEngine().Markets(f.State(), "Steel").Single(m => m.CityId == city);

    [Fact]
    public void Off_by_default_and_identical_to_before()
    {
        var f = Fixture.GoldenBasic();
        Assert.Null(f.Manifest.Parameters.Scarcity);
        Assert.Empty(new LandedCostEngine().Markets(f.State(), "Steel"));
        var a = f.Quote().From("city-a");
        Assert.Equal(0, a.ScarcityPremium);
        Assert.Equal(134, a.LandedPrice);
    }

    [Fact]
    public void Overloaded_supplier_is_priced_up_to_the_world_ceiling()
    {
        // 500 / 300 = 167% load would ask +16.67, but at a gate price above 106 Carford (140 - 34) would import instead.
        var f = Golden();
        var q = f.Quote();
        var a = q.From("city-a");
        Assert.Equal(6, a.ScarcityPremium!.Value, 9);
        Assert.Equal(140, a.LandedPrice!.Value, 9);       // exactly the world price: Carford is the indifferent buyer
        Assert.Same(a, q.Selected());                      // a tie goes to the city offer (it comes first)

        var m = Market(f, "city-a");
        Assert.Equal((300.0, 6.0, 106.0), (m.SpareSupply, m.Premium, m.GatePrice));
        var buyer = Assert.Single(m.Buyers);
        Assert.Equal(("city-b", 500.0, BuyerStatus.Indifferent), (buyer.CityId, buyer.Demand, buyer.Status));
        Assert.Equal(106, buyer.SwitchPrice, 9);
        Assert.Equal(372, m.Load, 9);                      // supply at 106: 300 x (1 + 0.06 / 0.25)
    }

    [Fact]
    public void Moderate_overload_raises_the_price_below_the_ceiling()
    {
        var f = Golden();
        f.Steel("city-b").InputUsePerGameDay = 350;       // 350 / 300: +25% x 0.1667 = +4.1667
        var a = f.Quote().From("city-a");
        Assert.Equal(25.0 / 6, a.ScarcityPremium!.Value, 9);
        Assert.Equal(134 + 25.0 / 6, a.LandedPrice!.Value, 9);
        var m = Market(f, "city-a");
        Assert.Equal(BuyerStatus.Served, m.Buyers.Single().Status);
        Assert.Equal(350, m.Load, 9);
    }

    [Theory]
    [InlineData(1300.0, 500.0)] // more output: 600 spare covers 500
    [InlineData(1000.0, 300.0)] // less demand: exactly the 300 spare
    [InlineData(1000.0, 0.0)]   // no demand at all
    public void Enough_supply_means_no_premium(double outputA, double useB)
    {
        var f = Golden();
        f.Steel("city-a").RealizedOutputPerGameDay = outputA;
        f.Steel("city-b").InputUsePerGameDay = useB;
        Assert.Equal(0, f.Quote().From("city-a").ScarcityPremium);
        Assert.Equal(134, f.Quote().From("city-a").LandedPrice!.Value, 9);
    }

    [Fact]
    public void Slope_and_cap_shape_the_supply_curve()
    {
        Assert.Equal(0, Golden(slope: 0).Quote().From("city-a").ScarcityPremium);
        Assert.Equal(3, Golden(max: 0.03).Quote().From("city-a").ScarcityPremium!.Value, 9);   // capped below the ceiling
        Assert.Equal(6, Golden(slope: 5).Quote().From("city-a").ScarcityPremium!.Value, 9);    // steep: straight to the ceiling
    }

    [Fact]
    public void Without_a_world_option_the_supplier_charges_what_the_curve_asks()
    {
        var f = Golden();
        f.Manifest.WorldGateways.Clear();
        var a = f.Quote().From("city-a");
        Assert.Equal(100 * 0.25 * (500.0 / 300 - 1), a.ScarcityPremium!.Value, 9); // 16.67, no ceiling
        Assert.Equal(BuyerStatus.Served, Market(f, "city-a").Buyers.Single().Status);
        Assert.True(double.IsPositiveInfinity(Market(f, "city-a").Buyers.Single().SwitchPrice));
    }

    [Fact]
    public void Unreachable_and_untrusted_demand_does_not_count()
    {
        var f = Golden();
        f.Manifest.Links.Clear();                         // Carford cannot reach Ironvale
        Assert.Empty(Market(f, "city-a").Buyers);

        f = Golden();
        f.Steel("city-b").Confidence = Confidence.Unknown; // figures not trusted: no demand
        Assert.Empty(Market(f, "city-a").Buyers);
        Assert.Equal(0, f.Quote().From("city-a").ScarcityPremium);

        f = Golden();
        f.Steel("city-b").InputUsePerGameDay = null;      // unknown use is not demand
        Assert.Empty(Market(f, "city-a").Buyers);
    }

    [Fact]
    public void World_offers_never_carry_scarcity()
    {
        var w = Golden().Quote().World();
        Assert.Equal(0, w.ScarcityPremium);
        Assert.Equal(140, w.LandedPrice);
    }

    [Fact]
    public void Competing_supplier_takes_the_overflow()
    {
        // Give city-c a road to city-b at 150 km: 100 + 17 = 117, cheaper than city-a's 134.
        var f = Golden();
        f.City("city-c").OutsideConnections[0].TransportModes = new() { TransportMode.Road };
        f.City("city-b").OutsideConnections.Add(new OutsideConnectionInfo { EndpointKey = "oc-road-north-01", Kind = ConnectionKind.Transport, TransportModes = { TransportMode.Road }, CarriesCargo = true });
        f.Manifest.Links.Add(ScenarioTests.RoadLink("cb-road-01", "city-c", "oc-road-north-01", "city-b", "oc-road-north-01", 150));
        var q = f.Quote();
        Assert.Same(q.From("city-c"), q.Selected());
        Assert.Equal(0, q.From("city-c").ScarcityPremium); // 4900 spare easily covers 500
        Assert.Equal(0, q.From("city-a").ScarcityPremium); // Carford leans on city-c, so city-a is not pressured
        Assert.Equal(BuyerStatus.PricedOut, Market(f, "city-a").Buyers.Single().Status);
    }

    [Fact]
    public void Scarcity_changes_the_inputs_hash_and_is_validated()
    {
        Assert.NotEqual(Fixture.GoldenBasic().Quote().Provenance.InputsSha256, Golden().Quote().Provenance.InputsSha256);
        Assert.NotEqual(Golden(0.25).Quote().Provenance.InputsSha256, Golden(0.5).Quote().Provenance.InputsSha256);
        var problems = Assert.Throws<RegionStateException>(() => Golden(slope: -1, max: double.NaN).Quote()).Problems;
        Assert.Contains(problems, p => p.Contains("premiumSlope"));
        Assert.Contains(problems, p => p.Contains("maxPremium"));
    }

    [Fact]
    public void Checker_passes_with_scarcity_and_policies()
    {
        var f = Golden();
        f.Manifest.Parameters.Policies.ImportTariffs.Add(new ImportTariff { PolicyId = "t1", ResourceId = "Steel", AdValoremPercent = 20 });
        var report = new LandedCostEngine().Check(f.State());
        Assert.True(report.Passed, string.Join("\n", report.Violations));
    }
}
