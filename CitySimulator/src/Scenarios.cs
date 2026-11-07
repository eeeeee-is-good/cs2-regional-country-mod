using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Simulator
{
    /// <summary>
    /// Named synthetic regions for the golden scenarios in log §18.6 that the engine can already express.
    /// Every scenario quotes Steel delivered to "carford". Numbers are the log §16.5 test values.
    /// Expected outcomes are asserted in Tests/Scenarios.Tests, not here.
    /// </summary>
    public static class Scenarios
    {
        public const string Destination = "carford";
        public const string Resource = "Steel";

        public sealed class Entry
        {
            public Entry(string name, string description, Func<RegionScenario> build) { Name = name; Description = description; Build = build; }
            public string Name { get; }
            public string Description { get; }
            public Func<RegionScenario> Build { get; }
        }

        public static IReadOnlyList<Entry> All { get; } = new List<Entry>
        {
            new Entry("G01", "Two cities, no link: the producer has no influence; Carford falls back to the world market (140).", G01),
            new Entry("G02", "Two cities, 50 km road: Ironvale steel lands at 107 and beats the world (140).", G02),
            new Entry("G03", "Road vs rail over 400 km: road 142, rail 134, world 140. Rail wins.", G03),
            new Entry("G10", "Edge cases: zero and huge distances, a mode with no cost, one-way links and a cycle. No NaN, no infinite loop.", G10),
            new Entry("corridor", "Steel from Ironvale reaches Carford through the transit city Midway (rail then road). Lonepeak is cut off.", Corridor),
        };

        public static RegionScenario Build(string name) =>
            (All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
             ?? throw new ArgumentException($"unknown scenario '{name}'. Known: {string.Join(", ", All.Select(e => e.Name))}")).Build();

        // Ironvale: steel producer with road and rail gates on its east side. Surplus 300/day.
        public static SyntheticCity Ironvale() => new SyntheticCity("ironvale")
            .Gate("oc-road-east-01", TransportMode.Road)
            .Gate("oc-rail-east-01", TransportMode.Rail)
            .Utility("oc-power-north-01", ConnectionKind.Electricity)
            .Resource("Steel", output: 1000, inputUse: 700, capacity: 1200, producerCompanyCount: 4);

        // Carford: steel consumer with west gates toward Ironvale and a southern road gate to the world.
        public static SyntheticCity Carford() => new SyntheticCity("carford")
            .Gate("oc-road-west-01", TransportMode.Road)
            .Gate("oc-rail-west-01", TransportMode.Rail)
            .Gate("oc-road-south-01", TransportMode.Road)
            .Resource("Steel", output: 0, inputUse: 500, capacity: null, confidence: Confidence.Provisional, producerCompanyCount: 0);

        private static RegionScenario TwoCities(string name, string description)
        {
            var s = new RegionScenario(name, description)
                .City("carford", "Carford (consumer)")
                .City("ironvale", "Ironvale (producer)")
                .WorldGateway("carford", "oc-road-south-01", TransportMode.Road, 100);
            return s.Publish(Ironvale(), Carford());
        }

        private static RegionScenario G01() => TwoCities("G01", All[0].Description);

        private static RegionScenario G02() => TwoCities("G02", All[1].Description)
            .Link("ic-road-01", "ironvale", "oc-road-east-01", "carford", "oc-road-west-01", TransportMode.Road, 50);

        private static RegionScenario G03() => TwoCities("G03", All[2].Description)
            .Link("ic-rail-01", "ironvale", "oc-rail-east-01", "carford", "oc-rail-west-01", TransportMode.Rail, 400)
            .Link("ic-road-01", "ironvale", "oc-road-east-01", "carford", "oc-road-west-01", TransportMode.Road, 400);

        private static RegionScenario G10()
        {
            var s = new RegionScenario("G10", All[3].Description)
                .City("carford", "Carford (consumer)")
                .City("ironvale", "Ironvale (producer)")
                .City("loopton", "Loopton (cycle)")
                .City("farreach", "Farreach (very far)")
                .WorldGateway("carford", "oc-road-south-01", TransportMode.Road, 0)
                // Cycle ironvale -> loopton -> carford -> loopton, zero-length road links.
                .Link("e01-zero", "ironvale", "oc-road-east-01", "loopton", "oc-road-1", TransportMode.Road, 0)
                .Link("e02-zero", "loopton", "oc-road-2", "carford", "oc-road-west-01", TransportMode.Road, 0)
                // One-way rail into ironvale only: cannot carry steel out of ironvale.
                .Link("e03-oneway", "carford", "oc-rail-west-01", "ironvale", "oc-rail-east-01", TransportMode.Rail, 10, bidirectional: false)
                // Air has no ModeCost in the parameters: never usable.
                .Link("e04-air", "farreach", "oc-air-1", "carford", "oc-road-west-01", TransportMode.Air, 10)
                // Huge but finite distance.
                .Link("e05-huge", "farreach", "oc-rail-1", "loopton", "oc-rail-1", TransportMode.Rail, 1e9);

            var loopton = new SyntheticCity("loopton")
                .Gate("oc-road-1", TransportMode.Road).Gate("oc-road-2", TransportMode.Road).Gate("oc-rail-1", TransportMode.Rail);
            var farreach = new SyntheticCity("farreach")
                .Gate("oc-air-1", TransportMode.Air).Gate("oc-rail-1", TransportMode.Rail)
                .Resource("Steel", output: 5000, inputUse: 0, capacity: 5000, producerCompanyCount: 9);
            return s.Publish(Ironvale(), Carford(), loopton, farreach);
        }

        private static RegionScenario Corridor()
        {
            var s = new RegionScenario("corridor", All[4].Description)
                .City("carford", "Carford (consumer)")
                .City("ironvale", "Ironvale (producer)")
                .City("lonepeak", "Lonepeak (isolated)")
                .City("midway", "Midway (transit)")
                .WorldGateway("carford", "oc-road-south-01", TransportMode.Road, 100)
                .Link("im-rail-01", "ironvale", "oc-rail-east-01", "midway", "oc-rail-west-01", TransportMode.Rail, 300)
                .Link("mc-road-01", "midway", "oc-road-east-01", "carford", "oc-road-west-01", TransportMode.Road, 60);

            var midway = new SyntheticCity("midway")
                .Gate("oc-rail-west-01", TransportMode.Rail).Gate("oc-road-east-01", TransportMode.Road);
            var lonepeak = new SyntheticCity("lonepeak")
                .Gate("oc-road-north-01", TransportMode.Road)
                .Resource("Steel", output: 5000, inputUse: 100, capacity: 6000, producerCompanyCount: 12);
            return s.Publish(Ironvale(), Carford(), midway, lonepeak);
        }
    }
}
