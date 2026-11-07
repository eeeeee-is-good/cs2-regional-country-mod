using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    public enum BuyerStatus
    {
        /// <summary>Buys from this supplier at the cleared price.</summary>
        Served,
        /// <summary>The cleared price equals its next-best option: the marginal buyer, partly served.</summary>
        Indifferent,
        /// <summary>Its next-best option is cheaper at the cleared price.</summary>
        PricedOut,
    }

    /// <summary>A city whose demand leans on a supplier. Prices are at the supplier's gate (before freight).</summary>
    public sealed class MarketBuyer
    {
        public MarketBuyer(string cityId, double demand, double switchPrice, BuyerStatus status)
        {
            CityId = cityId; Demand = demand; SwitchPrice = switchPrice; Status = status;
        }

        public string CityId { get; }
        /// <summary>Per game day: inputUse - output, when both are measured.</summary>
        public double Demand { get; }
        /// <summary>The highest gate price at which this city still prefers this supplier (its next-best option minus the
        /// freight and subsidy from this supplier). +Infinity when it has no other option.</summary>
        public double SwitchPrice { get; }
        public BuyerStatus Status { get; }
    }

    /// <summary>One supplier's market: the supply curve, the demand leaning on it, and where they cross.</summary>
    public sealed class SupplierMarket
    {
        public string CityId { get; set; } = "";
        /// <summary>The regional producer price: the flat part of the supply curve.</summary>
        public double BasePrice { get; set; }
        /// <summary>The effective regional offer per game day (A4): where the supply curve starts to rise.</summary>
        public double SpareSupply { get; set; }
        public double PremiumSlope { get; set; }
        public double MaxPremium { get; set; }
        /// <summary>Cleared gate price minus the base price.</summary>
        public double Premium { get; set; }
        public double GatePrice => BasePrice + Premium;
        /// <summary>Quantity per game day this supplier is leaned on for at the cleared price.</summary>
        public double Load { get; set; }
        /// <summary>Load / SpareSupply. Above 1 means overloaded.</summary>
        public double Pressure { get; set; }
        /// <summary>Sorted by city id.</summary>
        public List<MarketBuyer> Buyers { get; set; } = new List<MarketBuyer>();

        /// <summary>Gate price the supplier asks for a quantity: flat up to SpareSupply, then rising, capped.</summary>
        public double SupplyPrice(double quantity) => BasePrice + ScarcityEvaluator.Premium(BasePrice, PremiumSlope, MaxPremium, quantity, SpareSupply);
    }

    /// <summary>
    /// Soft scarcity pricing (manifest parameters.scarcity; log §16.3 caveat, §17.4 step 4). Each supplier is a small
    /// supply/demand market at its gate:
    ///
    ///   supply:  price(q) = base                                    for q &lt;= spare
    ///            price(q) = base x (1 + min(max, slope x (q/spare - 1)))  above it
    ///   demand:  every city with a deficit (inputUse - output &gt; 0, both measured, confidence not Unknown) that can be
    ///            reached leans on this supplier while the supplier's delivered price is no worse than its next-best
    ///            option (another supplier at its current price, or the world). That makes demand a falling step curve.
    ///   price:   the lowest gate price where supply covers demand. Usually either the base price (enough steel) or
    ///            the price at which the marginal city switches to its next-best option (the world price acts as a ceiling).
    ///
    /// Suppliers compete, so every market is re-cleared with the others' latest prices until nothing changes. Starting
    /// from zero premiums, every round can only raise premiums, so this converges to the smallest consistent set of
    /// prices. Deterministic: fixed order, fixed stopping rule. Nothing is shipped, booked or allocated.
    /// Known simplification: a city's demand leans on one supplier at a time; it is not split between suppliers.
    /// </summary>
    public static class ScarcityEvaluator
    {
        private const int MaxRounds = 500;

        public static double Premium(double basePrice, double slope, double max, double quantity, double spare)
        {
            if (spare <= 0) return basePrice * max;
            var rise = Math.Min(max, slope * Math.Max(0, quantity / spare - 1));
            return basePrice * rise;
        }

        private sealed class Buyer
        {
            public Buyer(string cityId, double demand) { CityId = cityId; Demand = demand; }
            public string CityId { get; }
            public double Demand { get; }
        }

        private sealed class Lean
        {
            public Lean(Buyer buyer, double switchPrice, bool tieStays) { Buyer = buyer; SwitchPrice = switchPrice; TieStays = tieStays; }
            public Buyer Buyer { get; }
            public double SwitchPrice { get; }
            public bool TieStays { get; }
            public bool Stays(double p) => p < SwitchPrice || (p == SwitchPrice && TieStays);
        }

        internal static IReadOnlyList<SupplierMarket> Solve(QuoteContext ctx)
        {
            var rule = ctx.Manifest.Parameters.Scarcity;
            if (rule == null || !ctx.Known) return Array.Empty<SupplierMarket>();
            var basePrice = ctx.Price!.RegionalProducerPriceIndex;
            var cityIds = ctx.Manifest.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal).ToList();

            // Suppliers: exactly the cities that could make an eligible offer on supply grounds.
            var spare = new SortedDictionary<string, double>(StringComparer.Ordinal);
            var buyers = new List<Buyer>();
            foreach (var id in cityIds)
            {
                if (!ctx.Latest.TryGetValue(id, out var pub)) continue;
                var obs = ctx.Observation(pub);
                if (obs == null) continue;
                if (!ctx.IsStale(pub))
                {
                    var supply = SupplyRule.Evaluate(obs);
                    if (supply.ExcludedReason == null && supply.EffectiveOfferPerGameDay is double e && e > 0) spare[id] = e;
                }
                if (obs.Confidence != Confidence.Unknown && obs.RealizedOutputPerGameDay is double output && obs.InputUsePerGameDay is double use && use > output)
                    buyers.Add(new Buyer(id, use - output));
            }
            if (spare.Count == 0) return Array.Empty<SupplierMarket>();

            // Delivery add-on from each supplier to each buyer (freight - subsidy), and each buyer's best world price.
            var addOn = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var s in spare.Keys)
                foreach (var b in buyers)
                {
                    if (string.Equals(s, b.CityId, StringComparison.Ordinal)) continue;
                    var route = ctx.RouteBetween(s, b.CityId);
                    if (route != null) addOn[s + "\u0000" + b.CityId] = route.Cost - ctx.SubsidyFor(route);
                }
            var world = buyers.ToDictionary(b => b.CityId, b => ctx.GatewaysOf(b.CityId)
                .Select(g => LandedCostEngine.WorldOffer(ctx, g)).Where(o => o.Eligible)
                .Select(o => o.LandedPrice!.Value).DefaultIfEmpty(double.PositiveInfinity).Min(), StringComparer.Ordinal);

            var premium = spare.Keys.ToDictionary(s => s, _ => 0.0, StringComparer.Ordinal);
            for (var round = 0; round < MaxRounds; round++)
            {
                var next = spare.Keys.ToDictionary(s => s, s => Clear(rule, basePrice, spare[s], Leans(s)) - basePrice, StringComparer.Ordinal);
                var delta = next.Max(kv => Math.Abs(kv.Value - premium[kv.Key]));
                premium = next;
                if (delta <= 1e-12 * (1 + basePrice)) break;
            }

            return spare.Keys.Select(s =>
            {
                var leans = Leans(s);
                var gate = basePrice + premium[s];
                var market = new SupplierMarket
                {
                    CityId = s, BasePrice = basePrice, SpareSupply = spare[s], PremiumSlope = rule.PremiumSlope,
                    MaxPremium = rule.MaxPremium, Premium = premium[s],
                };
                double served = 0, marginal = 0;
                foreach (var l in leans.OrderBy(l => l.Buyer.CityId, StringComparer.Ordinal))
                {
                    var status = gate < l.SwitchPrice ? BuyerStatus.Served
                        : gate == l.SwitchPrice && l.TieStays ? BuyerStatus.Indifferent : BuyerStatus.PricedOut;
                    if (status == BuyerStatus.Served) served += l.Buyer.Demand;
                    if (status == BuyerStatus.Indifferent) marginal += l.Buyer.Demand;
                    market.Buyers.Add(new MarketBuyer(l.Buyer.CityId, l.Buyer.Demand, l.SwitchPrice, status));
                }
                // The marginal buyers take what the supplier offers at this price beyond the served demand.
                market.Load = served + Math.Min(marginal, Math.Max(0, QuantityAt(rule, basePrice, spare[s], gate) - served));
                market.Pressure = market.Load / spare[s];
                return market;
            }).ToList();

            // Every buyer this supplier can reach, with the gate price at which it would switch to its next-best option.
            List<Lean> Leans(string s)
            {
                var list = new List<Lean>();
                foreach (var b in buyers)
                {
                    if (!addOn.TryGetValue(s + "\u0000" + b.CityId, out var mine)) continue;
                    var alt = world[b.CityId];
                    var altWinsTie = false; // world offers come after city offers, so a tie with the world goes to the city
                    foreach (var other in spare.Keys)
                    {
                        if (string.Equals(other, s, StringComparison.Ordinal) || !addOn.TryGetValue(other + "\u0000" + b.CityId, out var theirs)) continue;
                        var price = basePrice + premium[other] + theirs;
                        var earlier = string.CompareOrdinal(other, s) < 0; // offers are ordered by city id: ties go to the earlier one
                        if (price < alt) { alt = price; altWinsTie = earlier; }
                        else if (price == alt && earlier) altWinsTie = true;
                    }
                    list.Add(new Lean(b, alt - mine, !altWinsTie));
                }
                return list;
            }
        }

        /// <summary>The lowest gate price p &gt;= base where the supply price of the demand still buying at p is &lt;= p.</summary>
        private static double Clear(ScarcityRule rule, double basePrice, double spare, List<Lean> leans)
        {
            double SupplyPrice(double q) => basePrice + Premium(basePrice, rule.PremiumSlope, rule.MaxPremium, q, spare);
            double DemandAt(double p) => leans.Where(l => l.Stays(p)).Sum(l => l.Buyer.Demand);

            var candidates = leans.Select(l => l.SwitchPrice).Where(x => !double.IsInfinity(x) && x >= basePrice)
                .Append(basePrice).Distinct().OrderBy(x => x).ToList();
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (SupplyPrice(DemandAt(c)) <= c) return c;
                // Just above c, the buyers whose switch price is c have left; demand is constant until the next candidate.
                var open = SupplyPrice(leans.Where(l => l.SwitchPrice > c).Sum(l => l.Buyer.Demand));
                var next = i + 1 < candidates.Count ? candidates[i + 1] : double.PositiveInfinity;
                if (open < next) return Math.Max(open, c);
            }
            return basePrice; // unreachable: the last interval is unbounded above
        }

        /// <summary>Inverse supply: the quantity offered at a gate price (unbounded at the cap or with a zero slope).</summary>
        private static double QuantityAt(ScarcityRule rule, double basePrice, double spare, double gate)
        {
            var rise = basePrice > 0 ? (gate - basePrice) / basePrice : 0;
            if (rise <= 0) return spare;
            if (rule.PremiumSlope <= 0 || rise >= rule.MaxPremium) return double.PositiveInfinity;
            return spare * (1 + rise / rule.PremiumSlope);
        }
    }
}
