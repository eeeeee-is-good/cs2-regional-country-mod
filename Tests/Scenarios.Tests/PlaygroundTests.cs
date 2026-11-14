using System.Text.Json;
using System.Text.Json.Serialization;
using Regions.Contracts;
using Regions.Engine;
using Regions.Playground;
using Xunit;

namespace Regions.ScenarioTests;

/// <summary>The sandbox must show exactly what the engine computes: it is a view, not a second pricing model.</summary>
public class PlaygroundTests
{
    public static IEnumerable<object[]> Names() => Simulator.Scenarios.All.Select(e => new object[] { e.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Sandbox_gives_the_same_prices_as_the_engine_for_every_scenario(string name)
    {
        var scenario = Simulator.Scenarios.Build(name);
        // The sandbox opens examples with its default scarcity rule on; compare against the engine with the same rule.
        scenario.Manifest.Parameters.Scarcity ??= new ScarcityRule
        {
            PremiumSlope = Playground.Playground.DefaultScarcitySlope, MaxPremium = Playground.Playground.DefaultScarcityCap,
        };
        var direct = scenario.ToState();
        var model = Playground.Playground.FromScenario(Simulator.Scenarios.Build(name));
        var analysis = Playground.Playground.Analyze(model);

        Assert.Empty(analysis.Errors);
        Assert.True(analysis.RulesPassed, string.Join("\n", analysis.Violations));
        var engine = new LandedCostEngine();
        foreach (var city in direct.Manifest.Cities)
        {
            var expected = engine.Quote(direct, new QuoteRequest { DestCityId = city.CityId, ResourceId = "Steel" });
            var actual = analysis.Cities[city.CityId].Quote!;
            Assert.Equal(expected.SelectedOfferIndex, actual.SelectedOfferIndex);
            Assert.Equal(Prices(expected), Prices(actual));
        }
    }

    private static string Prices(QuoteResult q) =>
        string.Join("|", q.Offers.Select(o => $"{o.OriginKind}:{o.OriginCityId}:{o.Eligible}:{o.ExcludedReason}:{o.LandedPrice:R}:{o.ScarcityPremium:R}:{o.Tariff:R}:{o.Subsidy:R}:{o.Path.Count}"));

    [Fact]
    public void Markets_are_reported_and_json_safe()
    {
        var model = Playground.Playground.FromScenario(Simulator.Scenarios.Build("G03"));
        var a = Playground.Playground.Analyze(model);
        var ironvale = Assert.Single(a.Markets);
        Assert.Equal("ironvale", ironvale.CityId);
        Assert.Equal(300, ironvale.SpareSupply);
        Assert.Equal(500, a.Cities["carford"].Demand);
        // Serializable with web defaults (an infinite switch price would throw).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        Assert.Contains("\"markets\"", JsonSerializer.Serialize(a, options));

        model.ScarcityEnabled = false;
        Assert.Empty(Playground.Playground.Analyze(model).Markets);
    }

    [Fact]
    public void Empty_map_is_valid()
    {
        var a = Playground.Playground.Analyze(Playground.Playground.Empty());
        Assert.Empty(a.Errors);
        Assert.Empty(a.Cities);
        Assert.True(a.RulesPassed);
    }

    [Fact]
    public void Invalid_input_is_reported_not_thrown()
    {
        var model = Playground.Playground.Empty();
        model.Cities.Add(new PlaygroundCity { Id = "city-1", Name = "One" });
        model.Links.Add(new PlaygroundLink { Id = "link-1", A = "city-1", B = "city-9", Mode = TransportMode.Road, DistanceKm = -5 });
        var a = Playground.Playground.Analyze(model);
        Assert.Contains(a.Errors, e => e.Contains("unknown city city-9"));
        Assert.Contains(a.Errors, e => e.Contains("distanceKm"));
        Assert.Empty(a.Cities);
    }

    [Fact]
    public void Drawn_region_prices_follow_the_map()
    {
        // A small region drawn from scratch, like a player would: a producer, a consumer, a road between them.
        var model = Playground.Playground.Empty();
        model.Cities.Add(new PlaygroundCity { Id = "city-1", Name = "Mill", X = 100, Y = 100, Steel = new PlaygroundSteel { Output = 1000, InputUse = 200, Capacity = 900 } });
        model.Cities.Add(new PlaygroundCity { Id = "city-2", Name = "Town", X = 400, Y = 100, WorldGatewayKm = 50, Steel = new PlaygroundSteel { Output = 0, InputUse = 300, Capacity = 0 } });
        model.Links.Add(new PlaygroundLink { Id = "link-1", A = "city-1", B = "city-2", Mode = TransportMode.Road, DistanceKm = 300, AutoDistance = true });

        var town = Playground.Playground.Analyze(model).Cities["city-2"].Quote!;
        var best = town.Offers[town.SelectedOfferIndex!.Value];
        Assert.Equal("city-1", best.OriginCityId);
        Assert.Equal(132, best.LandedPrice!.Value, 9); // 100 + 2 + 0.1 x 300; world is 128 + 7 = 135

        model.TariffPercent = 0;
        model.Links[0].DistanceKm = 400; // 142 > world 135
        town = Playground.Playground.Analyze(model).Cities["city-2"].Quote!;
        Assert.Equal(OriginKind.World, town.Offers[town.SelectedOfferIndex!.Value].OriginKind);

        model.TariffPercent = 10; // world 147.8 > 142
        town = Playground.Playground.Analyze(model).Cities["city-2"].Quote!;
        Assert.Equal("city-1", town.Offers[town.SelectedOfferIndex!.Value].OriginCityId);
    }

    [Fact]
    public void Model_round_trips_through_the_web_json_shape()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var model = Playground.Playground.FromScenario(Simulator.Scenarios.Build("G09"));
        var json = JsonSerializer.Serialize(model, options);
        Assert.Contains("\"mode\":\"Rail\"", json);
        var back = JsonSerializer.Deserialize<PlaygroundModel>(json, options)!;
        Assert.Equal(JsonSerializer.Serialize(back, options), json);
    }

    private static SimulationRequest Request(string scenario, int periods, bool people = false, params string[] frozen) => new()
    {
        Model = Playground.Playground.FromScenario(Simulator.Scenarios.Build(scenario)), Periods = periods, People = people, Frozen = frozen.ToList(),
    };

    [Fact]
    public void Simulation_runs_every_period_for_every_city_and_is_deterministic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var r = Playground.Playground.Simulate(Request("G03", 30, people: true));
        Assert.Empty(r.Errors);
        Assert.Equal(Enumerable.Range(1, 30), r.Periods);
        foreach (var name in SimulationResult.MetricNames)
            foreach (var city in new[] { "ironvale", "carford" })
                Assert.Equal(30, r.Metrics[name][city].Count);
        Assert.All(r.Metrics["population"]["carford"], v => Assert.NotNull(v));
        Assert.Equal(JsonSerializer.Serialize(r, options), JsonSerializer.Serialize(Playground.Playground.Simulate(Request("G03", 30, people: true)), options));
    }

    [Fact]
    public void Simulation_starts_from_the_prices_the_map_shows()
    {
        var request = Request("corridor", 1);
        var analysis = Playground.Playground.Analyze(request.Model);
        var r = Playground.Playground.Simulate(request);
        foreach (var (city, a) in analysis.Cities)
        {
            var q = a.Quote!;
            if (q.SelectedOfferIndex == null) continue;
            Assert.Equal(q.Offers[q.SelectedOfferIndex.Value].LandedPrice!.Value, r.Metrics["buyTarget"][city][0]!.Value, 9);
        }
    }

    [Fact]
    public void Simulation_without_people_has_no_population_and_frozen_cities_stay_put()
    {
        var r = Playground.Playground.Simulate(Request("corridor", 20, people: false));
        Assert.All(r.Metrics["population"].Values.SelectMany(v => v), v => Assert.Null(v));

        var frozen = Playground.Playground.Simulate(Request("corridor", 20, people: true, "midway"));
        Assert.Empty(frozen.Errors);
        Assert.Single(frozen.Metrics["population"]["midway"].Distinct());
        Assert.NotNull(frozen.Metrics["buy"]["midway"][0]);           // it played period 1, so it has a price to keep
        Assert.Single(frozen.Metrics["buy"]["midway"].Distinct());
        Assert.Single(frozen.Metrics["output"]["midway"].Distinct());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Simulation_rejects_bad_period_counts(int periods)
    {
        Assert.NotEmpty(Playground.Playground.Simulate(Request("G03", periods)).Errors);
    }

    [Fact]
    public void Simulation_reports_an_invalid_map()
    {
        var request = new SimulationRequest { Model = Playground.Playground.Empty(), Periods = 5 };
        request.Model.Links.Add(new PlaygroundLink { Id = "link-1", A = "city-1", B = "city-9", Mode = TransportMode.Road, DistanceKm = 5 });
        Assert.NotEmpty(Playground.Playground.Simulate(request).Errors);
    }

    [Fact]
    public void A_world_price_shock_moves_import_prices_gradually()
    {
        var request = Request("G01", 40);                       // Carford imports at 140
        request.Shock = new SimulationShock { AtPeriod = 5, WorldPricePercent = 50 };
        var r = Playground.Playground.Simulate(request);
        Assert.Empty(r.Errors);
        var buy = r.Metrics["buy"]["carford"].Select(v => v!.Value).ToArray();
        Assert.Equal(buy[0], buy[3], 9);                         // unchanged before the shock
        Assert.True(buy[^1] > buy[3] + 10);
        for (var i = 1; i < buy.Length; i++) Assert.True(Math.Abs(buy[i] - buy[i - 1]) <= 2 + 1e-9, $"period {i + 1}");
    }

    [Fact]
    public void A_capacity_cut_lowers_output_and_bad_shocks_are_rejected()
    {
        var request = Request("G03", 20, people: true);
        request.Shock = new SimulationShock { AtPeriod = 3, CityId = "ironvale", CapacityPercent = -50, UsePercent = 20 };
        var r = Playground.Playground.Simulate(request);
        Assert.Empty(r.Errors);
        var capacity = r.Metrics["capacity"]["ironvale"];
        Assert.True(capacity[2] < capacity[1] * 0.6);

        request.Shock = new SimulationShock { AtPeriod = 30 };
        Assert.NotEmpty(Playground.Playground.Simulate(request).Errors);
        request.Shock = new SimulationShock { AtPeriod = 3, CapacityPercent = -10 };
        Assert.NotEmpty(Playground.Playground.Simulate(request).Errors);
        request.Shock = new SimulationShock { AtPeriod = 3, WorldPricePercent = -150 };
        Assert.NotEmpty(Playground.Playground.Simulate(request).Errors);
    }
}
