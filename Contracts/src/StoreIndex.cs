using System.Collections.Generic;

namespace Regions.Contracts
{
    /// <summary>
    /// The region store's ingestion index (snapshots/index.json, schema: schema/store-index.v1.schema.json).
    /// It is written only by RegionStore when it ingests files from inbox/. Exporters read it to choose the next Seq.
    /// See Contracts/README.md "Region store layout".
    /// </summary>
    public sealed class StoreIndex
    {
        public int SchemaVersion { get; set; } = SchemaVersions.StoreIndex;
        public string RegionId { get; set; } = "";
        /// <summary>Increments by 1 per accepted snapshot across all cities.</summary>
        public int CurrentRegionSeq { get; set; }
        /// <summary>Sorted by (CityId, Seq).</summary>
        public List<StoredSnapshotInfo> Snapshots { get; set; } = new List<StoredSnapshotInfo>();
    }

    public sealed class StoredSnapshotInfo
    {
        public string CityId { get; set; } = "";
        public int Seq { get; set; }
        /// <summary>Value of CurrentRegionSeq assigned when this snapshot was accepted.</summary>
        public int RegionSeq { get; set; }
        /// <summary>SHA-256 (lowercase hex) of the exact file bytes. Same (CityId, Seq) with the same hash is a no-op;
        /// with a different hash it is a conflict.</summary>
        public string FileSha256 { get; set; } = "";
    }

    public static class SchemaVersions
    {
        public const int CitySnapshot = 1;
        public const int RegionManifest = 1;
        public const int QuoteResult = 1;
        public const int CapabilityReport = 1;
        public const int StoreIndex = 1;
    }
}
