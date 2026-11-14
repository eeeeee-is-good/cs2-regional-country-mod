using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>D3: how fast an applied price may move.</summary>
    public sealed class SignalRule
    {
        /// <summary>Largest move per period, as a fraction of the resource's regional producer price (0.02 = 2%).</summary>
        public double MaxStepFraction { get; set; } = 0.02;
    }

    public enum SignalStatus
    {
        /// <summary>First period with a target: the applied price starts at the target (nothing was applied before).</summary>
        New,
        /// <summary>Applied equals target.</summary>
        Settled,
        /// <summary>Applied is stepping toward the target.</summary>
        Moving,
        /// <summary>The city is not active (unloaded): its applied price is frozen (§8A freeze-and-converge).</summary>
        Frozen,
        /// <summary>No target this period (nothing reaches the city, or it does not sell): the last applied price is kept.</summary>
        NoTarget,
    }

    /// <summary>One price channel: the instantaneous target from the engine and the gradual applied price.</summary>
    public sealed class PriceSignal
    {
        public double? Target { get; set; }
        public double? Applied { get; set; }
        public SignalStatus Status { get; set; }
    }

    /// <summary>The signals for one (city, resource).</summary>
    public sealed class CitySignal
    {
        public string CityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        /// <summary>What the city pays to buy: the landed price of its selected offer.</summary>
        public PriceSignal Buy { get; set; } = new PriceSignal();
        /// <summary>What a producing city sells at, at its gate: base price plus any scarcity premium. Null target for non-producers.</summary>
        public PriceSignal Sell { get; set; } = new PriceSignal();
        /// <summary>RegionSeq of the city's snapshot the last time it published (Phase 3 exposure report).</summary>
        public int? ReferenceRegionSeq { get; set; }
        /// <summary>The applied buy price in the period when that snapshot was first seen (Phase 3 exposure report).</summary>
        public double? ReferenceBuyPrice { get; set; }
    }

    /// <summary>Applied price state of a region. Kept in the region store, never in a save.</summary>
    public sealed class SignalState
    {
        public int Period { get; set; }
        /// <summary>Sorted by (CityId, ResourceId) ordinal.</summary>
        public List<CitySignal> Signals { get; set; } = new List<CitySignal>();
    }

    /// <summary>
    /// Phase 1 (docs/ECONOMIC_MODEL_PLAN.md §3.1): the quote is the instantaneous *target*; the *applied* price follows
    /// it at a bounded rate, one explicit region period at a time (D4: never tied to a city clock):
    ///
    ///   applied(t+1) = applied(t) + clamp(target(t+1) - applied(t), +-maxStep),  maxStep = MaxStepFraction x producer price
    ///
    /// Only active (loaded) cities move. An inactive city keeps its applied price and, when active again, converges from
    /// there: no backlog and no replay (§8A). Pure and deterministic: the same previous state and region give the same
    /// next state. The quote itself is untouched, so replay of quotes stays byte-identical.
    /// </summary>
    public static class SignalPlanner
    {
        /// <param name="active">Cities that are loaded this period; null means all.</param>
        public static SignalState Step(SignalState? previous, RegionState state, LandedCostEngine engine, SignalRule rule, ISet<string>? active = null)
        {
            if (!Num.IsFiniteNonNegative(rule.MaxStepFraction)) throw new ArgumentException("MaxStepFraction must be finite and >= 0", nameof(rule));
            var m = state.Manifest;
            var prev = (previous?.Signals ?? new List<CitySignal>())
                .ToDictionary(s => s.CityId + "\u0000" + s.ResourceId, StringComparer.Ordinal);
            var latest = state.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId, StringComparer.Ordinal);
            var next = new SignalState { Period = (previous?.Period ?? 0) + 1 };

            var resources = m.Parameters.ResourcePrices.Select(r => r.ResourceId)
                .Concat(state.LatestSnapshots.SelectMany(p => p.Snapshot.Resources.Select(r => r.ResourceId)))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            foreach (var resourceId in resources)
            {
                var price = m.Parameters.ResourcePrices.FirstOrDefault(p => p.ResourceId == resourceId);
                var basePrice = price?.RegionalProducerPriceIndex ?? 0;
                var maxStep = rule.MaxStepFraction * basePrice;
                var markets = engine.Markets(state, resourceId).ToDictionary(x => x.CityId, StringComparer.Ordinal);

                foreach (var city in m.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var q = engine.Quote(state, new QuoteRequest { DestCityId = city, ResourceId = resourceId });
                    double? buyTarget = q.SelectedOfferIndex is int i ? q.Offers[i].LandedPrice : null;
                    double? sellTarget = markets.TryGetValue(city, out var market) ? market.GatePrice
                        : price != null && IsSurplusSupplier(state, latest, city, resourceId) ? basePrice : (double?)null;

                    prev.TryGetValue(city + "\u0000" + resourceId, out var before);
                    var isActive = active == null || active.Contains(city);
                    var signal = new CitySignal
                    {
                        CityId = city,
                        ResourceId = resourceId,
                        Buy = Move(before?.Buy, buyTarget, maxStep, isActive),
                        Sell = Move(before?.Sell, sellTarget, maxStep, isActive),
                        ReferenceRegionSeq = before?.ReferenceRegionSeq,
                        ReferenceBuyPrice = before?.ReferenceBuyPrice,
                    };

                    // Remember the price the city faced when its current snapshot was first seen (exposure report baseline).
                    var seq = latest.TryGetValue(city, out var pub) ? pub.RegionSeq : (int?)null;
                    if (seq != signal.ReferenceRegionSeq)
                    {
                        signal.ReferenceRegionSeq = seq;
                        signal.ReferenceBuyPrice = signal.Buy.Applied;
                    }
                    next.Signals.Add(signal);
                }
            }
            return next;
        }

        private static bool IsSurplusSupplier(RegionState state, IReadOnlyDictionary<string, PublishedSnapshot> latest, string city, string resourceId)
        {
            if (!latest.TryGetValue(city, out var pub)) return false;
            var obs = pub.Snapshot.Resources.FirstOrDefault(r => r.ResourceId == resourceId);
            if (obs == null) return false;
            var supply = SupplyRule.Evaluate(obs, Freshness.Weight(state, pub));
            return supply.ExcludedReason == null;
        }

        private static PriceSignal Move(PriceSignal? before, double? target, double maxStep, bool active)
        {
            var previous = before?.Applied;
            if (!active)
                return new PriceSignal { Target = target, Applied = previous, Status = SignalStatus.Frozen };
            if (target == null)
                return new PriceSignal { Target = null, Applied = previous, Status = SignalStatus.NoTarget };
            if (previous == null)
                return new PriceSignal { Target = target, Applied = target, Status = SignalStatus.New };

            var gap = target.Value - previous.Value;
            var step = Math.Max(-maxStep, Math.Min(maxStep, gap));
            var applied = Math.Abs(gap) <= maxStep ? target.Value : previous.Value + step;
            return new PriceSignal { Target = target, Applied = applied, Status = applied == target.Value ? SignalStatus.Settled : SignalStatus.Moving };
        }
    }
}
