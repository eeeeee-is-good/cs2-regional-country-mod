using System.Collections.Generic;

namespace Regions.Contracts
{
    /// <summary>
    /// One published observation of a loaded city (schema: schema/city-snapshot.v1.schema.json).
    /// Written by CS2Integration (or CitySimulator). Immutable once published.
    /// Nullable numbers mean "unknown / not measured", NEVER zero (log §25.5).
    /// </summary>
    public sealed class CitySnapshot
    {
        public int SchemaVersion { get; set; } = SchemaVersions.CitySnapshot;

        /// <summary>Region-assigned city id from the manifest. In Prototype 0 the exporter finds it by matching
        /// RegionManifest.Cities[].SaveRef. That is temporary until in-save identity is verified (V-15).</summary>
        public string CityId { get; set; } = "";

        /// <summary>Per-city publication sequence, starting at 1, strictly increasing.</summary>
        public int Seq { get; set; }

        /// <summary>The latest seq the exporter saw for this city when it wrote this snapshot; null for the first.
        /// A mismatch with the store's latest seq means a conflicting/forked publication (log §22.6).</summary>
        public int? ParentSeq { get; set; }

        public SnapshotProducer Producer { get; set; } = new SnapshotProducer();
        public SnapshotProfiles Profiles { get; set; } = new SnapshotProfiles();
        public GameTime GameTime { get; set; } = new GameTime();

        /// <summary>Length of the in-game window the rates were measured over. null = unknown.</summary>
        public double? MeasurementWindowGameHours { get; set; }

        /// <summary>Sorted by ResourceId (ordinal), so identical state yields identical bytes.</summary>
        public List<ResourceObservation> Resources { get; set; } = new List<ResourceObservation>();

        /// <summary>Sorted by EndpointKey (ordinal).</summary>
        public List<OutsideConnectionInfo> OutsideConnections { get; set; } = new List<OutsideConnectionInfo>();

        /// <summary>Informational only. The engine MUST NOT read this (determinism).</summary>
        public string? ExportedAtUtc { get; set; }
    }

    public sealed class SnapshotProducer
    {
        /// <summary>"CS2Integration" or "CitySimulator".</summary>
        public string Tool { get; set; } = "";
        public string ToolVersion { get; set; } = "";
        /// <summary>Game build string as reported by the game; null for synthetic snapshots.</summary>
        public string? GameBuild { get; set; }
        /// <summary>SHA-256 (lowercase hex) of the CapabilityReport file in force at export; null if none.</summary>
        public string? CapabilityReportSha256 { get; set; }
    }

    /// <summary>Mod profiles that change the meaning of statistics (log §24.3, §25.5).</summary>
    public sealed class SnapshotProfiles
    {
        public MapProfile Map { get; set; } = MapProfile.Unknown;
        public EconomyProfile Economy { get; set; } = EconomyProfile.Unknown;
        public MobilityProfile Mobility { get; set; } = MobilityProfile.Unknown;
        public OccupancyProfile Occupancy { get; set; } = OccupancyProfile.Unknown;
        public CompanyControlProfile CompanyControl { get; set; } = CompanyControlProfile.Unknown;
        public FactoryProductionProfile FactoryProduction { get; set; } = FactoryProductionProfile.Unknown;
        /// <summary>Hash of relevant mod versions + settings; null if not computed.</summary>
        public string? SettingsFingerprint { get; set; }
    }

    /// <summary>The city's LOCAL simulated time. Cities' clocks are independent and must not be compared
    /// across cities (log §8A). Used only to detect rollback within one city.</summary>
    public sealed class GameTime
    {
        /// <summary>In-game days elapsed since city start; null if not determinable (game API unverified).</summary>
        public double? GameDays { get; set; }
        /// <summary>Raw simulation frame/tick if available; informational.</summary>
        public long? RawTick { get; set; }
    }

    public sealed class ResourceObservation
    {
        /// <summary>Project resource id from resources/resource-ids.v1.json (not the game's enum name).</summary>
        public string ResourceId { get; set; } = "";
        /// <summary>Measured production over the window, per in-game day, in game resource units (V-10). null = unknown.</summary>
        public double? RealizedOutputPerGameDay { get; set; }
        /// <summary>Measured or estimated local use per in-game day. null = unknown. Do not derive as production - surplus (§14.3).</summary>
        public double? InputUsePerGameDay { get; set; }
        /// <summary>Conservative sustainable capacity estimate. null = unknown. Not inventory (§25.3).</summary>
        public double? CapacityEstimatePerGameDay { get; set; }
        public int? ProducerCompanyCount { get; set; }
        public Confidence Confidence { get; set; } = Confidence.Unknown;
        /// <summary>Free-text provenance, e.g. "CityProductionStatistic windowed" or "synthetic".</summary>
        public string Source { get; set; } = "";
    }

    public sealed class OutsideConnectionInfo
    {
        /// <summary>Opaque, stable per-city key, built by CS2Integration from a position/type fingerprint (V-08).
        /// NEVER an ECS Entity index/version. Unique within the city.</summary>
        public string EndpointKey { get; set; } = "";
        public ConnectionKind Kind { get; set; }
        /// <summary>Transport modes for Kind=Transport; empty for utilities. Sorted in enum order.</summary>
        public List<TransportMode> TransportModes { get; set; } = new List<TransportMode>();
        /// <summary>Map-local position in metres. The frame convention is unverified (V-28); null if not read.</summary>
        public Vector3Metres? PositionMetres { get; set; }
        public bool? CarriesCargo { get; set; }
        public bool? CarriesPassengers { get; set; }
    }

    public sealed class Vector3Metres
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }
}
