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
}
