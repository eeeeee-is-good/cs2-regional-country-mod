using System.Globalization;
using System.Text;
using Regions.Contracts;
using Regions.Engine;

namespace Regions.ScenarioRunner;

/// <summary>Human-readable "Why this price?" output (log §18.7). Deterministic: no clock, invariant culture.</summary>
public static class TextReport
{
    public static string Quote(QuoteResult q, RegionState state)
    {
        var manifest = state.Manifest;
        var names = manifest.Cities.ToDictionary(c => c.CityId, c => string.IsNullOrEmpty(c.DisplayName) ? c.CityId : c.DisplayName);
        string SupplyNotes(Offer o)
        {
            var obs = state.LatestSnapshots.FirstOrDefault(p => p.Snapshot.CityId == o.OriginCityId)?
                .Snapshot.Resources.FirstOrDefault(r => r.ResourceId == q.ResourceId);
            var notes = obs == null ? new List<string>() : SupplyRule.Evaluate(obs).Notes;
            return notes.Count == 0 ? "" : " (" + string.Join(", ", notes) + ")";
        }
        string Name(string? id) => id != null && names.TryGetValue(id, out var n) ? n : id ?? "?";

        var sb = new StringBuilder();
        sb.Append(q.ResourceId).Append(" delivered to ").Append(Name(q.DestCityId)).Append(" [").Append(q.DestCityId).Append("]\n");
        sb.Append("  Modeled price quote, not a physical trade. Applied to game: ").Append(q.AppliedToGame ? "YES" : "no").Append(".\n");
        sb.Append("  trace ").Append(q.Provenance.TraceId).Append(", engine ").Append(q.Provenance.EngineVersion)
          .Append(", inputs sha256 ").Append(q.Provenance.InputsSha256 ?? "-").Append('\n');

        if (q.Offers.Count == 0)
        {
            sb.Append("  No offers: no other city reports this resource and the city has no world gateway.\n");
            return sb.ToString();
        }

        var rows = new List<string[]> { new[] { "", "#", "Origin", "Route", "Producer", "Freight", "Tariff", "Subsidy", "Landed", "Result" } };
        for (var i = 0; i < q.Offers.Count; i++)
        {
            var o = q.Offers[i];
            var origin = o.OriginKind == OriginKind.City ? Name(o.OriginCityId) : "World via " + o.WorldGatewayEndpointKey;
            var route = o.OriginKind == OriginKind.World ? "(world gateway)" : o.Path.Count == 0 ? "-" : string.Join(" > ", o.Path);
            rows.Add(new[]
            {
                q.SelectedOfferIndex == i ? ">" : "", i.ToString(CultureInfo.InvariantCulture), origin, route,
                N(o.ProducerPrice), N(o.Freight), N(o.Tariff), N(o.Subsidy), N(o.LandedPrice),
                Result(o, q.ResourceId) + (o.OriginKind == OriginKind.City && (o.Eligible || o.ExcludedReason is ExcludedReason.NoSupply or ExcludedReason.UnknownSupply) ? SupplyNotes(o) : ""),
            });
        }
        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var numeric = new HashSet<int> { 1, 4, 5, 6, 7, 8 };
        foreach (var r in rows)
        {
            sb.Append("  ");
            for (var c = 0; c < r.Length; c++)
            {
                var cell = c == r.Length - 1 ? r[c] : numeric.Contains(c) ? r[c].PadLeft(widths[c]) : r[c].PadRight(widths[c]);
                sb.Append(cell).Append(c == r.Length - 1 ? "" : "  ");
            }
            sb.Append('\n');
        }

        if (q.SelectedOfferIndex is int s)
        {
            var o = q.Offers[s];
            var who = o.OriginKind == OriginKind.City ? Name(o.OriginCityId) : "the world market via " + o.WorldGatewayEndpointKey;
            sb.Append("  Cheapest: #").Append(s).Append(' ').Append(who).Append(" at ").Append(N(o.LandedPrice)).Append('\n');
        }
        else
        {
            sb.Append("  Cheapest: none. No offer is eligible.\n");
        }
        return sb.ToString();
    }

    public static string Check(InvariantReport report, RegionManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("Checked ").Append(report.QuotesChecked).Append(" quotes in region ").Append(manifest.RegionId)
          .Append(" against log §18.4 invariants #1, #2, #3, #7, #14.\n");
        if (report.Passed) sb.Append("All invariants hold.\n");
        else
        {
            sb.Append(report.Violations.Count).Append(" violation(s):\n");
            foreach (var v in report.Violations) sb.Append("  ").Append(v).Append('\n');
        }
        return sb.ToString();
    }

    private static string Result(Offer o, string resourceId)
    {
        if (o.Eligible)
            return o.EffectiveRegionalOfferPerGameDay is double e ? $"eligible, offers {N(e)}/day" : "eligible";
        return o.ExcludedReason switch
        {
            ExcludedReason.NoPath => "excluded: no usable route",
            ExcludedReason.ModeIncompatible => $"excluded: routes exist, but none in a mode allowed for {resourceId}",
            ExcludedReason.StaleSnapshot => "excluded: supplier snapshot too old",
            ExcludedReason.UnknownSupply => "excluded: supply unknown",
            ExcludedReason.NoSupply => $"excluded: no spare supply ({N(o.EffectiveRegionalOfferPerGameDay)}/day)",
            ExcludedReason.NoWorldGateway => "excluded: gateway not usable",
            ExcludedReason.UnknownResource => "excluded: unknown resource or no price set",
            _ => "excluded: " + o.ExcludedReason,
        };
    }

    private static string N(double? d)
    {
        if (d is not double v) return "-";
        return Math.Abs(v) >= 1e9 ? v.ToString("0.###E+0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
