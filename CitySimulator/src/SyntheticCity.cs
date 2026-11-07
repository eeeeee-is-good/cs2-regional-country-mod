using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Simulator
{
    /// <summary>
    /// A hand-described city that publishes CitySnapshots the same way the CS2 exporter will (B5):
    /// seq starts at 1 and increments per Capture, parentSeq is the previous seq, and lists are sorted
    /// as Contracts/README.md requires. Values are declared, not simulated: nothing grows or accumulates
    /// between captures (log §16.6, ARCHITECTURE.md N5).
    /// </summary>
    public sealed class SyntheticCity
    {
        public const string ToolName = "CitySimulator";
        public const string ToolVersion = "0.1.0";

        private readonly SortedDictionary<string, OutsideConnectionInfo> _gates = new SortedDictionary<string, OutsideConnectionInfo>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, ResourceObservation> _resources = new SortedDictionary<string, ResourceObservation>(StringComparer.Ordinal);

        public SyntheticCity(string cityId)
        {
            if (string.IsNullOrEmpty(cityId)) throw new ArgumentException("cityId is required", nameof(cityId));
            CityId = cityId;
        }

        public string CityId { get; }
        /// <summary>Seq of the last captured snapshot; 0 before the first capture.</summary>
        public int LastSeq { get; private set; }
        /// <summary>The city's own clock. null = unknown (as for a real exporter that cannot read it yet).</summary>
        public double? GameDays { get; set; } = 0;
        public double? MeasurementWindowGameHours { get; set; } = 168;

        /// <summary>A transport outside connection (road/rail/ship/air gate).</summary>
        public SyntheticCity Gate(string endpointKey, TransportMode mode, bool? carriesCargo = true)
        {
            _gates[endpointKey] = new OutsideConnectionInfo
            {
                EndpointKey = endpointKey,
                Kind = ConnectionKind.Transport,
                TransportModes = new List<TransportMode> { mode },
                CarriesCargo = carriesCargo,
                CarriesPassengers = true,
            };
            return this;
        }

        /// <summary>A utility connection (electricity, water, sewage). Never freight.</summary>
        public SyntheticCity Utility(string endpointKey, ConnectionKind kind)
        {
            if (kind == ConnectionKind.Transport) throw new ArgumentException("use Gate for transport connections", nameof(kind));
            _gates[endpointKey] = new OutsideConnectionInfo { EndpointKey = endpointKey, Kind = kind };
            return this;
        }

        /// <summary>Declares this city's observed figures for a resource, per game day. null = unknown, never zero.</summary>
        public SyntheticCity Resource(string resourceId, double? output, double? inputUse, double? capacity,
                                      Confidence confidence = Confidence.Validated, int? producerCompanyCount = null)
        {
            _resources[resourceId] = new ResourceObservation
            {
                ResourceId = resourceId,
                RealizedOutputPerGameDay = output,
                InputUsePerGameDay = inputUse,
                CapacityEstimatePerGameDay = capacity,
                ProducerCompanyCount = producerCompanyCount,
                Confidence = confidence,
                Source = "synthetic",
            };
            return this;
        }

        public SyntheticCity RemoveResource(string resourceId) { _resources.Remove(resourceId); return this; }

        /// <summary>Moves this city's clock forward. Nothing else changes: there is no production between captures.</summary>
        public SyntheticCity Advance(double gameDays)
        {
            if (gameDays < 0) throw new ArgumentOutOfRangeException(nameof(gameDays), "a city clock never runs backwards");
            if (GameDays != null) GameDays += gameDays;
            return this;
        }

        /// <summary>Publishes the next snapshot. Each call returns a fresh object; later edits to this city do not change it.</summary>
        public CitySnapshot Capture()
        {
            var snapshot = new CitySnapshot
            {
                CityId = CityId,
                Seq = LastSeq + 1,
                ParentSeq = LastSeq == 0 ? (int?)null : LastSeq,
                Producer = new SnapshotProducer { Tool = ToolName, ToolVersion = ToolVersion },
                Profiles = new SnapshotProfiles
                {
                    Map = MapProfile.Vanilla, Economy = EconomyProfile.Vanilla, Mobility = MobilityProfile.Vanilla,
                    Occupancy = OccupancyProfile.Vanilla, CompanyControl = CompanyControlProfile.Vanilla,
                    FactoryProduction = FactoryProductionProfile.Vanilla,
                },
                GameTime = new GameTime { GameDays = GameDays },
                MeasurementWindowGameHours = MeasurementWindowGameHours,
                Resources = _resources.Values.Select(Copy).ToList(),
                OutsideConnections = _gates.Values.Select(Copy).ToList(),
                ExportedAtUtc = null, // no clock reads
            };
            LastSeq = snapshot.Seq;
            return snapshot;
        }

        private static ResourceObservation Copy(ResourceObservation r) => new ResourceObservation
        {
            ResourceId = r.ResourceId,
            RealizedOutputPerGameDay = r.RealizedOutputPerGameDay,
            InputUsePerGameDay = r.InputUsePerGameDay,
            CapacityEstimatePerGameDay = r.CapacityEstimatePerGameDay,
            ProducerCompanyCount = r.ProducerCompanyCount,
            Confidence = r.Confidence,
            Source = r.Source,
        };

        private static OutsideConnectionInfo Copy(OutsideConnectionInfo o) => new OutsideConnectionInfo
        {
            EndpointKey = o.EndpointKey,
            Kind = o.Kind,
            TransportModes = o.TransportModes.OrderBy(m => m).ToList(),
            PositionMetres = o.PositionMetres == null ? null : new Vector3Metres { X = o.PositionMetres.X, Y = o.PositionMetres.Y, Z = o.PositionMetres.Z },
            CarriesCargo = o.CarriesCargo,
            CarriesPassengers = o.CarriesPassengers,
        };
    }
}
