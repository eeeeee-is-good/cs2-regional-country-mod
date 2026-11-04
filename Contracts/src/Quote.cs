using System.Collections.Generic;

namespace Regions.Contracts
{
    public sealed class QuoteRequest
    {
        public string DestCityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
    }

    /// <summary>
    /// A modeled landed-cost quote plus its explanation (schema: schema/quote-result.v1.schema.json).
    /// It is NOT a shipment, a sale or a payment (D-20, D-35).
    /// Invariant: for each eligible offer, ProducerPrice + Freight + Tariff - Subsidy == LandedPrice.
    /// </summary>
    public sealed class QuoteResult
    {
        public int SchemaVersion { get; set; } = SchemaVersions.QuoteResult;
        public string DestCityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        /// <summary>Sorted: City offers by CityId, then World offers by gateway EndpointKey.</summary>
        public List<Offer> Offers { get; set; } = new List<Offer>();
        /// <summary>Index into Offers of the lowest eligible LandedPrice (ties broken by Offers order); null if none.</summary>
        public int? SelectedOfferIndex { get; set; }
        public ReportedAs ReportedAs { get; set; } = ReportedAs.ModeledPriceQuoteNotPhysicalTrade;
        /// <summary>Always false in Prototype 0. True only when a verified game-side writer applied a modifier.</summary>
        public bool AppliedToGame { get; set; }
        public QuoteProvenance Provenance { get; set; } = new QuoteProvenance();
    }

    public sealed class Offer
    {
        public OriginKind OriginKind { get; set; }
        /// <summary>Supplier city for OriginKind=City; null for World.</summary>
        public string? OriginCityId { get; set; }
        /// <summary>Destination gateway endpoint used for OriginKind=World; null for City.</summary>
        public string? WorldGatewayEndpointKey { get; set; }
        /// <summary>LinkIds in traversal order; empty for World or ineligible offers.</summary>
        public List<string> Path { get; set; } = new List<string>();
        public bool Eligible { get; set; }
        /// <summary>Set when Eligible is false.</summary>
        public ExcludedReason? ExcludedReason { get; set; }
        /// <summary>Price components. null when the offer is ineligible.</summary>
        public double? ProducerPrice { get; set; }
        public double? Freight { get; set; }
        public double? Tariff { get; set; }
        public double? Subsidy { get; set; }
        public double? LandedPrice { get; set; }
        /// <summary>Supplier snapshot used (City offers only).</summary>
        public SnapshotRef? SupplierSnapshot { get; set; }
        /// <summary>Effective regional offer quantity used for eligibility, per game day (City offers only). Not a shipment.</summary>
        public double? EffectiveRegionalOfferPerGameDay { get; set; }
    }

    public sealed class SnapshotRef
    {
        public string CityId { get; set; } = "";
        public int Seq { get; set; }
        public int RegionSeq { get; set; }
    }

    public sealed class QuoteProvenance
    {
        public string EngineVersion { get; set; } = "";
        public string RegionId { get; set; } = "";
        public int ManifestRevision { get; set; }
        /// <summary>Region store counter at quote time.</summary>
        public int CurrentRegionSeq { get; set; }
        /// <summary>SHA-256 of the engine's canonical inputs. Golden files may hold null, meaning "not compared".</summary>
        public string? InputsSha256 { get; set; }
        /// <summary>Deterministic id, e.g. "{destCityId}/{resourceId}/m{manifestRevision}/r{currentRegionSeq}".</summary>
        public string TraceId { get; set; } = "";
    }
}
