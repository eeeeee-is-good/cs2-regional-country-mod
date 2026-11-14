using System.Text.Json;
using Regions.Contracts.Tests;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>Phase 3: the exposure report describes what an unloaded city will meet, and is never an input.</summary>
public class ExposureTests
{
    private static readonly LandedCostEngine Engine = new();
    private static readonly SignalRule Rule = new();

    private static (Fixture, SignalState) CarfordAwayDuringAShock(int periodsAway)
    {
        var f = Fixture.GoldenBasic();
        var s = SignalPlanner.Step(null, f.State(), Engine, Rule);      // Carford publishes; pays 134
        f.Link("ab-rail-01").DistanceKm = 5000;                         // cheap supply cut: target becomes the world 140
        f.Link("ab-road-01").DistanceKm = 5000;
        var active = new HashSet<string> { "city-a", "city-c" };
        for (var i = 0; i < periodsAway; i++) s = SignalPlanner.Step(s, f.State(), Engine, Rule, active);
        return (f, s);
    }

    [Fact]
    public void Describes_what_a_returning_city_will_meet()
    {
        var (f, s) = CarfordAwayDuringAShock(5);
        var b = ExposureReport.Build(f.State(), s, Rule).Single(e => e.CityId == "city-b");
        Assert.Equal("Forecast", b.ReportedAs);
        Assert.Equal((134.0, 134.0, 140.0), (b.PriceAtLastPublish!.Value, b.AppliedNow!.Value, b.TargetNow!.Value));
        Assert.Equal(6, b.ChangeSincePublish!.Value, 9);
        Assert.Equal(6.0 / 134 * 100, b.ChangePercent!.Value, 9);
        Assert.Equal(3, b.PeriodsToConverge);                           // 6 at 2 per period
        Assert.Equal("buying costs 4.5% more than when it last published; about 3 periods to adjust once loaded", b.Summary);
        Assert.Equal(1, b.SnapshotAge);
    }

    [Fact]
    public void Settled_and_unreached_cities_are_described()
    {
        var f = Fixture.GoldenBasic();
        var s = SignalPlanner.Step(null, f.State(), Engine, Rule);
        var report = ExposureReport.Build(f.State(), s, Rule);
        Assert.Equal("buying costs about the same as when it last published; nothing to catch up", report.Single(e => e.CityId == "city-b").Summary);
        Assert.Equal("no supply reaches this city at the moment", report.Single(e => e.CityId == "city-a").Summary);
    }

    [Fact]
    public void Building_the_report_changes_nothing()
    {
        var (f, s) = CarfordAwayDuringAShock(3);
        var quote = Q.Bytes(f.Quote());
        var next = JsonSerializer.Serialize(SignalPlanner.Step(s, f.State(), Engine, Rule), ContractJson.Options);
        for (var i = 0; i < 3; i++) ExposureReport.Build(f.State(), s, Rule);
        Assert.Equal(quote, Q.Bytes(f.Quote()));
        Assert.Equal(next, JsonSerializer.Serialize(SignalPlanner.Step(s, f.State(), Engine, Rule), ContractJson.Options));
    }
}
