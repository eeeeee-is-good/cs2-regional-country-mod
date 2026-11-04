// Project-owned enums. These are NOT game enums. Values serialize as their exact
// PascalCase names (string enums). Never serialize as integers.
// Adding a member is a non-breaking change within a schema version; renaming or removing one is breaking.

namespace Regions.Contracts
{
    /// <summary>Freight/passenger transport mode of a link or outside connection.
    /// The game's OutsideConnectionTransferType flags (Road=1, Train=2, Air=4, Ship=0x10) map here; that mapping is unverified (V-06).</summary>
    public enum TransportMode { Road, Rail, Ship, Air }

    /// <summary>Outside-connection kind. Utility connections are never freight (log §9 regression list).</summary>
    public enum ConnectionKind { Transport, Electricity, Water, Sewage }

    public enum Confidence { Validated, Provisional, Unknown }

    public enum LinkStatus { Proposed, Verified, Broken, Unresolved }

    public enum MapProfile { Vanilla, MapExt28, MapExt43, MapExt57, Unknown }

    public enum EconomyProfile { Vanilla, MapExt, EconomyEX, Unknown }

    public enum MobilityProfile { Vanilla, RealisticTrips, RTT, Unknown }

    public enum OccupancyProfile { Vanilla, RWH, AdvancedBuildingControl, Mixed, Unknown }

    public enum CompanyControlProfile { Vanilla, ChangeCompany, Mixed, Unknown }

    public enum FactoryProductionProfile { Vanilla, SignatureLogistics, IndustriesExtended, Mixed, Unknown }

    public enum OriginKind { City, World }

    public enum ExcludedReason
    {
        NoPath,            // no valid link path from supplier to destination (D-33)
        ModeIncompatible,  // a path exists but not in a mode allowed for this resource
        StaleSnapshot,     // supplier snapshot is older than the staleness threshold
        NoSupply,          // supplier's effective regional offer is <= 0
        UnknownSupply,     // supplier data is null/unknown: never treated as zero or as supply
        NoWorldGateway,    // destination has no eligible world gateway
        UnknownResource    // resource id not in the resource table / no price parameter
    }

    /// <summary>What a quote claims to be. Only one value exists in v1, on purpose (D-20, D-35).</summary>
    public enum ReportedAs { ModeledPriceQuoteNotPhysicalTrade }

    /// <summary>Feature modes from log §23.4.</summary>
    public enum Feature
    {
        RegistryRead, TransportGraph, MarketShadow, MarketWrite,
        FiscalShadow, FiscalWrite, PopulationShadow, PopulationWrite
    }

    public enum FeatureStatus { Enabled, ShadowOnly, Disabled }
}
