using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>A7: import tariffs (G04), freight subsidies (G05) and subsidy budget caps (G09). Log §17.4, §17.10.</summary>
public class PolicyTests
{
    private static Fixture WithTariff(double percent, double specific = 0, string resource = "Steel")
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Policies.ImportTariffs.Add(new ImportTariff { PolicyId = "t1", ResourceId = resource, AdValoremPercent = percent, SpecificPerUnit = specific });
        return f;
    }

    private static Fixture WithSubsidy(TransportMode mode, double percent, double budget = 1e9, string resource = "Steel", params string[] links)
    {
        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Policies.FreightSubsidies.Add(new FreightSubsidy
        {
            PolicyId = "s1", ResourceId = resource, Mode = mode, PercentOfFreight = percent, BudgetPerGameDay = budget,
            LinkIds = links.Length == 0 ? null : links.ToList(),
        });
        return f;
    }

    // ---- G04: import tariff ----

    [Fact]
    public void G04_tariff_raises_only_the_world_offer()
    {
        var q = WithTariff(25).Quote();
        Q.AssertEligible(q.World(), 172); // 128 + 12 + 25% of 128
        Assert.Equal(32, q.World().Tariff);
        Q.AssertEligible(q.From("city-a"), 134, "ab-rail-01"); // domestic: no customs (D-44)
        Assert.Equal(0, q.From("city-a").Tariff);
    }

    [Fact]
    public void G04_tariff_can_flip_the_winner_to_the_domestic_supplier()
    {
        var f = WithTariff(10);
        f.RemoveLink("ab-rail-01");
        var q = f.Quote();
        Assert.Equal(152.8, q.World().LandedPrice!.Value, 9);
        Assert.Same(q.From("city-a"), q.Selected()); // road 142 now beats world 152.80

        f.Manifest.Parameters.Policies.ImportTariffs.Clear();
        var without = f.Quote();
        Assert.Same(without.World(), without.Selected()); // without the tariff the world (140) wins
    }

    [Fact]
    public void G04_specific_tariff_adds_a_fixed_amount()
    {
        Assert.Equal(145, WithTariff(0, specific: 5).Quote().World().LandedPrice!.Value, 9);
        Assert.Equal(177, WithTariff(25, specific: 5).Quote().World().LandedPrice!.Value, 9);
    }

    [Fact]
    public void Tariff_on_another_resource_changes_nothing()
    {
        Assert.Equal(Q.Decision(Fixture.GoldenBasic().Quote()), Q.Decision(WithTariff(50, resource: "Food").Quote()));
    }

    // ---- G05: freight subsidy ----

    [Fact]
    public void G05_rail_subsidy_lowers_only_the_rail_route()
    {
        var q = WithSubsidy(TransportMode.Rail, 25).Quote();
        Q.AssertEligible(q.From("city-a"), 125.5, "ab-rail-01"); // 134 - 25% of 34
        Assert.Equal(8.5, q.From("city-a").Subsidy!.Value, 9);
        Q.AssertEligible(q.World(), 140);                        // foreign leg: no domestic subsidy
        Assert.Equal(0, q.World().Subsidy);
        Q.AssertExcluded(q.From("city-c"), ExcludedReason.NoPath); // a subsidy never creates a path
    }

    [Theory]
    [InlineData(TransportMode.Road, "Steel")]  // road subsidy, but the route is rail
    [InlineData(TransportMode.Ship, "Steel")]  // no ship links at all
    [InlineData(TransportMode.Rail, "Food")]   // another resource
    public void Subsidy_outside_its_scope_changes_nothing(TransportMode mode, string resource)
    {
        Assert.Equal(Q.Decision(Fixture.GoldenBasic().Quote()), Q.Decision(WithSubsidy(mode, 50, resource: resource).Quote()));
    }

    [Fact]
    public void Link_scoped_subsidy_applies_only_to_listed_links()
    {
        Assert.Equal(125.5, WithSubsidy(TransportMode.Rail, 25, links: "ab-rail-01").Quote().From("city-a").LandedPrice!.Value, 9);

        var f = WithSubsidy(TransportMode.Road, 25, links: "ab-road-01");
        f.RemoveLink("ab-rail-01");
        Assert.Equal(131.5, f.Quote().From("city-a").LandedPrice!.Value, 9); // 142 - 25% of 42
    }

    [Fact]
    public void Full_subsidy_never_goes_below_the_producer_price()
    {
        var a = WithSubsidy(TransportMode.Rail, 100).Quote().From("city-a");
        Assert.Equal(100, a.LandedPrice!.Value, 9);
        Assert.Equal(a.Freight!.Value, a.Subsidy!.Value, 9);
    }

    [Fact]
    public void Route_is_chosen_by_freight_before_subsidy()
    {
        // A 100% road subsidy would make road cheaper (100 vs 134), but v1 picks the physically cheapest path
        // and subsidises it (PolicyEvaluator). This test pins that documented choice.
        var a = WithSubsidy(TransportMode.Road, 100).Quote().From("city-a");
        Q.AssertEligible(a, 134, "ab-rail-01");
        Assert.Equal(0, a.Subsidy);
    }

    // ---- G09: budget cap ----

    [Theory]
    [InlineData(1e9, 125.5, 1.0)]  // exposure 300/day x 8.5 = 2550, fully covered
    [InlineData(2550, 125.5, 1.0)] // exactly covered
    [InlineData(1275, 129.75, 0.5)]
    [InlineData(0, 134, 0.0)]      // no budget: the subsidy stops
    public void G09_budget_scales_the_subsidy(double budget, double landed, double scale)
    {
        var f = WithSubsidy(TransportMode.Rail, 25, budget);
        Assert.Equal(landed, f.Quote().From("city-a").LandedPrice!.Value, 9);

        var forecast = Assert.Single(new LandedCostEngine().Forecast(f.State()));
        Assert.Equal(2550, forecast.ExposurePerGameDay!.Value, 9);
        Assert.Equal(scale, forecast.Scale!.Value, 9);
        Assert.Equal(Math.Min(2550, budget), forecast.ForecastSpendPerGameDay!.Value, 9);
        Assert.True(forecast.ForecastSpendPerGameDay <= budget);
    }

    [Fact]
    public void Exposure_ignores_stale_unknown_and_unreachable_suppliers()
    {
        var f = WithSubsidy(TransportMode.Rail, 25, budget: 1);
        f.CurrentRegionSeq = 12; // city-a (regionSeq 1) is stale; city-c has no route; city-b has no spare supply
        var forecast = Assert.Single(new LandedCostEngine().Forecast(f.State()));
        Assert.Equal(0, forecast.ExposurePerGameDay);
        Assert.Equal(1, forecast.Scale);
    }

    [Fact]
    public void Budget_scale_does_not_depend_on_the_destination_quoted()
    {
        var f = WithSubsidy(TransportMode.Rail, 25, budget: 1275);
        f.Snapshots.Single(s => s.Snapshot.CityId == "city-c").Snapshot.Resources.Clear();
        var toB = f.Quote(dest: "city-b").From("city-a").Subsidy;
        var forecast = Assert.Single(new LandedCostEngine().Forecast(f.State()));
        Assert.Equal(8.5 * forecast.Scale!.Value, toB!.Value, 9);
    }

    [Fact]
    public void Tariff_forecast_does_not_invent_revenue()
    {
        var forecast = Assert.Single(new LandedCostEngine().Forecast(WithTariff(25).State()));
        Assert.Equal("ImportTariff", forecast.Kind);
        Assert.Null(forecast.ForecastSpendPerGameDay);
        Assert.Contains("not tracked", forecast.Note);
    }

    // ---- Validation ----

    [Fact]
    public void Invalid_policies_are_rejected()
    {
        var f = Fixture.GoldenBasic();
        var p = f.Manifest.Parameters.Policies;
        p.ImportTariffs.Add(new ImportTariff { PolicyId = "dup", ResourceId = "Steel", AdValoremPercent = -1 });
        p.ImportTariffs.Add(new ImportTariff { PolicyId = "t2", ResourceId = "Steel", SpecificPerUnit = double.NaN });
        p.FreightSubsidies.Add(new FreightSubsidy { PolicyId = "dup", ResourceId = "Steel", Mode = TransportMode.Rail, PercentOfFreight = 150 });
        p.FreightSubsidies.Add(new FreightSubsidy { PolicyId = "s2", ResourceId = "Steel", Mode = TransportMode.Rail, BudgetPerGameDay = -1 });
        p.FreightSubsidies.Add(new FreightSubsidy { PolicyId = "s3", ResourceId = "Food", Mode = TransportMode.Rail, LinkIds = new() { "nope", "ab-road-01" } });
        var problems = Assert.Throws<RegionStateException>(() => f.Quote()).Problems;

        Assert.Contains(problems, x => x.Contains("adValoremPercent"));
        Assert.Contains(problems, x => x.Contains("specificPerUnit"));
        Assert.Contains(problems, x => x.Contains("more than one import tariff for Steel"));
        Assert.Contains(problems, x => x.Contains("policies[dup]: empty or duplicate policyId"));
        Assert.Contains(problems, x => x.Contains("percentOfFreight"));
        Assert.Contains(problems, x => x.Contains("budgetPerGameDay"));
        Assert.Contains(problems, x => x.Contains("more than one freight subsidy for Steel by Rail"));
        Assert.Contains(problems, x => x.Contains("unknown link nope"));
        Assert.Contains(problems, x => x.Contains("link ab-road-01 is Road, the subsidy is for Rail"));
    }

    // ---- Replay ----

    [Fact]
    public void Policies_change_the_inputs_hash_and_a_missing_block_equals_an_empty_one()
    {
        var baseline = Fixture.GoldenBasic().Quote().Provenance.InputsSha256;
        Assert.NotEqual(baseline, WithTariff(25).Quote().Provenance.InputsSha256);
        Assert.NotEqual(baseline, WithSubsidy(TransportMode.Rail, 25).Quote().Provenance.InputsSha256);

        var f = Fixture.GoldenBasic();
        f.Manifest.Parameters.Policies = null!; // a manifest written before A7
        Assert.Equal(baseline, f.Quote().Provenance.InputsSha256);
    }

    [Fact]
    public void Checker_passes_with_policies()
    {
        var f = WithSubsidy(TransportMode.Rail, 25, budget: 1275);
        f.Manifest.Parameters.Policies.ImportTariffs.Add(new ImportTariff { PolicyId = "t1", ResourceId = "Steel", AdValoremPercent = 10 });
        var report = new LandedCostEngine().Check(f.State());
        Assert.True(report.Passed, string.Join("\n", report.Violations));
    }
}
