using System.Collections.Generic;

namespace Regions.Contracts
{
    /// <summary>
    /// Region definition: member cities, atlas placement, links and model parameters
    /// (schema: schema/region-manifest.v1.schema.json). Lives in the external region store, never in a save.
    /// Written atomically with optimistic concurrency on Revision.
    /// </summary>
    public sealed class RegionManifest
    {
        public int SchemaVersion { get; set; } = SchemaVersions.RegionManifest;
        public string RegionId { get; set; } = "";
        /// <summary>Incremented on every write. A writer must supply the revision it read.</summary>
        public int Revision { get; set; }
        /// <summary>Sorted by CityId.</summary>
        public List<CityEntry> Cities { get; set; } = new List<CityEntry>();
        /// <summary>Sorted by LinkId.</summary>
        public List<LinkEntry> Links { get; set; } = new List<LinkEntry>();
        /// <summary>Sorted by (CityId, EndpointKey).</summary>
        public List<WorldGateway> WorldGateways { get; set; } = new List<WorldGateway>();
        public ModelParameters Parameters { get; set; } = new ModelParameters();
    }

    public sealed class CityEntry
    {
        public string CityId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>Informational save reference used by the Prototype 0 exporter to find its CityId. Not identity (V-15).</summary>
        public string? SaveRef { get; set; }
        /// <summary>Freeform atlas placement (map spec). Not a claim about terrain continuity.</summary>
        public Placement? Placement { get; set; }
    }

    public sealed class Placement
    {
        public double OriginXMetres { get; set; }
        public double OriginYMetres { get; set; }
        public double SizeXMetres { get; set; }
        public double SizeYMetres { get; set; }
        public double RotationDegrees { get; set; }
        public MapProfile MapProfile { get; set; } = MapProfile.Unknown;
    }

    public sealed class EndpointRef
    {
        public string CityId { get; set; } = "";
        public string EndpointKey { get; set; } = "";
    }

    /// <summary>An intercity corridor between two outside connections. Distance is a declared regional
    /// distance, NOT derived from save coordinates (D-36, §16.2).</summary>
    public sealed class LinkEntry
    {
        public string LinkId { get; set; } = "";
        public EndpointRef A { get; set; } = new EndpointRef();
        public EndpointRef B { get; set; } = new EndpointRef();
        public TransportMode Mode { get; set; }
        public double DistanceKm { get; set; }
        /// <summary>v1: links are usable in both directions when true; false means A to B only.</summary>
        public bool Bidirectional { get; set; } = true;
        public LinkStatus Status { get; set; } = LinkStatus.Proposed;
    }

    /// <summary>A city's access to the abstract external world market through one of its outside connections.</summary>
    public sealed class WorldGateway
    {
        public string CityId { get; set; } = "";
        public string EndpointKey { get; set; } = "";
        public TransportMode Mode { get; set; }
        /// <summary>Declared distance from this gate to the abstract world supplier.</summary>
        public double DistanceKm { get; set; }
    }

    /// <summary>Model parameters. All values are arbitrary test inputs, NOT game constants (§16.5).</summary>
    public sealed class ModelParameters
    {
        /// <summary>Sorted by Mode.</summary>
        public List<ModeCost> ModeCosts { get; set; } = new List<ModeCost>();
        /// <summary>Sorted by ResourceId.</summary>
        public List<ResourcePrice> ResourcePrices { get; set; } = new List<ResourcePrice>();
        public StalenessRule Staleness { get; set; } = new StalenessRule();
    }

    public sealed class ModeCost
    {
        public TransportMode Mode { get; set; }
        /// <summary>Charged once per link traversed (includes terminal handling in v1).</summary>
        public double FixedCost { get; set; }
        public double CostPerKm { get; set; }
    }

    public sealed class ResourcePrice
    {
        public string ResourceId { get; set; } = "";
        /// <summary>Price index of a domestic city producer, before freight.</summary>
        public double RegionalProducerPriceIndex { get; set; }
        /// <summary>Price index of the abstract world producer, before freight to the gateway.</summary>
        public double WorldProducerPriceIndex { get; set; }
    }

    /// <summary>Staleness is measured on the region store's ingestion counter (RegionSeq), never by comparing
    /// independent city clocks. Provisional rule; Developer A may refine it in A4 (additive change).</summary>
    public sealed class StalenessRule
    {
        /// <summary>A supplier snapshot is excluded when (currentRegionSeq - snapshot.RegionSeq) exceeds this value. null = never stale.</summary>
        public int? MaxRegionSeqAge { get; set; }
    }
}
