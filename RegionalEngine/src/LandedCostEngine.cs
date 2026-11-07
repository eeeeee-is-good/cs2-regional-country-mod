using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>ARCHITECTURE.md §6. Check(state) (invariant report) arrives with A5.</summary>
    public interface IRegionalEngine
    {
        QuoteResult Quote(RegionState state, QuoteRequest request);
    }

    /// <summary>
    /// Landed-cost quote for one (destination city, resource), following Contracts/README.md "Quote semantics"
    /// and log §16.3. Pure and deterministic: no IO, no clock, no randomness, ordinal sorting everywhere.
    /// The result is a modeled price signal, never a shipment (ReportedAs, AppliedToGame=false).
    /// The QuoteResult is itself the trace: every offer considered, with its exclusion reason or price components.
    /// </summary>
    public sealed class LandedCostEngine : IRegionalEngine
    {
        public const string EngineVersion = "0.1.0-a3";

        private static readonly TransportMode[] AllModes =
            (TransportMode[])System.Enum.GetValues(typeof(TransportMode));

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

            var dest = request.DestCityId;
            var latest = state.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId, StringComparer.Ordinal);
            var resource = state.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, request.ResourceId, StringComparison.Ordinal));
            var price = m.Parameters.ResourcePrices.FirstOrDefault(r => string.Equals(r.ResourceId, request.ResourceId, StringComparison.Ordinal));
            var graph = new RouteGraph(m, latest);
            var costs = m.Parameters.ModeCosts.ToDictionary(c => c.Mode);

            var offers = new List<Offer>();

            // One offer per supplier city whose latest snapshot reports this resource, ordered by city id.
            foreach (var city in m.Cities.OrderBy(c => c.CityId, StringComparer.Ordinal))
            {
                if (string.Equals(city.CityId, dest, StringComparison.Ordinal)) continue;
                if (!latest.TryGetValue(city.CityId, out var pub)) continue;
                var obs = pub.Snapshot.Resources.FirstOrDefault(r => string.Equals(r.ResourceId, request.ResourceId, StringComparison.Ordinal));
                if (obs == null) continue;
                offers.Add(CityOffer(state, graph, dest, pub, obs, resource, price));
            }

            // One offer per world gateway of the destination, ordered by endpoint key.
            foreach (var gw in m.WorldGateways
                         .Where(g => string.Equals(g.CityId, dest, StringComparison.Ordinal))
                         .OrderBy(g => g.EndpointKey, StringComparer.Ordinal))
            {
                offers.Add(WorldOffer(latest, costs, gw, resource, price));
            }

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
        private static Offer CityOffer(RegionState state, RouteGraph graph, string dest, PublishedSnapshot pub,
                                       ResourceObservation obs, ResourceDefinition? resource, ResourcePrice? price)
        {
            var offer = new Offer
            {
                OriginKind = OriginKind.City,
                OriginCityId = pub.Snapshot.CityId,
                SupplierSnapshot = new SnapshotRef { CityId = pub.Snapshot.CityId, Seq = pub.Snapshot.Seq, RegionSeq = pub.RegionSeq },
            };

            if (resource == null || price == null) return Exclude(offer, ExcludedReason.UnknownResource);

            var route = graph.Cheapest(pub.Snapshot.CityId, dest, resource.AllowedFreightModes);
            if (route == null)
            {
                var anyMode = graph.Cheapest(pub.Snapshot.CityId, dest, AllModes);
                return Exclude(offer, anyMode == null ? ExcludedReason.NoPath : ExcludedReason.ModeIncompatible);
            }

            var maxAge = state.Manifest.Parameters.Staleness.MaxRegionSeqAge;
            if (maxAge != null && state.CurrentRegionSeq - pub.RegionSeq > maxAge.Value)
                return Exclude(offer, ExcludedReason.StaleSnapshot);

            var supply = SupplyRule.Evaluate(obs);
            offer.EffectiveRegionalOfferPerGameDay = supply.EffectiveOfferPerGameDay;
            if (supply.ExcludedReason != null) return Exclude(offer, supply.ExcludedReason.Value);

            offer.Path = route.LinkIds.ToList();
            return Price(offer, price.RegionalProducerPriceIndex, route.Cost);
        }

        // World offers: UnknownResource -> NoWorldGateway (gateway endpoint or mode cost missing) -> ModeIncompatible.
        private static Offer WorldOffer(IReadOnlyDictionary<string, PublishedSnapshot> latest, IReadOnlyDictionary<TransportMode, ModeCost> costs,
                                        WorldGateway gw, ResourceDefinition? resource, ResourcePrice? price)
        {
            var offer = new Offer { OriginKind = OriginKind.World, WorldGatewayEndpointKey = gw.EndpointKey };

            if (resource == null || price == null) return Exclude(offer, ExcludedReason.UnknownResource);
            if (!costs.TryGetValue(gw.Mode, out var cost) || !RouteGraph.EndpointCarries(latest, gw.CityId, gw.EndpointKey, gw.Mode))
                return Exclude(offer, ExcludedReason.NoWorldGateway);
            if (!resource.AllowedFreightModes.Contains(gw.Mode)) return Exclude(offer, ExcludedReason.ModeIncompatible);

            var freight = cost.FixedCost + cost.CostPerKm * gw.DistanceKm;
            if (!Num.IsFinite(freight))
                throw new RegionStateException(new[] { $"worldGateways[{gw.CityId}/{gw.EndpointKey}]: freight cost overflows" });
            return Price(offer, price.WorldProducerPriceIndex, freight);
        }

        private static Offer Price(Offer offer, double producerPrice, double freight)
        {
            const double tariff = 0, subsidy = 0; // Tariffs and subsidies arrive with A7.
            var landed = producerPrice + freight + tariff - subsidy;
            if (!Num.IsFiniteNonNegative(landed))
                throw new RegionStateException(new[] { $"landed price is not finite and non-negative ({landed})" });

            offer.Eligible = true;
            offer.ExcludedReason = null;
            offer.ProducerPrice = producerPrice;
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
            offer.ProducerPrice = offer.Freight = offer.Tariff = offer.Subsidy = offer.LandedPrice = null;
            return offer;
        }
    }
}
