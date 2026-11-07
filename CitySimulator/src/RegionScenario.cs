using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;
using Regions.Engine;

namespace Regions.Simulator
{
    /// <summary>
    /// A synthetic region: a manifest plus the latest published snapshot per city, as a region store would hold them.
    /// Publish() behaves like an accepted ingestion: CurrentRegionSeq += 1 and the snapshot gets that RegionSeq.
    /// Duplicate detection and conflicts are RegionStore's job (A2) and are not modeled here.
    /// </summary>
    public sealed class RegionScenario
    {
        public RegionScenario(string name, string description)
        {
            Name = name;
            Description = description;
            Manifest = new RegionManifest
            {
                RegionId = "region-" + name.ToLowerInvariant(),
                Revision = 1,
                Parameters = new ModelParameters
                {
                    // Log §16.5 test values. Arbitrary model inputs, NOT game constants.
                    ModeCosts =
                    {
                        new ModeCost { Mode = TransportMode.Road, FixedCost = 2, CostPerKm = 0.1 },
                        new ModeCost { Mode = TransportMode.Rail, FixedCost = 18, CostPerKm = 0.04 },
                        new ModeCost { Mode = TransportMode.Ship, FixedCost = 30, CostPerKm = 0.01 },
                    },
                    ResourcePrices = { new ResourcePrice { ResourceId = "Steel", RegionalProducerPriceIndex = 100, WorldProducerPriceIndex = 128 } },
                    Staleness = new StalenessRule { MaxRegionSeqAge = 10 },
                },
            };
        }

        public string Name { get; }
        public string Description { get; }
        public RegionManifest Manifest { get; }
        public int CurrentRegionSeq { get; private set; }
        public List<ResourceDefinition> Resources { get; } = DefaultResources();

        private readonly SortedDictionary<string, PublishedSnapshot> _latest = new SortedDictionary<string, PublishedSnapshot>(StringComparer.Ordinal);
        public IReadOnlyList<PublishedSnapshot> LatestSnapshots => _latest.Values.ToList();

        /// <summary>Mirrors Contracts/resources/resource-ids.v1.json (a test checks they agree).</summary>
        public static List<ResourceDefinition> DefaultResources() => new List<ResourceDefinition>
        {
            new ResourceDefinition("Steel", new[] { TransportMode.Road, TransportMode.Rail, TransportMode.Ship }),
        };

        public RegionScenario City(string cityId, string displayName)
        {
            Manifest.Cities.Add(new CityEntry { CityId = cityId, DisplayName = displayName, SaveRef = "synthetic-" + cityId });
            return this;
        }

        public RegionScenario Link(string linkId, string cityA, string endpointA, string cityB, string endpointB,
                                   TransportMode mode, double distanceKm, bool bidirectional = true, LinkStatus status = LinkStatus.Proposed)
        {
            Manifest.Links.Add(new LinkEntry
            {
                LinkId = linkId,
                A = new EndpointRef { CityId = cityA, EndpointKey = endpointA },
                B = new EndpointRef { CityId = cityB, EndpointKey = endpointB },
                Mode = mode,
                DistanceKm = distanceKm,
                Bidirectional = bidirectional,
                Status = status,
            });
            return this;
        }

        public RegionScenario WorldGateway(string cityId, string endpointKey, TransportMode mode, double distanceKm)
        {
            Manifest.WorldGateways.Add(new WorldGateway { CityId = cityId, EndpointKey = endpointKey, Mode = mode, DistanceKm = distanceKm });
            return this;
        }

        /// <summary>Accepts a snapshot as the city's latest and returns the RegionSeq it was assigned.</summary>
        public int Publish(CitySnapshot snapshot)
        {
            CurrentRegionSeq += 1;
            _latest[snapshot.CityId] = new PublishedSnapshot(snapshot, CurrentRegionSeq);
            return CurrentRegionSeq;
        }

        public RegionScenario Publish(params SyntheticCity[] cities)
        {
            foreach (var c in cities) Publish(c.Capture());
            return this;
        }

        public RegionState ToState() => new RegionState(Manifest, _latest.Values, CurrentRegionSeq, Resources);

        /// <summary>Sorts every documented manifest array (Contracts/README.md rule 5) so the manifest can be written as a valid fixture.</summary>
        public RegionScenario SortManifest()
        {
            Sort(Manifest.Cities, (x, y) => string.CompareOrdinal(x.CityId, y.CityId));
            Sort(Manifest.Links, (x, y) => string.CompareOrdinal(x.LinkId, y.LinkId));
            Sort(Manifest.WorldGateways, (x, y) =>
            {
                var c = string.CompareOrdinal(x.CityId, y.CityId);
                return c != 0 ? c : string.CompareOrdinal(x.EndpointKey, y.EndpointKey);
            });
            Sort(Manifest.Parameters.ModeCosts, (x, y) => x.Mode.CompareTo(y.Mode));
            Sort(Manifest.Parameters.ResourcePrices, (x, y) => string.CompareOrdinal(x.ResourceId, y.ResourceId));
            return this;
        }

        // Stable sort (List.Sort is not stable).
        private static void Sort<T>(List<T> list, Comparison<T> cmp)
        {
            var sorted = list.Select((item, i) => (item, i)).OrderBy(x => x.item, Comparer<T>.Create(cmp)).ThenBy(x => x.i).Select(x => x.item).ToList();
            list.Clear();
            list.AddRange(sorted);
        }
    }
}
