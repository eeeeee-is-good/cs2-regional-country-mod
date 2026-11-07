using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>A city snapshot as accepted by the region store, together with the RegionSeq it was assigned.</summary>
    public sealed class PublishedSnapshot
    {
        public PublishedSnapshot(CitySnapshot snapshot, int regionSeq)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            RegionSeq = regionSeq;
        }

        public CitySnapshot Snapshot { get; }
        public int RegionSeq { get; }
    }

    /// <summary>Engine view of one row of Contracts/resources/resource-ids.v1.json. Only the fields the engine reads.</summary>
    public sealed class ResourceDefinition
    {
        public ResourceDefinition(string resourceId, IEnumerable<TransportMode> allowedFreightModes)
        {
            ResourceId = resourceId ?? throw new ArgumentNullException(nameof(resourceId));
            AllowedFreightModes = (allowedFreightModes ?? throw new ArgumentNullException(nameof(allowedFreightModes)))
                .Distinct().OrderBy(m => m).ToList();
        }

        public string ResourceId { get; }
        /// <summary>Distinct, sorted in enum order.</summary>
        public IReadOnlyList<TransportMode> AllowedFreightModes { get; }
    }

    /// <summary>
    /// Everything a quote depends on: the manifest, the latest accepted snapshot per city, the store's
    /// ingestion counter and the resource table. The engine never reads anything else (no clock, no IO).
    /// Input order does not matter: the engine sorts everything itself.
    /// </summary>
    public sealed class RegionState
    {
        public RegionState(RegionManifest manifest, IEnumerable<PublishedSnapshot> latestSnapshots,
                           int currentRegionSeq, IEnumerable<ResourceDefinition> resources)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            LatestSnapshots = (latestSnapshots ?? throw new ArgumentNullException(nameof(latestSnapshots))).ToList();
            CurrentRegionSeq = currentRegionSeq;
            Resources = (resources ?? throw new ArgumentNullException(nameof(resources))).ToList();
        }

        public RegionManifest Manifest { get; }
        /// <summary>At most one (the latest) snapshot per city.</summary>
        public IReadOnlyList<PublishedSnapshot> LatestSnapshots { get; }
        public int CurrentRegionSeq { get; }
        public IReadOnlyList<ResourceDefinition> Resources { get; }
    }

    /// <summary>Thrown when inputs are invalid. The engine fails loudly instead of clamping (log §18.4 #7).</summary>
    public sealed class RegionStateException : Exception
    {
        public RegionStateException(IReadOnlyList<string> problems)
            : base("Invalid region state:\n" + string.Join("\n", problems))
        {
            Problems = problems;
        }

        public IReadOnlyList<string> Problems { get; }
    }
}
