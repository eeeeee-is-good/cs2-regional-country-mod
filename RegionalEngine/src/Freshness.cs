using System;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>
    /// D1 (docs/ECONOMIC_MODEL_PLAN.md): an old snapshot fades out on both sides, supply and demand, instead of
    /// keeping full weight and then vanishing (ARCHITECTURE.md N5 "decaying estimate").
    ///
    ///   age = currentRegionSeq - snapshot.regionSeq   (counted in region ingestions, never city clocks)
    ///   weight = 1                                        while age &lt;= maxAge / 2
    ///          = 1 - (age - maxAge/2) / (maxAge + 1 - maxAge/2)   then falling linearly
    ///          = 0                                        when age &gt; maxAge (StaleSnapshot)
    /// No maxAge (null) means no ageing.
    /// </summary>
    public static class Freshness
    {
        public static double Weight(int age, int? maxAge)
        {
            if (maxAge == null) return 1;
            if (age > maxAge.Value) return 0;
            var full = maxAge.Value / 2.0;
            if (age <= full) return 1;
            return 1 - (age - full) / (maxAge.Value + 1 - full);
        }

        public static double Weight(RegionState state, PublishedSnapshot pub) =>
            Weight(state.CurrentRegionSeq - pub.RegionSeq, state.Manifest.Parameters.Staleness.MaxRegionSeqAge);
    }

    /// <summary>
    /// What a city needs from others per game day: its deficit inputUse - output, when both are measured and the
    /// figures are not Unknown. null = unknown (never zero).
    /// D2: demand is deliberately not discounted for Provisional figures (supply is, by A4). Counting uncertain need in
    /// full leans toward "scarce" rather than "plentiful", the safer error for a mod that may nudge real prices.
    /// Freshness (D1) applies to demand exactly as to supply.
    /// </summary>
    public static class DemandRule
    {
        public static double? Deficit(ResourceObservation o)
        {
            if (o.Confidence == Confidence.Unknown || o.RealizedOutputPerGameDay is not double output || o.InputUsePerGameDay is not double use)
                return null;
            return Math.Max(0, use - output);
        }
    }
}
