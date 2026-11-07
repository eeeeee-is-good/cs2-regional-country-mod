using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>One policy's forecast. Every figure is a modeled estimate, never cash (log §17.7, D-46).</summary>
    public sealed class PolicyForecast
    {
        public PolicyForecast(string policyId, string kind, string resourceId, TransportMode? mode,
                              double? exposurePerGameDay, double? budgetPerGameDay, double? scale, double? forecastSpendPerGameDay, string note)
        {
            PolicyId = policyId; Kind = kind; ResourceId = resourceId; Mode = mode;
            ExposurePerGameDay = exposurePerGameDay; BudgetPerGameDay = budgetPerGameDay; Scale = scale;
            ForecastSpendPerGameDay = forecastSpendPerGameDay; Note = note;
        }

        public string PolicyId { get; }
        /// <summary>"ImportTariff" or "FreightSubsidy".</summary>
        public string Kind { get; }
        public string ResourceId { get; }
        public TransportMode? Mode { get; }
        /// <summary>Subsidies: the unscaled cost if every eligible supplier used it at its best rate. null for tariffs.</summary>
        public double? ExposurePerGameDay { get; }
        public double? BudgetPerGameDay { get; }
        /// <summary>Subsidies: the factor (0..1) applied to the subsidy so the forecast stays within budget.</summary>
        public double? Scale { get; }
        /// <summary>Subsidies: min(exposure, budget). Tariffs: null, because import volumes are not tracked (D-42).</summary>
        public double? ForecastSpendPerGameDay { get; }
        public string Note { get; }
    }

    /// <summary>
    /// Federal landed-cost levers, following the log §17.4 stacking order:
    /// route eligibility and freight first, then customs (import tariffs on World offers only, D-44),
    /// then logistics support (freight subsidies on qualifying domestic links only, within budget, D-43).
    ///
    /// Route choice uses unsubsidized freight: a subsidy lowers the price on the path that is physically cheapest;
    /// it never makes the engine pick a different path. Budget scaling is region-wide and deterministic:
    ///   exposure(policy) = sum over supplier cities s (sorted) of effectiveOffer(s) x max over destinations d of subsidyPerUnit(s -> d)
    ///   scale = exposure &lt;= budget ? 1 : budget / exposure
    /// so the forecast spend never exceeds the budget, and a zero budget stops the subsidy (G09).
    /// Each supplier's offer is counted once, at its best rate: an upper bound, because a surplus cannot be sold twice.
    /// </summary>
    public static class PolicyEvaluator
    {
        public static double TariffPerUnit(ImportTariff t, double worldProducerPrice) =>
            worldProducerPrice * t.AdValoremPercent / 100.0 + t.SpecificPerUnit;

        /// <summary>Unscaled subsidy per unit on a route: the share of freight on qualifying links.</summary>
        public static double SubsidyPerUnit(FreightSubsidy s, IReadOnlyList<string> path,
                                            IReadOnlyDictionary<string, LinkEntry> links, IReadOnlyDictionary<TransportMode, ModeCost> costs)
        {
            var qualifying = 0.0;
            foreach (var id in path)
            {
                var l = links[id];
                if (l.Mode != s.Mode) continue;
                if (s.LinkIds != null && !s.LinkIds.Contains(id, StringComparer.Ordinal)) continue;
                var c = costs[l.Mode];
                qualifying += c.FixedCost + c.CostPerKm * l.DistanceKm;
            }
            return qualifying * s.PercentOfFreight / 100.0;
        }

        internal static IEnumerable<ImportTariff> Tariffs(RegionManifest m, string resourceId) =>
            (m.Parameters.Policies?.ImportTariffs ?? new List<ImportTariff>())
                .Where(t => string.Equals(t.ResourceId, resourceId, StringComparison.Ordinal))
                .OrderBy(t => t.PolicyId, StringComparer.Ordinal);

        internal static IEnumerable<FreightSubsidy> Subsidies(RegionManifest m, string resourceId) =>
            (m.Parameters.Policies?.FreightSubsidies ?? new List<FreightSubsidy>())
                .Where(s => string.Equals(s.ResourceId, resourceId, StringComparison.Ordinal))
                .OrderBy(s => s.PolicyId, StringComparer.Ordinal);

        /// <summary>Forecast for one subsidy: exposure, scale and spend.</summary>
        internal static PolicyForecast Forecast(FreightSubsidy s, RegionState state, RouteGraph graph,
                                                IReadOnlyDictionary<string, PublishedSnapshot> latest)
        {
            var m = state.Manifest;
            var resource = state.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, s.ResourceId, StringComparison.Ordinal));
            var price = m.Parameters.ResourcePrices.FirstOrDefault(r => string.Equals(r.ResourceId, s.ResourceId, StringComparison.Ordinal));
            if (resource == null || price == null)
                return new PolicyForecast(s.PolicyId, "FreightSubsidy", s.ResourceId, s.Mode, 0, s.BudgetPerGameDay, 1, 0,
                                          "unknown resource or no price: never applies");

            var links = m.Links.ToDictionary(l => l.LinkId, StringComparer.Ordinal);
            var costs = m.Parameters.ModeCosts.ToDictionary(c => c.Mode);
            var maxAge = m.Parameters.Staleness.MaxRegionSeqAge;
            var cityIds = m.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var exposure = 0.0;
            foreach (var supplier in cityIds)
            {
                if (!latest.TryGetValue(supplier, out var pub)) continue;
                if (maxAge != null && state.CurrentRegionSeq - pub.RegionSeq > maxAge.Value) continue;
                var obs = pub.Snapshot.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, s.ResourceId, StringComparison.Ordinal));
                if (obs == null) continue;
                var supply = SupplyRule.Evaluate(obs);
                if (supply.ExcludedReason != null || supply.EffectiveOfferPerGameDay is not double effective) continue;

                var best = 0.0;
                foreach (var dest in cityIds)
                {
                    if (string.Equals(dest, supplier, StringComparison.Ordinal)) continue;
                    var route = graph.Cheapest(supplier, dest, resource.AllowedFreightModes);
                    if (route == null) continue;
                    best = Math.Max(best, SubsidyPerUnit(s, route.LinkIds, links, costs));
                }
                exposure += effective * best;
            }

            if (!Num.IsFinite(exposure))
                throw new RegionStateException(new[] { $"policies[{s.PolicyId}]: subsidy exposure overflows" });
            var scale = exposure <= s.BudgetPerGameDay ? 1.0 : s.BudgetPerGameDay / exposure;
            var note = scale < 1 ? "budget too small for the forecast exposure: subsidy scaled down" : "within budget";
            return new PolicyForecast(s.PolicyId, "FreightSubsidy", s.ResourceId, s.Mode, exposure, s.BudgetPerGameDay, scale,
                                      Math.Min(exposure, s.BudgetPerGameDay), note);
        }

        internal static PolicyForecast Forecast(ImportTariff t) =>
            new PolicyForecast(t.PolicyId, "ImportTariff", t.ResourceId, null, null, null, null, null,
                               "revenue not estimated: import volumes are not tracked (D-42)");
    }
}
