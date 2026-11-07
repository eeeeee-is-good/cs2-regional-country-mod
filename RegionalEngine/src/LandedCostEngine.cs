using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>ARCHITECTURE.md §6.</summary>
    public interface IRegionalEngine
    {
        QuoteResult Quote(RegionState state, QuoteRequest request);
        /// <summary>Quotes every (city, resource) pair and checks the log §18.4 invariants. See InvariantChecker.</summary>
        InvariantReport Check(RegionState state);
    }

    /// <summary>
    /// Landed-cost quote for one (destination city, resource), following Contracts/README.md "Quote semantics"
    /// and log §16.3, §17.4. Pure and deterministic: no IO, no clock, no randomness, ordinal sorting everywhere.
    /// The result is a modeled price signal, never a shipment (ReportedAs, AppliedToGame=false).
    /// The QuoteResult is itself the trace: every offer considered, with its exclusion reason or price components.
    /// </summary>
    public sealed class LandedCostEngine : IRegionalEngine
    {
        /// <summary>0.2.0: A4 supply rule. 0.3.0: A7 tariffs and subsidies. 0.4.0: optional scarcity pricing.</summary>
        public const string EngineVersion = "0.4.0";

        private static readonly TransportMode[] AllModes =
            (TransportMode[])System.Enum.GetValues(typeof(TransportMode));

        public InvariantReport Check(RegionState state) => new InvariantChecker(this).Check(state);

        /// <summary>Forecast for every policy in the manifest, sorted by policy id. Estimates only, never cash (log §17.7).</summary>
        public IReadOnlyList<PolicyForecast> Forecast(RegionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var problems = RegionStateValidator.Validate(state);
            if (problems.Count > 0) throw new RegionStateException(problems);

            var m = state.Manifest;
            var policies = m.Parameters.Policies ?? new PolicySet();
            var latest = state.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId, StringComparer.Ordinal);
            var graph = new RouteGraph(m, latest);
            return policies.ImportTariffs.Select(PolicyEvaluator.Forecast)
                .Concat(policies.FreightSubsidies.Select(s => PolicyEvaluator.Forecast(s, state, graph, latest)))
                .OrderBy(f => f.PolicyId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The supply/demand picture behind scarcity pricing for one resource: per supplier, its spare supply, the
        /// cities leaning on it and the cleared price. Empty when the region has no scarcity rule. See ScarcityEvaluator.
        /// </summary>
        public IReadOnlyList<SupplierMarket> Markets(RegionState state, string resourceId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var problems = RegionStateValidator.Validate(state);
            if (problems.Count > 0) throw new RegionStateException(problems);
            return QuoteContext.Create(state, resourceId).Markets;
        }

        public QuoteResult Quote(RegionState state, QuoteRequest request)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var problems = RegionStateValidator.Validate(state);
            var m = state.Manifest;
            if (string.IsNullOrEmpty(request.ResourceId)) problems.Add("request: empty resourceId");
            if (m.Cities != null && !m.Cities.Any(c => string.Equals(c.CityId, request.DestCityId, StringComparison.Ordinal)))
                problems.Add($"request: destination city {request.DestCityId} is not in the manifest");
            if (problems.Count > 0) throw new RegionStateException(problems);

            var ctx = QuoteContext.Create(state, request.ResourceId);
            var dest = request.DestCityId;
            var offers = new List<Offer>();

            // One offer per supplier city whose latest snapshot reports this resource, ordered by city id.
            foreach (var city in m.Cities.OrderBy(c => c.CityId, StringComparer.Ordinal))
            {
                if (string.Equals(city.CityId, dest, StringComparison.Ordinal)) continue;
                if (!ctx.Latest.TryGetValue(city.CityId, out var pub)) continue;
                var obs = ctx.Observation(pub);
                if (obs == null) continue;
                offers.Add(CityOffer(ctx, dest, pub, obs));
            }

            // One offer per world gateway of the destination, ordered by endpoint key.
            foreach (var gw in ctx.GatewaysOf(dest))
                offers.Add(WorldOffer(ctx, gw));

            int? selected = null;
            for (var i = 0; i < offers.Count; i++)
            {
                if (!offers[i].Eligible) continue;
                if (selected == null || offers[i].LandedPrice!.Value < offers[selected.Value].LandedPrice!.Value) selected = i;
            }

            return new QuoteResult
            {
                DestCityId = dest,
                ResourceId = request.ResourceId,
                Offers = offers,
                SelectedOfferIndex = selected,
                ReportedAs = ReportedAs.ModeledPriceQuoteNotPhysicalTrade,
                AppliedToGame = false,
                Provenance = new QuoteProvenance
                {
                    EngineVersion = EngineVersion,
                    RegionId = m.RegionId,
                    ManifestRevision = m.Revision,
                    CurrentRegionSeq = state.CurrentRegionSeq,
                    InputsSha256 = InputHasher.Hash(state, request),
                    TraceId = $"{dest}/{request.ResourceId}/m{m.Revision}/r{state.CurrentRegionSeq}",
                },
            };
        }

        // Exclusion precedence (Contracts/README.md): UnknownResource -> NoPath/ModeIncompatible -> StaleSnapshot -> UnknownSupply -> NoSupply.
        internal static Offer CityOffer(QuoteContext ctx, string dest, PublishedSnapshot pub, ResourceObservation obs)
        {
            var origin = pub.Snapshot.CityId;
            var offer = new Offer
            {
                OriginKind = OriginKind.City,
                OriginCityId = origin,
                SupplierSnapshot = new SnapshotRef { CityId = origin, Seq = pub.Snapshot.Seq, RegionSeq = pub.RegionSeq },
            };

            if (!ctx.Known) return Exclude(offer, ExcludedReason.UnknownResource);

            var route = ctx.RouteBetween(origin, dest);
            if (route == null)
            {
                var anyMode = ctx.Graph.Cheapest(origin, dest, AllModes);
                return Exclude(offer, anyMode == null ? ExcludedReason.NoPath : ExcludedReason.ModeIncompatible);
            }

            if (ctx.IsStale(pub)) return Exclude(offer, ExcludedReason.StaleSnapshot);

            var supply = SupplyRule.Evaluate(obs);
            offer.EffectiveRegionalOfferPerGameDay = supply.EffectiveOfferPerGameDay;
            if (supply.ExcludedReason != null) return Exclude(offer, supply.ExcludedReason.Value);

            offer.Path = route.LinkIds.ToList();
            // Domestic trade: never customs (D-44). Freight subsidy only on qualifying links. Scarcity raises the supplier's price.
            return Price(offer, ctx.Price!.RegionalProducerPriceIndex, ctx.PremiumOf(origin), route.Cost, tariff: 0, subsidy: ctx.SubsidyFor(route));
        }

        // World offers: UnknownResource -> NoWorldGateway (gateway endpoint or mode cost missing) -> ModeIncompatible.
        internal static Offer WorldOffer(QuoteContext ctx, WorldGateway gw)
        {
            var offer = new Offer { OriginKind = OriginKind.World, WorldGatewayEndpointKey = gw.EndpointKey };

            if (!ctx.Known) return Exclude(offer, ExcludedReason.UnknownResource);
            if (!ctx.Costs.TryGetValue(gw.Mode, out var cost) || !RouteGraph.EndpointCarries(ctx.Latest, gw.CityId, gw.EndpointKey, gw.Mode))
                return Exclude(offer, ExcludedReason.NoWorldGateway);
            if (!ctx.Resource!.AllowedFreightModes.Contains(gw.Mode)) return Exclude(offer, ExcludedReason.ModeIncompatible);

            var freight = cost.FixedCost + cost.CostPerKm * gw.DistanceKm;
            if (!Num.IsFinite(freight))
                throw new RegionStateException(new[] { $"worldGateways[{gw.CityId}/{gw.EndpointKey}]: freight cost overflows" });
            var world = ctx.Price!.WorldProducerPriceIndex;
            // Foreign leg: customs applies; domestic freight subsidies and regional scarcity never do (log §17.6).
            return Price(offer, world, scarcity: 0, freight, tariff: ctx.TariffFor(world), subsidy: 0);
        }

        private static Offer Price(Offer offer, double producerPrice, double scarcity, double freight, double tariff, double subsidy)
        {
            var landed = producerPrice + scarcity + freight + tariff - subsidy;
            if (!Num.IsFiniteNonNegative(tariff) || !Num.IsFiniteNonNegative(subsidy) || !Num.IsFiniteNonNegative(scarcity)
                || subsidy > freight + 1e-9 * Math.Max(1, freight))
                throw new RegionStateException(new[] { $"price parts out of range (scarcity {scarcity}, tariff {tariff}, subsidy {subsidy}, freight {freight})" });
            if (!Num.IsFiniteNonNegative(landed))
                throw new RegionStateException(new[] { $"landed price is not finite and non-negative ({landed})" });

            offer.Eligible = true;
            offer.ExcludedReason = null;
            offer.ProducerPrice = producerPrice;
            offer.ScarcityPremium = scarcity;
            offer.Freight = freight;
            offer.Tariff = tariff;
            offer.Subsidy = subsidy;
            offer.LandedPrice = landed;
            return offer;
        }

        private static Offer Exclude(Offer offer, ExcludedReason reason)
        {
            offer.Eligible = false;
            offer.ExcludedReason = reason;
            offer.Path = new List<string>();
            offer.ProducerPrice = offer.ScarcityPremium = offer.Freight = offer.Tariff = offer.Subsidy = offer.LandedPrice = null;
            return offer;
        }
    }

    /// <summary>Everything a quote needs for one resource, computed once: routes, policies and scarcity premiums.</summary>
    internal sealed class QuoteContext
    {
        private readonly Dictionary<string, Route?> _routes = new Dictionary<string, Route?>(StringComparer.Ordinal);
        private Dictionary<string, double> _premiums = new Dictionary<string, double>(StringComparer.Ordinal);

        private QuoteContext(RegionState state, string resourceId)
        {
            State = state;
            Manifest = state.Manifest;
            ResourceId = resourceId;
            Latest = state.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId, StringComparer.Ordinal);
            Graph = new RouteGraph(Manifest, Latest);
            Costs = Manifest.Parameters.ModeCosts.ToDictionary(c => c.Mode);
            Links = Manifest.Links.ToDictionary(l => l.LinkId, StringComparer.Ordinal);
            Resource = state.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, resourceId, StringComparison.Ordinal));
            Price = Manifest.Parameters.ResourcePrices.FirstOrDefault(r => string.Equals(r.ResourceId, resourceId, StringComparison.Ordinal));
            Tariffs = PolicyEvaluator.Tariffs(Manifest, resourceId).ToList();
            Subsidies = PolicyEvaluator.Subsidies(Manifest, resourceId)
                .Select(s => (Policy: s, Scale: PolicyEvaluator.Forecast(s, state, Graph, Latest).Scale ?? 1.0))
                .ToList();
        }

        public static QuoteContext Create(RegionState state, string resourceId)
        {
            var ctx = new QuoteContext(state, resourceId);
            // Scarcity needs routes, policies and world offers, so it is solved last (log §17.4 step 4 after steps 1-3).
            ctx.Markets = ScarcityEvaluator.Solve(ctx);
            ctx._premiums = ctx.Markets.ToDictionary(x => x.CityId, x => x.Premium, StringComparer.Ordinal);
            return ctx;
        }

        public RegionState State { get; }
        public RegionManifest Manifest { get; }
        public string ResourceId { get; }
        public Dictionary<string, PublishedSnapshot> Latest { get; }
        public RouteGraph Graph { get; }
        public Dictionary<TransportMode, ModeCost> Costs { get; }
        public Dictionary<string, LinkEntry> Links { get; }
        public ResourceDefinition? Resource { get; }
        public ResourcePrice? Price { get; }
        public List<ImportTariff> Tariffs { get; }
        public List<(FreightSubsidy Policy, double Scale)> Subsidies { get; }
        public IReadOnlyList<SupplierMarket> Markets { get; private set; } = Array.Empty<SupplierMarket>();

        public bool Known => Resource != null && Price != null;

        public ResourceObservation? Observation(PublishedSnapshot pub) =>
            pub.Snapshot.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, ResourceId, StringComparison.Ordinal));

        public bool IsStale(PublishedSnapshot pub)
        {
            var maxAge = Manifest.Parameters.Staleness.MaxRegionSeqAge;
            return maxAge != null && State.CurrentRegionSeq - pub.RegionSeq > maxAge.Value;
        }

        /// <summary>Cheapest freight path in a mode allowed for the resource (cached). null when none or the resource is unknown.</summary>
        public Route? RouteBetween(string from, string to)
        {
            if (Resource == null) return null;
            var key = from + "\u0000" + to;
            if (!_routes.TryGetValue(key, out var route)) _routes[key] = route = Graph.Cheapest(from, to, Resource.AllowedFreightModes);
            return route;
        }

        public IEnumerable<WorldGateway> GatewaysOf(string cityId) =>
            Manifest.WorldGateways
                .Where(g => string.Equals(g.CityId, cityId, StringComparison.Ordinal))
                .OrderBy(g => g.EndpointKey, StringComparer.Ordinal);

        public double SubsidyFor(Route route) =>
            Subsidies.Sum(s => s.Scale * PolicyEvaluator.SubsidyPerUnit(s.Policy, route.LinkIds, Links, Costs));

        public double TariffFor(double worldPrice) => Tariffs.Sum(t => PolicyEvaluator.TariffPerUnit(t, worldPrice));

        public double PremiumOf(string supplier) => _premiums.TryGetValue(supplier, out var p) ? p : 0;
    }
}
