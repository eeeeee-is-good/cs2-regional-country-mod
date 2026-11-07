using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>A cheapest path: link ids in traversal order and the summed freight.</summary>
    public sealed class Route
    {
        public Route(double cost, IReadOnlyList<string> linkIds) { Cost = cost; LinkIds = linkIds; }
        public double Cost { get; }
        public IReadOnlyList<string> LinkIds { get; }
    }

    /// <summary>
    /// Directed city graph built from manifest links (log §16.2, D-33, D-36). A link is usable only if:
    /// its status is Proposed or Verified; its mode has a ModeCost; and both endpoints resolve, in their
    /// city's latest snapshot, to a Transport outside connection that lists the link's mode and does not
    /// report carriesCargo=false. Utility connections are never freight (log §18.4 #2).
    /// Freight per link = fixedCost + costPerKm x distanceKm, charged once per link traversed.
    /// </summary>
    public sealed class RouteGraph
    {
        private readonly List<Edge> _edges = new List<Edge>();

        private sealed class Edge
        {
            public Edge(string from, string to, string linkId, TransportMode mode, double cost)
            { From = from; To = to; LinkId = linkId; Mode = mode; Cost = cost; }
            public string From { get; }
            public string To { get; }
            public string LinkId { get; }
            public TransportMode Mode { get; }
            public double Cost { get; }
        }

        public RouteGraph(RegionManifest manifest, IReadOnlyDictionary<string, PublishedSnapshot> latest)
        {
            var costs = manifest.Parameters.ModeCosts.ToDictionary(c => c.Mode);
            foreach (var link in manifest.Links.OrderBy(l => l.LinkId, StringComparer.Ordinal))
            {
                if (!LinkUsable(link, costs, latest)) continue;
                var c = costs[link.Mode];
                var cost = c.FixedCost + c.CostPerKm * link.DistanceKm;
                if (!Num.IsFinite(cost))
                    throw new RegionStateException(new[] { $"links[{link.LinkId}]: freight cost overflows (distance {link.DistanceKm} km)" });

                _edges.Add(new Edge(link.A.CityId, link.B.CityId, link.LinkId, link.Mode, cost));
                if (link.Bidirectional)
                    _edges.Add(new Edge(link.B.CityId, link.A.CityId, link.LinkId, link.Mode, cost));
            }
        }

        /// <summary>The link usability rule described on this class. Shared with InvariantChecker.</summary>
        public static bool LinkUsable(LinkEntry link, IReadOnlyDictionary<TransportMode, ModeCost> costs, IReadOnlyDictionary<string, PublishedSnapshot> latest) =>
            (link.Status == LinkStatus.Proposed || link.Status == LinkStatus.Verified)
            && costs.ContainsKey(link.Mode)
            && EndpointCarries(latest, link.A.CityId, link.A.EndpointKey, link.Mode)
            && EndpointCarries(latest, link.B.CityId, link.B.EndpointKey, link.Mode);

        /// <summary>True if the city's latest snapshot has a Transport connection with this key that lists the mode.</summary>
        public static bool EndpointCarries(IReadOnlyDictionary<string, PublishedSnapshot> latest, string cityId, string endpointKey, TransportMode mode)
        {
            if (!latest.TryGetValue(cityId, out var pub)) return false;
            foreach (var oc in pub.Snapshot.OutsideConnections)
            {
                if (!string.Equals(oc.EndpointKey, endpointKey, StringComparison.Ordinal)) continue;
                return oc.Kind == ConnectionKind.Transport
                    && oc.TransportModes != null && oc.TransportModes.Contains(mode)
                    && oc.CarriesCargo != false;
            }
            return false;
        }

        /// <summary>
        /// Cheapest path from <paramref name="from"/> to <paramref name="to"/> using only the given modes, or null.
        /// Dijkstra over cities (all costs are finite and non-negative, so it terminates on any graph,
        /// including cycles and zero-cost links). Ties are broken by the ordinal link-id sequence, so the
        /// result never depends on input order.
        /// </summary>
        public Route? Cheapest(string from, string to, IReadOnlyCollection<TransportMode> modes)
        {
            var best = new Dictionary<string, Label>(StringComparer.Ordinal) { [from] = new Label(0, new List<string>()) };
            var done = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                string? u = null;
                Label? lu = null;
                foreach (var kv in best)
                {
                    if (done.Contains(kv.Key)) continue;
                    if (lu == null) { u = kv.Key; lu = kv.Value; continue; }
                    var cmp = Compare(kv.Value, lu);
                    if (cmp < 0 || (cmp == 0 && string.CompareOrdinal(kv.Key, u) < 0)) { u = kv.Key; lu = kv.Value; }
                }

                if (u == null || lu == null) return null;
                if (string.Equals(u, to, StringComparison.Ordinal)) return new Route(lu.Cost, lu.Path);
                done.Add(u);

                foreach (var e in _edges)
                {
                    if (!string.Equals(e.From, u, StringComparison.Ordinal) || done.Contains(e.To) || !modes.Contains(e.Mode)) continue;
                    var cost = lu.Cost + e.Cost;
                    if (!Num.IsFinite(cost))
                        throw new RegionStateException(new[] { $"path cost overflows at link {e.LinkId}" });
                    var cand = new Label(cost, new List<string>(lu.Path) { e.LinkId });
                    if (!best.TryGetValue(e.To, out var cur) || Compare(cand, cur) < 0) best[e.To] = cand;
                }
            }
        }

        private sealed class Label
        {
            public Label(double cost, List<string> path) { Cost = cost; Path = path; }
            public double Cost { get; }
            public List<string> Path { get; }
        }

        private static int Compare(Label a, Label b)
        {
            var c = a.Cost.CompareTo(b.Cost);
            if (c != 0) return c;
            for (var i = 0; i < Math.Min(a.Path.Count, b.Path.Count); i++)
            {
                c = string.CompareOrdinal(a.Path[i], b.Path[i]);
                if (c != 0) return c;
            }
            return a.Path.Count.CompareTo(b.Path.Count);
        }
    }
}
