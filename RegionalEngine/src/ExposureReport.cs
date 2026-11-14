using System;
using System.Collections.Generic;
using System.Linq;

namespace Regions.Engine
{
    /// <summary>One city's forecast exposure to regional price changes since it last published.</summary>
    public sealed class CityExposure
    {
        public string CityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        /// <summary>Region ingestions since the city's snapshot was accepted.</summary>
        public int SnapshotAge { get; set; }
        /// <summary>D1 weight of that snapshot (1 fresh ... 0 stale).</summary>
        public double Freshness { get; set; }
        /// <summary>The applied buy price when its current snapshot was first seen.</summary>
        public double? PriceAtLastPublish { get; set; }
        public double? AppliedNow { get; set; }
        public double? TargetNow { get; set; }
        /// <summary>TargetNow - PriceAtLastPublish: how much the region moved while the city was away.</summary>
        public double? ChangeSincePublish { get; set; }
        public double? ChangePercent { get; set; }
        /// <summary>Periods an active city needs to close the gap from AppliedNow to TargetNow at the current step rule.</summary>
        public int? PeriodsToConverge { get; set; }
        public string Summary { get; set; } = "";
        /// <summary>Always "Forecast": this report is never an input to quotes, supply, demand or other cities (§3.3).</summary>
        public string ReportedAs => "Forecast";
    }

    /// <summary>
    /// Phase 3 (docs/ECONOMIC_MODEL_PLAN.md §3.3). Freeze-and-converge (§8A) means an unloaded city experiences nothing
    /// while away; this report only *describes* what it will meet when it returns. It is computed from the quotes and the
    /// applied-signal state, and nothing reads it back: it never changes a snapshot, a quote, a signal or another city.
    /// It is not an estimate of businesses that closed or opened: that would be counted again when CS2 really closes
    /// them after load.
    /// </summary>
    public static class ExposureReport
    {
        public static IReadOnlyList<CityExposure> Build(RegionState state, SignalState signals, SignalRule rule)
        {
            var latest = state.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId, StringComparer.Ordinal);
            var maxAge = state.Manifest.Parameters.Staleness.MaxRegionSeqAge;
            var result = new List<CityExposure>();
            foreach (var s in signals.Signals.OrderBy(x => x.CityId, StringComparer.Ordinal).ThenBy(x => x.ResourceId, StringComparer.Ordinal))
            {
                var age = latest.TryGetValue(s.CityId, out var pub) ? state.CurrentRegionSeq - pub.RegionSeq : 0;
                var basePrice = state.Manifest.Parameters.ResourcePrices.FirstOrDefault(p => p.ResourceId == s.ResourceId)?.RegionalProducerPriceIndex ?? 0;
                var maxStep = rule.MaxStepFraction * basePrice;
                var e = new CityExposure
                {
                    CityId = s.CityId, ResourceId = s.ResourceId, SnapshotAge = age, Freshness = Freshness.Weight(age, maxAge),
                    PriceAtLastPublish = s.ReferenceBuyPrice, AppliedNow = s.Buy.Applied, TargetNow = s.Buy.Target,
                };
                if (e.TargetNow is double t && e.PriceAtLastPublish is double r)
                {
                    e.ChangeSincePublish = t - r;
                    e.ChangePercent = r > 0 ? (t - r) / r * 100 : (double?)null;
                }
                if (e.TargetNow is double target && e.AppliedNow is double applied)
                    e.PeriodsToConverge = maxStep > 0 ? (int)Math.Ceiling(Math.Abs(target - applied) / maxStep - 1e-9) : (target == applied ? 0 : (int?)null);

                e.Summary = Describe(e);
                result.Add(e);
            }
            return result;
        }

        private static string Describe(CityExposure e)
        {
            if (e.TargetNow == null) return "no supply reaches this city at the moment";
            if (e.ChangePercent is not double pct) return "no earlier price to compare with";
            var change = Math.Abs(pct) < 0.5 ? "buying costs about the same as"
                : FormattableString.Invariant($"buying costs {Math.Abs(pct):0.#}% {(pct > 0 ? "more" : "less")} than");
            var converge = e.PeriodsToConverge switch
            {
                null => "",
                0 => "; nothing to catch up",
                1 => "; about 1 period to adjust once loaded",
                var n => FormattableString.Invariant($"; about {n} periods to adjust once loaded"),
            };
            return $"{change} when it last published{converge}";
        }
    }
}
