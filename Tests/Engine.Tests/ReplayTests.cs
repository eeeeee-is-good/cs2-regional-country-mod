using Regions.Contracts;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>PROTOTYPE_PLAN acceptance criterion 2: identical inputs give a byte-identical QuoteResult and inputsSha256.</summary>
public class ReplayTests
{
    [Fact]
    public void Replay_is_byte_identical()
    {
        var first = Q.Bytes(Fixture.GoldenBasic().Quote());
        for (var i = 0; i < 5; i++) Assert.Equal(first, Q.Bytes(Fixture.GoldenBasic().Quote()));
    }

    /// <summary>
    /// Criterion 2 across machines: CI runs this on Windows, Linux and macOS, and every OS must produce
    /// exactly these bytes. Update the pins only for an intended change to the engine or its inputs
    /// (an EngineVersion bump changes the quote bytes, a fixture change changes both).
    /// </summary>
    [Fact]
    public void Golden_quote_bytes_are_pinned()
    {
        var q = Fixture.GoldenBasic().Quote();
        // The persisted form (Contracts rule 1: LF). Raw System.Text.Json output in .NET 8 uses the OS newline.
        var fileBytes = Regions.Store.ContractJson.ToBytes(q);
        var quoteSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();
        Assert.True(PinnedGoldenInputsSha256 == q.Provenance.InputsSha256, $"inputsSha256 is {q.Provenance.InputsSha256}");
        Assert.True(PinnedGoldenQuoteSha256 == quoteSha, $"quote file sha256 is {quoteSha}");
    }

    private const string PinnedGoldenInputsSha256 = "75fa2a07ecf5525ae2176616f8257765a23a0f77cac3cc0f5c412ead9f952612";
    private const string PinnedGoldenQuoteSha256 = "e2a35c1f9a6eae64565135acfb6f1e07f2364c7c10c9669c7fcd07ccb8aa48c2";

    [Fact]
    public void Input_order_does_not_change_a_single_byte()
    {
        var baseline = Q.Bytes(Fixture.GoldenBasic().Quote());

        var f = Fixture.GoldenBasic();
        f.Manifest.Cities.Reverse();
        f.Manifest.Links.Reverse();
        f.Manifest.WorldGateways.Reverse();
        f.Manifest.Parameters.ModeCosts.Reverse();
        f.Manifest.Parameters.ResourcePrices.Reverse();
        f.Snapshots.Reverse();
        foreach (var (s, _) in f.Snapshots) { s.Resources.Reverse(); s.OutsideConnections.Reverse(); }
        f.Resources.Reverse();

        Assert.Equal(baseline, Q.Bytes(f.Quote()));
    }

    [Fact]
    public void Wall_clock_field_is_ignored()
    {
        var baseline = Q.Bytes(Fixture.GoldenBasic().Quote());
        var f = Fixture.GoldenBasic();
        f.City("city-a").ExportedAtUtc = "2030-01-01T00:00:00Z";
        Assert.Equal(baseline, Q.Bytes(f.Quote()));
    }

    [Fact]
    public void Any_input_change_changes_the_inputs_hash()
    {
        var baseline = Fixture.GoldenBasic().Quote().Provenance.InputsSha256;
        var mutations = new List<Action<Fixture>>
        {
            f => f.Link("ab-road-01").DistanceKm = 400.0000001,
            f => f.Steel("city-c").Source = "changed",
            f => f.Manifest.Revision = 2,
            f => f.CurrentRegionSeq = 4,
            f => f.City("city-b").OutsideConnections[0].CarriesPassengers = true,
            f => f.SetSteelModes(TransportMode.Road),
        };
        foreach (var mutate in mutations)
        {
            var f = Fixture.GoldenBasic();
            mutate(f);
            Assert.NotEqual(baseline, f.Quote().Provenance.InputsSha256);
        }
    }

    [Fact]
    public void Quoting_does_not_mutate_its_inputs()
    {
        var f = Fixture.GoldenBasic();
        var state = f.State();
        var before = InputHasher.Hash(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" });
        new LandedCostEngine().Quote(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" });
        Assert.Equal(before, InputHasher.Hash(state, new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" }));
    }
}
