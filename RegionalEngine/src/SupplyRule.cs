using System;
using Regions.Contracts;

namespace Regions.Engine
{
    public sealed class SupplyAssessment
    {
        public SupplyAssessment(ExcludedReason? excludedReason, double? effectiveOfferPerGameDay)
        {
            ExcludedReason = excludedReason;
            EffectiveOfferPerGameDay = effectiveOfferPerGameDay;
        }

        /// <summary>null when the supplier is eligible on supply grounds.</summary>
        public ExcludedReason? ExcludedReason { get; }
        /// <summary>min(output - inputUse, capacity) x confidenceFactor; null when it cannot be computed.</summary>
        public double? EffectiveOfferPerGameDay { get; }
    }

    /// <summary>
    /// Supply eligibility (Contracts/README.md "Quote semantics"):
    /// effective = min(output - inputUse, capacity) x confidenceFactor, with confidenceFactor(Validated) = 1.0.
    ///
    /// INTERIM (A3): the factors for Provisional/Unknown and the handling of a null capacity are decided in A4.
    /// Until then this fails closed: anything not Validated, or any null input, is UnknownSupply. null is never
    /// treated as zero (log §25.5). UnknownSupply takes precedence over NoSupply.
    /// </summary>
    public static class SupplyRule
    {
        public static SupplyAssessment Evaluate(ResourceObservation o)
        {
            if (o.Confidence != Confidence.Validated) return new SupplyAssessment(ExcludedReason.UnknownSupply, null);
            if (o.RealizedOutputPerGameDay == null || o.InputUsePerGameDay == null || o.CapacityEstimatePerGameDay == null)
                return new SupplyAssessment(ExcludedReason.UnknownSupply, null);

            const double validatedFactor = 1.0;
            var surplus = o.RealizedOutputPerGameDay.Value - o.InputUsePerGameDay.Value;
            var effective = Math.Min(surplus, o.CapacityEstimatePerGameDay.Value) * validatedFactor;

            return effective <= 0
                ? new SupplyAssessment(ExcludedReason.NoSupply, effective)
                : new SupplyAssessment(null, effective);
        }
    }
}
