using System;
using Regions.Contracts;

namespace Regions.Simulator
{
    /// <summary>How a synthetic city's consumption of a resource reacts to price (Phase 2, simulator only).</summary>
    public enum DemandClass
    {
        /// <summary>Little reaction to price (food, fuel).</summary>
        Essential,
        /// <summary>Reacts more (consumer goods).</summary>
        Discretionary,
        /// <summary>Follows production needs, not price.</summary>
        IndustrialInput,
    }

    /// <summary>
    /// Fixed parameters of the synthetic goods economy. SIMULATOR ONLY: in a real region, CS2's households and companies
    /// are the demand and supply response, and observed snapshots already contain it. Putting any of this in the engine
    /// would make the same price change act twice (docs/ECONOMIC_MODEL_PLAN.md §3.2, §4.5).
    /// </summary>
    public sealed class GoodsRule
    {
        public double EssentialElasticity { get; set; } = 0.2;
        public double DiscretionaryElasticity { get; set; } = 0.8;
        /// <summary>How strongly output follows the selling price, below capacity.</summary>
        public double SupplyElasticity { get; set; } = 0.6;
        /// <summary>Share of the gap to the desired level closed each period (a lag; 1 = immediate).</summary>
        public double Adjustment { get; set; } = 0.3;
        /// <summary>Largest output change per period, as a share of capacity.</summary>
        public double MaxOutputStep { get; set; } = 0.1;

        public double Elasticity(DemandClass c) => c switch
        {
            DemandClass.Essential => EssentialElasticity,
            DemandClass.Discretionary => DiscretionaryElasticity,
            _ => 0,
        };
    }

    /// <summary>
    /// One synthetic city's market for one resource: what it makes and what it uses, per game day.
    /// Constant-elasticity reactions around reference prices, with a lag and caps. No randomness.
    /// </summary>
    public sealed class CityGoods
    {
        public string CityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        public Confidence Confidence { get; set; } = Confidence.Validated;

        /// <summary>Most it can produce. Output never exceeds it.</summary>
        public double Capacity { get; set; }
        /// <summary>Phase 4: the most capacity the city's industrial buildings can hold. 0 = not modelled.</summary>
        public double SpaceCapacity { get; set; }
        /// <summary>Output when it sells at the base price.</summary>
        public double BaseOutput { get; set; }
        public double Output { get; set; }

        /// <summary>Price-sensitive use (households, shops) at the reference price.</summary>
        public double BaseConsumerUse { get; set; }
        /// <summary>BaseConsumerUse at the start; Phase 4 scales it with the city's spending power.</summary>
        public double InitialConsumerUse { get; set; }
        public DemandClass ConsumerClass { get; set; } = DemandClass.Discretionary;
        public double ConsumerUse { get; set; }
        /// <summary>Use that follows production, not price (industrial inputs).</summary>
        public double IndustrialUse { get; set; }

        /// <summary>The price the city faced when the simulation started: consumption is measured against it.</summary>
        public double? ReferencePrice { get; set; }

        public double TotalUse => ConsumerUse + IndustrialUse;

        /// <summary>
        /// One period. <paramref name="buy"/> is the applied price to buy, <paramref name="sell"/> the applied price it
        /// sells at; either may be null (no offer reaches it, or it does not sell).
        /// </summary>
        public void Step(double? buy, double? sell, double basePrice, GoodsRule rule)
        {
            // Consumers face the import price when the city lacks the good, and its own selling price when it makes enough.
            var faced = Output >= TotalUse ? (sell ?? buy ?? basePrice) : (buy ?? sell ?? basePrice);
            ReferencePrice ??= faced;

            var ratio = faced / Math.Max(ReferencePrice.Value, 1e-9);
            var wantedUse = BaseConsumerUse * Math.Pow(Math.Max(ratio, 1e-9), -rule.Elasticity(ConsumerClass));
            ConsumerUse += rule.Adjustment * (wantedUse - ConsumerUse);

            if (Capacity > 0)
            {
                var sellRatio = (sell ?? basePrice) / Math.Max(basePrice, 1e-9);
                var wantedOutput = Math.Min(Capacity, BaseOutput * Math.Pow(Math.Max(sellRatio, 1e-9), rule.SupplyElasticity));
                var move = rule.Adjustment * (wantedOutput - Output);
                var cap = rule.MaxOutputStep * Capacity;
                Output = Math.Max(0, Math.Min(Capacity, Output + Math.Max(-cap, Math.Min(cap, move))));
            }
        }
    }
}
