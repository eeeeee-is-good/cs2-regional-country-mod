using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>The engine reproduces Contracts/golden/v1-basic/expected/quote.city-b.Steel.json.</summary>
public class GoldenQuoteTests
{
    [Fact]
    public void Engine_reproduces_golden_quote()
    {
        var actual = ContractJson.ToNode(Fixture.GoldenBasic().Quote());
        var expected = JsonNode.Parse(File.ReadAllText(Repo.Golden("v1-basic", "expected", "quote.city-b.Steel.json")))!;

        // Golden files hold engineVersion "golden" and inputsSha256 null, meaning "not compared".
        Assert.Equal(LandedCostEngine.EngineVersion, actual["provenance"]!["engineVersion"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", actual["provenance"]!["inputsSha256"]!.GetValue<string>());
        expected["provenance"]!["engineVersion"] = actual["provenance"]!["engineVersion"]!.DeepClone();
        expected["provenance"]!["inputsSha256"] = actual["provenance"]!["inputsSha256"]!.DeepClone();

        JsonTolerance.AssertEquivalent(expected, actual);
    }

    [Fact]
    public void Engine_output_is_schema_valid()
    {
        Schemas.AssertValid("quote-result.v1.schema.json", ContractJson.ToNode(Fixture.GoldenBasic().Quote()), "engine quote");
    }

    [Fact]
    public void Golden_quote_reads_as_documented()
    {
        // ARCHITECTURE.md §5: A->B by rail costs 134, beating road (142) and world (140); isolated city C is NoPath.
        var q = Fixture.GoldenBasic().Quote();
        Q.AssertEligible(q.From("city-a"), 134, "ab-rail-01");
        Assert.Equal(300, q.From("city-a").EffectiveRegionalOfferPerGameDay);
        Q.AssertExcluded(q.From("city-c"), ExcludedReason.NoPath);
        Q.AssertEligible(q.World(), 140);
        Assert.Same(q.From("city-a"), q.Selected());
        Assert.Equal(ReportedAs.ModeledPriceQuoteNotPhysicalTrade, q.ReportedAs);
        Assert.False(q.AppliedToGame);
        Assert.Equal("city-b/Steel/m1/r3", q.Provenance.TraceId);
    }
}
