using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Simulator
{
    /// <summary>
    /// Fixed parameters of the synthetic labour, housing and business model (Phase 4). SIMULATOR ONLY: inside a real
    /// city, CS2 owns jobs, housing, companies and households. All rates are per period. D6: three skill groups.
    /// </summary>
    public sealed class PeopleRule
    {
        public const int Groups = 3; // low, middle, high qualification

        /// <summary>Industry jobs per unit of daily output, by skill group.</summary>
        public double[] JobsPerOutput { get; set; } = { 1.2, 0.6, 0.2 };
        /// <summary>Initial employment rate used to size the non-industry jobs (5% unemployment at the start).</summary>
        public double InitialEmploymentRate { get; set; } = 0.95;
        /// <summary>Housing at the start, as a share of population (5% vacant). Housing never grows by itself.</summary>
        public double InitialHousingRatio { get; set; } = 1.05;

        /// <summary>Daily commuting is possible over a direct link no longer than this.</summary>
        public double CommuteMaxKm { get; set; } = 60;

        /// <summary>Share of the unemployment-rate gap times origin population that moves to a linked city per period.</summary>
        public double MigrationRate { get; set; } = 0.25;
        /// <summary>Cap on internal departures per period, as a share of the origin group.</summary>
        public double MaxMigrationShare { get; set; } = 0.02;
        /// <summary>International arrivals per period, as a share of vacancies left after commuting (needs free housing).</summary>
        public double ImmigrationRate { get; set; } = 0.05;
        /// <summary>Above this unemployment rate, a share of the unemployed leave the country each period.</summary>
        public double EmigrationThreshold { get; set; } = 0.10;
        public double EmigrationRate { get; set; } = 0.02;

        /// <summary>Spending of an unemployed person relative to an employed one.</summary>
        public double UnemployedSpending { get; set; } = 0.6;

        /// <summary>Selling above base by more than this share attracts new capacity...</summary>
        public double EntryPremium { get; set; } = 0.02;
        /// <summary>...but only when existing capacity is nearly full (output / capacity at least this). Otherwise the
        /// existing plants simply produce more.</summary>
        public double EntryUtilisation { get; set; } = 0.8;
        public double CapacityGrowth { get; set; } = 0.05;
        /// <summary>Below this utilisation (output / capacity), capacity shrinks.</summary>
        public double ExitUtilisation { get; set; } = 0.6;
        public double CapacityShrink { get; set; } = 0.03;
        /// <summary>Industrial space at the start, as a multiple of capacity. New capacity needs space; none is built automatically.</summary>
        public double InitialSpaceRatio { get; set; } = 1.5;
    }

    /// <summary>One synthetic city's people, jobs and housing (aggregate groups, never individuals).</summary>
    public sealed class CityPeople
    {
        public string CityId { get; set; } = "";
        public double[] Population { get; set; } = new double[PeopleRule.Groups];
        /// <summary>Jobs not tied to the simulated resource (services etc.); fixed.</summary>
        public double[] OtherJobs { get; set; } = new double[PeopleRule.Groups];
        /// <summary>Homes, in people. Never grows by itself (unmet demand does not create buildings).</summary>
        public double Housing { get; set; }
        /// <summary>Industrial space, in units of daily output: the most capacity this city's buildings can hold.</summary>
        public double IndustrialSpace { get; set; }

        // Results of the last period
        public double[] Jobs { get; set; } = new double[PeopleRule.Groups];
        public double[] Employed { get; set; } = new double[PeopleRule.Groups];
        public double[] Unemployed { get; set; } = new double[PeopleRule.Groups];
        public double[] Vacancies { get; set; } = new double[PeopleRule.Groups];
        public double CommutersOut { get; set; }
        public double CommutersIn { get; set; }

        // Cumulative flows (the migration ledger, §26: domestic moves cancel across cities)
        public double InternalArrivals { get; set; }
        public double InternalDepartures { get; set; }
        public double InternationalArrivals { get; set; }
        public double InternationalDepartures { get; set; }

        /// <summary>Spending power at the start; consumption is scaled by current / initial.</summary>
        public double InitialSpending { get; set; }

        public double TotalPopulation => Population.Sum();
        public double UnemploymentRate => TotalPopulation > 0 ? Unemployed.Sum() / TotalPopulation : 0;
        public double FreeHousing => Math.Max(0, Housing - TotalPopulation);
    }

    /// <summary>
    /// Phase 4 aggregate feedback, run once per period after goods react (RegionSimulation):
    ///   jobs from output -> employment -> commuting over short links -> migration along links toward lower
    ///   unemployment, capped by free housing -> international arrivals and departures -> business entry and exit
    ///   -> consumer spending follows employment.
    /// Frozen (unloaded) cities take no part: no arrivals into them, no departures from them (§26.5).
    /// Deliberately not forced to equilibrium: lags and caps leave normal unemployment, vacancies and shortages.
    /// </summary>
    public static class PeopleDynamics
    {
        /// <summary>Gives every city in the simulation a synthetic population, sized from defaults or the arguments.</summary>
        public static void Attach(RegionSimulation sim, PeopleRule? rule = null, double[]? population = null)
        {
            sim.PeopleRule = rule ?? new PeopleRule();
            var r = sim.PeopleRule;
            foreach (var city in sim.Scenario.Manifest.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal))
            {
                var pop = (population ?? new double[] { 3000, 2000, 1000 }).ToArray();
                var output = OutputOf(sim, city);
                var p = new CityPeople { CityId = city, Population = pop, Housing = pop.Sum() * r.InitialHousingRatio };
                for (var g = 0; g < PeopleRule.Groups; g++)
                    p.OtherJobs[g] = Math.Max(0, pop[g] * r.InitialEmploymentRate - r.JobsPerOutput[g] * output);
                foreach (var goods in sim.Goods.Values.Where(x => x.CityId == city))
                    goods.SpaceCapacity = goods.Capacity * r.InitialSpaceRatio;
                p.IndustrialSpace = sim.Goods.Values.Where(x => x.CityId == city).Sum(x => x.SpaceCapacity);
                Employment(p, output, r);
                p.InitialSpending = Spending(p, r);
                sim.People[city] = p;
            }
        }

        private static double OutputOf(RegionSimulation sim, string city) => sim.Goods.Values.Where(g => g.CityId == city).Sum(g => g.Output);

        private static double Spending(CityPeople p, PeopleRule r) => p.Employed.Sum() + r.UnemployedSpending * p.Unemployed.Sum();

        private static void Employment(CityPeople p, double output, PeopleRule r)
        {
            for (var g = 0; g < PeopleRule.Groups; g++)
            {
                p.Jobs[g] = p.OtherJobs[g] + r.JobsPerOutput[g] * output;
                p.Employed[g] = Math.Min(p.Population[g], p.Jobs[g]);
                p.Unemployed[g] = p.Population[g] - p.Employed[g];
                p.Vacancies[g] = p.Jobs[g] - p.Employed[g];
            }
            p.CommutersIn = p.CommutersOut = 0;
        }

        internal static void Step(RegionSimulation sim, ISet<string> active)
        {
            var r = sim.PeopleRule!;
            var people = sim.People;
            var cities = people.Keys.Where(active.Contains).ToList(); // sorted (SortedDictionary)

            // 1. Jobs and local employment.
            foreach (var c in cities) Employment(people[c], OutputOf(sim, c), r);

            // Usable direct links between active cities, in link-id order (deterministic).
            var links = sim.Scenario.Manifest.Links
                .Where(l => l.Status is LinkStatus.Proposed or LinkStatus.Verified && active.Contains(l.A.CityId) && active.Contains(l.B.CityId)
                            && people.ContainsKey(l.A.CityId) && people.ContainsKey(l.B.CityId))
                .OrderBy(l => l.LinkId, StringComparer.Ordinal).ToList();

            // 2. Commuting: unemployed workers fill vacancies in a linked city within commuting distance. Daily, not a move.
            foreach (var l in links.Where(l => l.DistanceKm <= r.CommuteMaxKm))
                foreach (var (from, to) in new[] { (l.A.CityId, l.B.CityId), (l.B.CityId, l.A.CityId) })
                    for (var g = 0; g < PeopleRule.Groups; g++)
                    {
                        var a = people[from]; var b = people[to];
                        var n = Math.Min(a.Unemployed[g], b.Vacancies[g]);
                        if (n <= 0) continue;
                        a.Unemployed[g] -= n; a.Employed[g] += n; b.Vacancies[g] -= n;
                        a.CommutersOut += n; b.CommutersIn += n;
                    }

            // 3. Internal migration toward lower unemployment along any link, capped by free housing at the destination.
            foreach (var l in links)
                foreach (var (from, to) in new[] { (l.A.CityId, l.B.CityId), (l.B.CityId, l.A.CityId) })
                {
                    var a = people[from]; var b = people[to];
                    for (var g = 0; g < PeopleRule.Groups; g++)
                    {
                        if (a.Population[g] <= 0) continue;
                        var ua = a.Unemployed[g] / a.Population[g];
                        var ub = b.Population[g] > 0 ? b.Unemployed[g] / b.Population[g] : 0;
                        if (ua <= ub) continue;
                        var n = Math.Min(Math.Min(r.MigrationRate * (ua - ub) * a.Population[g], r.MaxMigrationShare * a.Population[g]),
                                         Math.Min(a.Unemployed[g], b.FreeHousing));
                        if (n <= 0) continue;
                        a.Population[g] -= n; a.Unemployed[g] -= n; a.InternalDepartures += n;
                        b.Population[g] += n; b.InternalArrivals += n;
                        // A newcomer takes a vacancy if there is one, otherwise is unemployed there.
                        var hired = Math.Min(n, b.Vacancies[g]);
                        b.Employed[g] += hired; b.Vacancies[g] -= hired; b.Unemployed[g] += n - hired;
                    }
                }

            // 4. International flows: arrivals fill remaining vacancies where homes are free; high unemployment sends people abroad.
            foreach (var c in cities)
            {
                var p = people[c];
                for (var g = 0; g < PeopleRule.Groups; g++)
                {
                    var arrive = Math.Min(r.ImmigrationRate * p.Vacancies[g], p.FreeHousing);
                    if (arrive > 0)
                    {
                        p.Population[g] += arrive; p.Employed[g] += arrive; p.Vacancies[g] -= arrive; p.InternationalArrivals += arrive;
                    }
                    if (p.Population[g] > 0 && p.Unemployed[g] / p.Population[g] > r.EmigrationThreshold)
                    {
                        var leave = r.EmigrationRate * p.Unemployed[g];
                        p.Population[g] -= leave; p.Unemployed[g] -= leave; p.InternationalDepartures += leave;
                    }
                }
            }

            // 5. Business entry and exit, and 6. spending feeds consumption.
            foreach (var c in cities)
            {
                var p = people[c];
                foreach (var goods in sim.Goods.Values.Where(x => x.CityId == c))
                {
                    var basePrice = sim.Scenario.Manifest.Parameters.ResourcePrices.FirstOrDefault(x => x.ResourceId == goods.ResourceId)?.RegionalProducerPriceIndex ?? 0;
                    var sell = sim.Signal(c, goods.ResourceId)?.Sell.Applied;
                    if (goods.Capacity > 0 && basePrice > 0)
                    {
                        var utilisation = goods.Output / goods.Capacity;
                        if (sell is double s && (s - basePrice) / basePrice > r.EntryPremium && utilisation >= r.EntryUtilisation)
                        {
                            // New capacity needs free industrial space and idle local workers to staff it.
                            var workersPerUnit = r.JobsPerOutput.Sum();
                            var byWorkers = workersPerUnit > 0 ? p.Unemployed.Sum() / workersPerUnit : double.PositiveInfinity;
                            var add = Math.Min(Math.Min(r.CapacityGrowth * goods.Capacity, byWorkers), Math.Max(0, goods.SpaceCapacity - goods.Capacity));
                            if (add > 0) Grow(goods, add);
                        }
                        else if (utilisation < r.ExitUtilisation)
                        {
                            // Idle plants close; production is untouched (closing idle capacity must not cut output,
                            // or output falls, utilisation falls and closures feed on themselves).
                            var cut = Math.Min(r.CapacityShrink * goods.Capacity, goods.Capacity - goods.Output);
                            if (cut > 0)
                            {
                                goods.Capacity -= cut;
                                goods.BaseOutput = Math.Min(goods.BaseOutput, goods.Capacity);
                            }
                        }
                    }
                    var spending = Spending(p, r);
                    goods.BaseConsumerUse = goods.InitialConsumerUse * (p.InitialSpending > 0 ? spending / p.InitialSpending : 1);
                }
            }
        }

        /// <summary>New capacity comes with new producers: the supply curve shifts out in proportion.</summary>
        private static void Grow(CityGoods goods, double added)
        {
            goods.BaseOutput *= (goods.Capacity + added) / goods.Capacity;
            goods.Capacity += added;
        }
    }
}
