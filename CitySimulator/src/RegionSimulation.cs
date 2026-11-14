using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;
using Regions.Engine;

namespace Regions.Simulator
{
    /// <summary>One city and resource after a period.</summary>
    public sealed class CityPeriod
    {
        public string CityId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        public bool Frozen { get; set; }
        public double? BuyTarget { get; set; }
        public double? BuyApplied { get; set; }
        public double? SellTarget { get; set; }
        public double? SellApplied { get; set; }
        public double Output { get; set; }
        public double Use { get; set; }
        public double Capacity { get; set; }
        // Phase 4 (null without people)
        public double? Population { get; set; }
        public double? UnemploymentRate { get; set; }
        public double? Vacancies { get; set; }
        public double? CommutersOut { get; set; }
        public double? CommutersIn { get; set; }
    }

    public sealed class PeriodRecord
    {
        public int Period { get; set; }
        /// <summary>Sorted by (CityId, ResourceId).</summary>
        public List<CityPeriod> Cities { get; set; } = new List<CityPeriod>();
        public CityPeriod Of(string city, string resource = "Steel") => Cities.Single(c => c.CityId == city && c.ResourceId == resource);
    }

    /// <summary>
    /// The synthetic test bench (docs/ECONOMIC_MODEL_PLAN.md §5). Each period:
    ///   1. the real engine quotes every city (targets) and SignalPlanner moves applied prices (Phase 1);
    ///   2. each active synthetic city reacts to its applied prices (GoodsDynamics, and People in Phase 4);
    ///   3. it publishes a new snapshot, exactly as the game exporter would.
    /// The engine never knows the snapshots are synthetic. Frozen cities neither react nor publish (unloaded).
    /// Deterministic: no randomness, fixed order.
    /// </summary>
    public sealed class RegionSimulation
    {
        private readonly LandedCostEngine _engine = new LandedCostEngine();

        private RegionSimulation(RegionScenario scenario, GoodsRule goodsRule, SignalRule signalRule)
        {
            Scenario = scenario;
            GoodsRule = goodsRule;
            SignalRule = signalRule;
        }

        public RegionScenario Scenario { get; }
        public GoodsRule GoodsRule { get; }
        public SignalRule SignalRule { get; }
        /// <summary>Synthetic goods state per (city, resource), sorted.</summary>
        public SortedDictionary<string, CityGoods> Goods { get; } = new SortedDictionary<string, CityGoods>(StringComparer.Ordinal);
        /// <summary>Unloaded cities: they do not react or publish, and their applied prices are frozen.</summary>
        public HashSet<string> Frozen { get; } = new HashSet<string>(StringComparer.Ordinal);
        public SignalState? Signals { get; private set; }
        public List<PeriodRecord> History { get; } = new List<PeriodRecord>();
        /// <summary>Phase 4 synthetic people per city (empty unless PeopleDynamics.Attach was called).</summary>
        public SortedDictionary<string, CityPeople> People { get; } = new SortedDictionary<string, CityPeople>(StringComparer.Ordinal);
        public PeopleRule? PeopleRule { get; set; }
        /// <summary>Phase 4 hook: called once per period after goods react, before publishing.</summary>
        public Action<RegionSimulation, int>? AfterGoods { get; set; }

        /// <summary>
        /// Builds the synthetic economy from a scenario's latest snapshots. Each measured resource becomes a CityGoods:
        /// capacity = its capacity estimate (or its output when unknown), and its use is split into a price-sensitive
        /// consumer part and an industrial part (<paramref name="industrialShare"/>).
        /// </summary>
        public static RegionSimulation From(RegionScenario scenario, GoodsRule? goods = null, SignalRule? signals = null,
                                            double industrialShare = 0.5, DemandClass consumerClass = DemandClass.Discretionary)
        {
            var sim = new RegionSimulation(scenario, goods ?? new GoodsRule(), signals ?? new SignalRule());
            foreach (var pub in scenario.LatestSnapshots)
                foreach (var obs in pub.Snapshot.Resources)
                {
                    if (obs.RealizedOutputPerGameDay is not double output || obs.InputUsePerGameDay is not double use) continue;
                    sim.Goods[Key(pub.Snapshot.CityId, obs.ResourceId)] = new CityGoods
                    {
                        CityId = pub.Snapshot.CityId, ResourceId = obs.ResourceId, Confidence = obs.Confidence,
                        Capacity = obs.CapacityEstimatePerGameDay ?? output, BaseOutput = output, Output = output,
                        IndustrialUse = use * industrialShare,
                        BaseConsumerUse = use * (1 - industrialShare), ConsumerUse = use * (1 - industrialShare),
                        InitialConsumerUse = use * (1 - industrialShare),
                        ConsumerClass = consumerClass,
                    };
                }
            return sim;
        }

        public static string Key(string city, string resource) => city + "\u0000" + resource;

        public CityGoods GoodsOf(string city, string resource = "Steel") => Goods[Key(city, resource)];

        public void Run(int periods)
        {
            for (var i = 0; i < periods; i++) Step();
        }

        public PeriodRecord Step()
        {
            var cities = Scenario.Manifest.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var active = new HashSet<string>(cities.Where(c => !Frozen.Contains(c)), StringComparer.Ordinal);
            Signals = SignalPlanner.Step(Signals, Scenario.ToState(), _engine, SignalRule, active);

            foreach (var g in Goods.Values)
            {
                if (!active.Contains(g.CityId)) continue;
                var s = Signal(g.CityId, g.ResourceId);
                var basePrice = Scenario.Manifest.Parameters.ResourcePrices.FirstOrDefault(p => p.ResourceId == g.ResourceId)?.RegionalProducerPriceIndex ?? 0;
                g.Step(s?.Buy.Applied, s?.Sell.Applied, basePrice, GoodsRule);
            }

            if (People.Count > 0) PeopleDynamics.Step(this, active);
            AfterGoods?.Invoke(this, Signals.Period);

            foreach (var city in cities.Where(active.Contains)) Republish(city);

            var record = new PeriodRecord { Period = Signals.Period };
            foreach (var g in Goods.Values)
            {
                var s = Signal(g.CityId, g.ResourceId);
                record.Cities.Add(new CityPeriod
                {
                    CityId = g.CityId, ResourceId = g.ResourceId, Frozen = !active.Contains(g.CityId),
                    BuyTarget = s?.Buy.Target, BuyApplied = s?.Buy.Applied, SellTarget = s?.Sell.Target, SellApplied = s?.Sell.Applied,
                    Output = g.Output, Use = g.TotalUse, Capacity = g.Capacity,
                    Population = People.TryGetValue(g.CityId, out var p) ? p.TotalPopulation : (double?)null,
                    UnemploymentRate = p?.UnemploymentRate, Vacancies = p?.Vacancies.Sum(),
                    CommutersOut = p?.CommutersOut, CommutersIn = p?.CommutersIn,
                });
            }
            History.Add(record);
            return record;
        }

        public CitySignal? Signal(string city, string resource = "Steel") =>
            Signals?.Signals.FirstOrDefault(x => x.CityId == city && x.ResourceId == resource);

        /// <summary>Publishes the city's next snapshot with its current synthetic figures (seq + 1, one more game day).</summary>
        private void Republish(string city)
        {
            var prev = Scenario.LatestSnapshots.FirstOrDefault(p => p.Snapshot.CityId == city)?.Snapshot;
            if (prev == null) return;
            var next = new CitySnapshot
            {
                SchemaVersion = prev.SchemaVersion, CityId = prev.CityId, Seq = prev.Seq + 1, ParentSeq = prev.Seq,
                Producer = prev.Producer, Profiles = prev.Profiles,
                GameTime = new GameTime { GameDays = prev.GameTime.GameDays + 1, RawTick = null },
                MeasurementWindowGameHours = prev.MeasurementWindowGameHours,
                OutsideConnections = prev.OutsideConnections.ToList(),
                ExportedAtUtc = null,
                Resources = prev.Resources.Select(r =>
                {
                    if (!Goods.TryGetValue(Key(city, r.ResourceId), out var g)) return r;
                    return new ResourceObservation
                    {
                        ResourceId = r.ResourceId, Confidence = r.Confidence, Source = "synthetic dynamics",
                        ProducerCompanyCount = r.ProducerCompanyCount,
                        RealizedOutputPerGameDay = g.Output, InputUsePerGameDay = g.TotalUse,
                        CapacityEstimatePerGameDay = r.CapacityEstimatePerGameDay == null ? (double?)null : g.Capacity,
                    };
                }).ToList(),
            };
            Scenario.Publish(next);
        }
    }
}
