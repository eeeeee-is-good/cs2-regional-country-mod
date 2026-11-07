using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Xunit;

namespace Regions.ScenarioTests;

public class InvariantCheckerTests
{
    private static RegionState Golden() => Scenarios.Build("G03").ToState();

    [Fact]
    public void Real_engine_passes_on_every_city_and_resource()
    {
        var report = new LandedCostEngine().Check(Golden());
        Assert.True(report.Passed, string.Join("\n", report.Violations));
        Assert.Equal(2, report.QuotesChecked); // 2 destinations x 1 resource
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 100).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Real_engine_passes_on_random_regions(int seed)
    {
        var report = new LandedCostEngine().Check(RandomRegion(seed).ToState());
        Assert.True(report.Passed, $"seed {seed}:\n" + string.Join("\n", report.Violations));
    }

    [Fact]
    public void Invalid_input_is_reported_not_thrown()
    {
        var region = Scenarios.Build("G03");
        region.Manifest.Links[0].DistanceKm = double.NaN;
        var report = new LandedCostEngine().Check(region.ToState());
        Assert.False(report.Passed);
        Assert.All(report.Violations, v => Assert.Equal(InvariantChecker.NumericSafety, v.Invariant));
    }

    // ---- The checker catches deliberately broken engines ----

    [Fact]
    public void Detects_quote_claiming_to_be_applied_to_the_game() =>
        AssertCatches(InvariantChecker.EvidenceLabeling, (_, q) => q.AppliedToGame = true);

    [Fact]
    public void Detects_components_that_do_not_sum() =>
        AssertCatches(InvariantChecker.NumericSafety, (_, q) => { foreach (var o in q.Offers.Where(o => o.Eligible)) o.LandedPrice += 1; });

    [Fact]
    public void Detects_negative_prices() =>
        AssertCatches(InvariantChecker.NumericSafety, (_, q) => { foreach (var o in q.Offers.Where(o => o.Eligible)) { o.Freight = -o.Freight; o.LandedPrice = o.ProducerPrice + o.Freight; } });

    [Fact]
    public void Detects_wrong_selection() =>
        AssertCatches(InvariantChecker.Selection, (_, q) => { if (q.Offers.Count > 1) q.SelectedOfferIndex = q.Offers.Count - 1; });

    [Fact]
    public void Detects_freight_that_does_not_match_the_path() =>
        AssertCatches(InvariantChecker.PathCorrectness, (_, q) =>
        {
            foreach (var o in q.Offers.Where(o => o.Eligible && o.OriginKind == OriginKind.City))
                o.Path = o.Path.Select(id => id == "ic-rail-01" ? "ic-road-01" : id).ToList();
        });

    [Fact]
    public void Detects_path_through_a_link_that_does_not_exist() =>
        AssertCatches(InvariantChecker.PathCorrectness, (_, q) =>
        {
            foreach (var o in q.Offers.Where(o => o.Eligible && o.OriginKind == OriginKind.City)) o.Path = new List<string> { "teleport" };
        });

    [Fact]
    public void Detects_unreachable_supplier_leaking_into_prices() =>
        // A buggy engine where a big but unreachable supplier pulls the world price down (a "phantom global market").
        AssertCatches(InvariantChecker.GraphIsolation, (state, q) =>
        {
            var hugeSupply = state.LatestSnapshots.Any(p => p.Snapshot.Resources.Any(r => r.RealizedOutputPerGameDay > 20000));
            if (!hugeSupply) return;
            foreach (var o in q.Offers.Where(o => o.Eligible && o.OriginKind == OriginKind.World)) { o.ProducerPrice -= 1; o.LandedPrice -= 1; }
        }, Scenarios.Build("corridor").ToState());

    [Fact]
    public void Detects_prices_falling_when_a_route_is_removed() =>
        AssertCatches(InvariantChecker.BestFeasibleOffer, (state, q) =>
        {
            if (state.Manifest.Links.Count >= 2) return;
            foreach (var o in q.Offers.Where(o => o.Eligible)) { o.ProducerPrice -= 50; o.LandedPrice -= 50; }
        });

    private static void AssertCatches(string invariant, Action<RegionState, QuoteResult> corrupt, RegionState? state = null)
    {
        var report = new BrokenEngine(corrupt).Check(state ?? Golden());
        Assert.Contains(report.Violations, v => v.Invariant == invariant);
    }

    private sealed class BrokenEngine : IRegionalEngine
    {
        private readonly Action<RegionState, QuoteResult> _corrupt;
        private readonly LandedCostEngine _real = new();
        public BrokenEngine(Action<RegionState, QuoteResult> corrupt) => _corrupt = corrupt;

        public QuoteResult Quote(RegionState state, QuoteRequest request)
        {
            var q = _real.Quote(state, request);
            _corrupt(state, q);
            return q;
        }

        public InvariantReport Check(RegionState state) => new InvariantChecker(this).Check(state);
    }

    /// <summary>Seeded random region built from SyntheticCity: 2-6 cities, random road/rail/ship/air links, optional world gateway.</summary>
    internal static RegionScenario RandomRegion(int seed)
    {
        var rng = new Random(seed);
        var n = rng.Next(2, 7);
        var region = new RegionScenario($"random-{seed}", "random");
        var modes = new[] { TransportMode.Road, TransportMode.Rail, TransportMode.Ship, TransportMode.Air };
        var statuses = new[] { LinkStatus.Proposed, LinkStatus.Verified, LinkStatus.Broken, LinkStatus.Unresolved };

        for (var i = 0; i < n; i++)
        {
            var id = $"c{i}";
            region.City(id, $"City {i}");
            var city = new SyntheticCity(id).Gate("oc-road", TransportMode.Road).Gate("oc-rail", TransportMode.Rail).Gate("oc-ship", TransportMode.Ship);
            if (rng.Next(3) != 0)
                city.Resource("Steel", rng.Next(0, 2000), rng.Next(0, 1000), rng.Next(4) == 0 ? null : rng.Next(0, 2000),
                              rng.Next(4) == 0 ? Confidence.Provisional : Confidence.Validated);
            region.Publish(city.Capture());
        }

        var linkCount = rng.Next(0, 2 * n + 1);
        for (var k = 0; k < linkCount; k++)
        {
            var a = rng.Next(n);
            var b = (a + rng.Next(1, n)) % n;
            var mode = modes[rng.Next(modes.Length)];
            var gate = mode == TransportMode.Air ? "oc-road" : "oc-" + mode.ToString().ToLowerInvariant();
            region.Link($"l{k:D2}", $"c{a}", gate, $"c{b}", gate, mode,
                        rng.Next(4) == 0 ? 0 : Math.Round(rng.NextDouble() * 800, 2),
                        bidirectional: rng.Next(3) != 0, status: statuses[rng.Next(statuses.Length)]);
        }
        for (var i = 0; i < n; i++)
            if (rng.Next(3) == 0) region.WorldGateway($"c{i}", "oc-road", TransportMode.Road, rng.Next(0, 300));
        return region;
    }
}
