using Regions.Contracts;
using Regions.Contracts.Tests;
using Xunit;

namespace Regions.Engine.Tests;

/// <summary>
/// Property tests on seeded random regions (G10, log §18.4 #2, #3, #7). Seeded randomness is fine in tests;
/// a failure prints the seed so the region can be rebuilt exactly.
/// The engine's path is checked against a brute-force enumeration of every simple path.
/// </summary>
public class RandomGraphPropertyTests
{
    private static readonly TransportMode[] LinkModes = { TransportMode.Road, TransportMode.Rail, TransportMode.Ship, TransportMode.Air };
    private static readonly LinkStatus[] Statuses = { LinkStatus.Proposed, LinkStatus.Proposed, LinkStatus.Verified, LinkStatus.Broken, LinkStatus.Unresolved };

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 300).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_region_quotes_are_safe_correct_and_optimal(int seed)
    {
        var rng = new Random(seed);
        var f = RandomRegion(rng);
        var q = f.Quote(dest: "c0");
        var label = $"seed {seed}";

        Schemas.AssertValid("quote-result.v1.schema.json", ContractJson.ToNode(q), label);
        Assert.Equal(Q.Bytes(q), Q.Bytes(f.Quote(dest: "c0")));

        var links = f.Manifest.Links.ToDictionary(l => l.LinkId);
        var costs = f.Manifest.Parameters.ModeCosts.ToDictionary(c => c.Mode);

        foreach (var o in q.Offers)
        {
            if (!o.Eligible) { Assert.Null(o.LandedPrice); continue; }
            foreach (var v in new[] { o.ProducerPrice, o.Freight, o.Tariff, o.Subsidy, o.LandedPrice })
                Assert.True(v is double d && double.IsFinite(d) && d >= 0, $"{label}: bad component {v}");
            Assert.Equal(o.LandedPrice!.Value, o.ProducerPrice!.Value + o.Freight!.Value + o.Tariff!.Value - o.Subsidy!.Value, 9);

            if (o.OriginKind != OriginKind.City) continue;

            // Path is contiguous, respects direction, never revisits a city, and its freight is the sum of its links.
            var at = o.OriginCityId!;
            var visited = new HashSet<string> { at };
            var freight = 0.0;
            foreach (var id in o.Path)
            {
                var l = links[id];
                Assert.True(Usable(l), $"{label}: unusable link {id} on path");
                if (l.A.CityId == at) at = l.B.CityId;
                else { Assert.True(l.Bidirectional && l.B.CityId == at, $"{label}: path breaks at {id}"); at = l.A.CityId; }
                Assert.True(visited.Add(at), $"{label}: path revisits {at}");
                freight += costs[l.Mode].FixedCost + costs[l.Mode].CostPerKm * l.DistanceKm;
            }
            Assert.Equal("c0", at);
            Assert.Equal(freight, o.Freight!.Value, 9);
        }

        // Optimality and NoPath against brute force.
        foreach (var o in q.Offers.Where(o => o.OriginKind == OriginKind.City))
        {
            var best = BruteForceCheapest(f.Manifest, costs, o.OriginCityId!, "c0");
            if (o.Eligible) Assert.True(Math.Abs(best!.Value - o.Freight!.Value) <= 1e-9 * Math.Max(1, best.Value), $"{label}: {o.OriginCityId} freight {o.Freight} but brute force {best}");
            if (o.ExcludedReason == ExcludedReason.NoPath) Assert.Null(best);
            if (best == null) Assert.Equal(ExcludedReason.NoPath, o.ExcludedReason);
        }

        // Selection is the cheapest eligible offer, earliest on ties.
        var eligible = q.Offers.Select((o, i) => (o, i)).Where(x => x.o.Eligible).ToList();
        Assert.Equal(eligible.Count == 0 ? null : eligible.OrderBy(x => x.o.LandedPrice).ThenBy(x => x.i).First().i, q.SelectedOfferIndex);

        // §18.4 #3: adding a valid optional route never makes any offer, or the best offer, dearer.
        var from = $"c{rng.Next(1, f.Manifest.Cities.Count)}";
        f.Manifest.Links.Add(ScenarioTests.RoadLink("zz-extra", from, "oc-road-0", "c0", "oc-road-1", rng.NextDouble() * 500));
        var after = f.Quote(dest: "c0");
        foreach (var o in q.Offers.Where(o => o.Eligible))
        {
            var o2 = o.OriginKind == OriginKind.City ? after.From(o.OriginCityId!) : after.World();
            Assert.True(o2.Eligible && o2.LandedPrice <= o.LandedPrice + 1e-9, $"{label}: offer got worse after adding a route");
        }
        if (q.Selected() is Offer s) Assert.True(after.Selected()!.LandedPrice <= s.LandedPrice + 1e-9, $"{label}: best offer got worse");
    }

    private static bool Usable(LinkEntry l) =>
        (l.Status == LinkStatus.Proposed || l.Status == LinkStatus.Verified) && l.Mode != TransportMode.Air;

    private static double? BruteForceCheapest(RegionManifest m, Dictionary<TransportMode, ModeCost> costs, string from, string to)
    {
        double? best = null;
        void Walk(string at, HashSet<string> seen, double cost)
        {
            if (at == to) { if (best == null || cost < best) best = cost; return; }
            foreach (var l in m.Links.Where(Usable))
            {
                string? next = l.A.CityId == at ? l.B.CityId : l.Bidirectional && l.B.CityId == at ? l.A.CityId : null;
                if (next == null || seen.Contains(next)) continue;
                seen.Add(next);
                Walk(next, seen, cost + costs[l.Mode].FixedCost + costs[l.Mode].CostPerKm * l.DistanceKm);
                seen.Remove(next);
            }
        }
        Walk(from, new HashSet<string> { from }, 0);
        return best;
    }

    /// <summary>
    /// 2-7 cities "c0".."cN". Each city has gates oc-road-0..2, oc-rail-0..2, oc-ship-0..2. Links pick a random mode;
    /// Air links are wired to road gates, so they are never usable (no Air gate exists). Destination c0 may have a world gateway.
    /// </summary>
    private static Fixture RandomRegion(Random rng)
    {
        var f = Fixture.GoldenBasic(); // reuse golden parameters and resource table
        var n = rng.Next(2, 8);
        f.Manifest.Cities = Enumerable.Range(0, n).Select(i => new CityEntry { CityId = $"c{i}", DisplayName = $"City {i}" }).ToList();
        f.Snapshots = Enumerable.Range(0, n).Select(i => (City($"c{i}", rng), i + 1)).ToList();
        f.CurrentRegionSeq = n;
        f.Manifest.Parameters.Staleness.MaxRegionSeqAge = null;

        f.Manifest.Links = new List<LinkEntry>();
        var linkCount = rng.Next(0, 2 * n + 1);
        for (var k = 0; k < linkCount; k++)
        {
            var a = rng.Next(n);
            var b = (a + rng.Next(1, n)) % n;
            var mode = LinkModes[rng.Next(LinkModes.Length)];
            var gate = mode == TransportMode.Air ? "road" : mode.ToString().ToLowerInvariant();
            f.Manifest.Links.Add(new LinkEntry
            {
                LinkId = $"l{k:D2}",
                A = new EndpointRef { CityId = $"c{a}", EndpointKey = $"oc-{gate}-{rng.Next(3)}" },
                B = new EndpointRef { CityId = $"c{b}", EndpointKey = $"oc-{gate}-{rng.Next(3)}" },
                Mode = mode,
                DistanceKm = rng.Next(4) switch { 0 => 0, 1 => 1e6, _ => Math.Round(rng.NextDouble() * 1000, 3) },
                Bidirectional = rng.Next(3) != 0,
                Status = Statuses[rng.Next(Statuses.Length)],
            });
        }

        f.Manifest.WorldGateways = rng.Next(2) == 0
            ? new List<WorldGateway>()
            : new List<WorldGateway> { new() { CityId = "c0", EndpointKey = "oc-road-2", Mode = TransportMode.Road, DistanceKm = rng.Next(0, 300) } };
        return f;
    }

    private static CitySnapshot City(string id, Random rng)
    {
        var s = new CitySnapshot
        {
            CityId = id,
            Seq = 1,
            Producer = new SnapshotProducer { Tool = "CitySimulator", ToolVersion = "property-test" },
            Resources =
            {
                new ResourceObservation
                {
                    ResourceId = "Steel",
                    RealizedOutputPerGameDay = rng.Next(0, 2000),
                    InputUsePerGameDay = rng.Next(0, 1000),
                    CapacityEstimatePerGameDay = rng.Next(3) == 0 ? null : rng.Next(0, 2000),
                    Confidence = rng.Next(4) == 0 ? Confidence.Provisional : Confidence.Validated,
                    Source = "synthetic",
                },
            },
        };
        foreach (var mode in new[] { TransportMode.Road, TransportMode.Rail, TransportMode.Ship })
            for (var k = 0; k < 3; k++)
                s.OutsideConnections.Add(new OutsideConnectionInfo
                {
                    EndpointKey = $"oc-{mode.ToString().ToLowerInvariant()}-{k}",
                    Kind = ConnectionKind.Transport,
                    TransportModes = { mode },
                    CarriesCargo = true,
                });
        return s;
    }
}
