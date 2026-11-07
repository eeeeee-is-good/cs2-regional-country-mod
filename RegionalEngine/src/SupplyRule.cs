using System;
using System.Collections.Generic;
using Regions.Contracts;

namespace Regions.Engine
{
    public sealed class SupplyAssessment
    {
        public SupplyAssessment(ExcludedReason? excludedReason, double? effectiveOfferPerGameDay, IReadOnlyList<string> notes)
        {
            ExcludedReason = excludedReason;
            EffectiveOfferPerGameDay = effectiveOfferPerGameDay;
            Notes = notes;
        }

        /// <summary>null when the supplier is eligible on supply grounds.</summary>
        public ExcludedReason? ExcludedReason { get; }
        /// <summary>The effective regional offer per game day; null when it cannot be computed.</summary>
        public double? EffectiveOfferPerGameDay { get; }
        /// <summary>Human-readable reasons for every factor applied, in a fixed order (for traces and the CLI).</summary>
        public IReadOnlyList<string> Notes { get; }
    }

    /// <summary>
    /// A4 supply eligibility (Contracts/README.md "Quote semantics"; log §25.3, §14.3).
    ///
    ///   effective = base x confidenceFactor x dataPenalties
    ///
    /// | Data                          | Rule                                                                              |
    /// |-------------------------------|-----------------------------------------------------------------------------------|
    /// | confidence Unknown            | UnknownSupply: the figures may not be real.                                       |
    /// | output null                   | UnknownSupply: nothing was measured. null is never zero.                          |
    /// | confidence Validated          | factor 1.0                                                                        |
    /// | confidence Provisional        | factor 0.5                                                                        |
    /// | inputUse null                 | surplus unknown: base uses output instead, penalty x0.5 (§25.3: downgrade to a    |
    /// |                               | capacity-based index rather than invent a mass-balanced surplus)                  |
    /// | capacity null                 | no capacity bound: base is not capped, penalty x0.5                               |
    /// | effective &lt;= 0                | NoSupply (a measured zero or deficit)                                             |
    ///
    /// base = min(output - inputUse, capacity), with the substitutions above.
    /// Precedence inside supply: UnknownSupply before NoSupply.
    /// In v1 the effective offer only decides eligibility (&gt; 0) and is reported; it does not change prices, so
    /// there is no age decay here: staleness is the separate MaxRegionSeqAge cut-off.
    /// </summary>
    public static class SupplyRule
    {
        public const double ValidatedFactor = 1.0;
        public const double ProvisionalFactor = 0.5;
        public const double MissingInputUsePenalty = 0.5;
        public const double MissingCapacityPenalty = 0.5;

        public static SupplyAssessment Evaluate(ResourceObservation o)
        {
            var notes = new List<string>();

            if (o.Confidence == Confidence.Unknown)
                return new SupplyAssessment(ExcludedReason.UnknownSupply, null, new[] { "confidence Unknown" });
            if (o.RealizedOutputPerGameDay == null)
                return new SupplyAssessment(ExcludedReason.UnknownSupply, null, new[] { "output not measured" });

            var factor = o.Confidence == Confidence.Validated ? ValidatedFactor : ProvisionalFactor;
            if (o.Confidence == Confidence.Provisional) notes.Add(FormattableString.Invariant($"provisional x{ProvisionalFactor:0.##}"));

            var output = o.RealizedOutputPerGameDay.Value;
            double baseOffer;
            if (o.InputUsePerGameDay is double use)
            {
                baseOffer = output - use;
            }
            else
            {
                baseOffer = output;
                factor *= MissingInputUsePenalty;
                notes.Add(FormattableString.Invariant($"local use unknown x{MissingInputUsePenalty:0.##}"));
            }

            if (o.CapacityEstimatePerGameDay is double capacity)
            {
                baseOffer = Math.Min(baseOffer, capacity);
            }
            else
            {
                factor *= MissingCapacityPenalty;
                notes.Add(FormattableString.Invariant($"capacity unknown x{MissingCapacityPenalty:0.##}"));
            }

            var effective = baseOffer * factor;
            return effective <= 0
                ? new SupplyAssessment(ExcludedReason.NoSupply, effective, notes)
                : new SupplyAssessment(null, effective, notes);
        }
    }
}
