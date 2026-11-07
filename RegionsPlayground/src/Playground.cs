using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;

namespace Regions.Playground;

/// <summary>One supplier's supply/demand market, made JSON-safe (no infinities).</summary>
public sealed class PlaygroundMarket
{
    public string CityId { get; set; } = "";
    public double BasePrice { get; set; }
    public double SpareSupply { get; set; }
    public double Slope { get; set; }
    public double Cap { get; set; }
    public double Premium { get; set; }
    public double GatePrice { get; set; }
    public double Load { get; set; }
    public double Pressure { get; set; }
    public List<PlaygroundBuyer> Buyers { get; set; } = new();
}

public sealed class PlaygroundBuyer
{
    public string CityId { get; set; } = "";
    public double Demand { get; set; }
    /// <summary>null = no other option at any price.</summary>
    public double? SwitchPrice { get; set; }
    public BuyerStatus Status { get; set; }
}

public sealed class CityAnalysis
{
    /// <summary>Steel this city needs per day (use - output, both measured, figures trusted); 0 otherwise.</summary>
    public double Demand { get; set; }
    public QuoteResult? Quote { get; set; }
    /// <summary>This city's own steel as a supplier: effective offer, exclusion reason and the factors applied (A4).</summary>
    public double? EffectiveOffer { get; set; }
    public ExcludedReason? SupplyExcluded { get; set; }
    public IReadOnlyList<string> SupplyNotes { get; set; } = Array.Empty<string>();
}

public sealed class Analysis
{
    /// <summary>Invalid input. When not empty, nothing else is filled in.</summary>
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, CityAnalysis> Cities { get; set; } = new();
    public bool RulesPassed { get; set; }
    public int QuotesChecked { get; set; }
    public List<string> Violations { get; set; } = new();
    public IReadOnlyList<PolicyForecast> Policies { get; set; } = Array.Empty<PolicyForecast>();
    /// <summary>Per supplier, when scarcity pricing is on.</summary>
    public List<PlaygroundMarket> Markets { get; set; } = new();
    public string EngineVersion { get; set; } = LandedCostEngine.EngineVersion;
}

/// <summary>
/// Bridge between the sandbox model and the real engine. Each city becomes a synthetic city with one transport gate
/// per mode it uses ("oc-road", "oc-rail", ...) plus "oc-world" for its world connection, publishing one snapshot.
/// Nothing here re-implements pricing: every number shown in the UI comes from LandedCostEngine.
/// </summary>
public static class Playground
{
    public const string Resource = "Steel";
    private const string WorldGate = "oc-world";

    public static readonly TransportMode[] Modes = { TransportMode.Road, TransportMode.Rail, TransportMode.Ship };

    public const double DefaultScarcitySlope = 0.25;
    public const double DefaultScarcityCap = 1.0;

    public static PlaygroundModel Empty() => new()
    {
        Costs = DefaultCosts(),
        ScarcityEnabled = true, ScarcitySlope = DefaultScarcitySlope, ScarcityCap = DefaultScarcityCap,
        Subsidies = Modes.Select(m => new PlaygroundSubsidy { Mode = m, Percent = 0, Budget = 5000 }).ToList(),
    };

    private static List<PlaygroundModeCost> DefaultCosts() => new()
    {
        // Log §16.5 test values, the same as the built-in scenarios. Arbitrary model inputs, not game constants.
        new() { Mode = TransportMode.Road, Fixed = 2, PerKm = 0.1 },
        new() { Mode = TransportMode.Rail, Fixed = 18, PerKm = 0.04 },
        new() { Mode = TransportMode.Ship, Fixed = 30, PerKm = 0.01 },
    };

    private static string Gate(TransportMode mode) => "oc-" + mode.ToString().ToLowerInvariant();

    public static RegionState ToState(PlaygroundModel model)
    {
        var manifest = new RegionManifest { RegionId = "sandbox", Revision = 1 };
        manifest.Parameters.Staleness.MaxRegionSeqAge = null; // every city is published fresh on each analysis

        foreach (var c in model.Cities)
            manifest.Cities.Add(new CityEntry
            {
                CityId = c.Id,
                DisplayName = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name,
                Placement = new Placement { OriginXMetres = c.X * 1000, OriginYMetres = c.Y * 1000, SizeXMetres = 14336, SizeYMetres = 14336, MapProfile = MapProfile.Vanilla },
            });

        foreach (var l in model.Links)
            manifest.Links.Add(new LinkEntry
            {
                LinkId = l.Id,
                A = new EndpointRef { CityId = l.A, EndpointKey = Gate(l.Mode) },
                B = new EndpointRef { CityId = l.B, EndpointKey = Gate(l.Mode) },
                Mode = l.Mode,
                DistanceKm = l.DistanceKm,
                Bidirectional = !l.OneWay,
                Status = l.Broken ? LinkStatus.Broken : LinkStatus.Proposed,
            });

        foreach (var c in model.Cities.Where(c => c.WorldGatewayKm != null))
            manifest.WorldGateways.Add(new WorldGateway { CityId = c.Id, EndpointKey = WorldGate, Mode = TransportMode.Road, DistanceKm = c.WorldGatewayKm!.Value });

        manifest.Parameters.ModeCosts = model.Costs.Select(c => new ModeCost { Mode = c.Mode, FixedCost = c.Fixed, CostPerKm = c.PerKm }).ToList();
        manifest.Parameters.ResourcePrices = new() { new ResourcePrice { ResourceId = Resource, RegionalProducerPriceIndex = model.RegionalPrice, WorldProducerPriceIndex = model.WorldPrice } };

        if (model.ScarcityEnabled)
            manifest.Parameters.Scarcity = new ScarcityRule { PremiumSlope = model.ScarcitySlope, MaxPremium = model.ScarcityCap };

        if (model.TariffPercent > 0)
            manifest.Parameters.Policies.ImportTariffs.Add(new ImportTariff { PolicyId = "tariff-steel", ResourceId = Resource, AdValoremPercent = model.TariffPercent });
        foreach (var s in model.Subsidies.Where(s => s.Percent > 0))
            manifest.Parameters.Policies.FreightSubsidies.Add(new FreightSubsidy
            {
                PolicyId = "subsidy-" + s.Mode.ToString().ToLowerInvariant(), ResourceId = Resource, Mode = s.Mode,
                PercentOfFreight = s.Percent, BudgetPerGameDay = s.Budget,
            });

        var snapshots = new List<PublishedSnapshot>();
        var regionSeq = 0;
        foreach (var c in model.Cities)
        {
            var city = new SyntheticCity(c.Id);
            foreach (var mode in model.Links.Where(l => l.A == c.Id || l.B == c.Id).Select(l => l.Mode).Distinct())
                city.Gate(Gate(mode), mode);
            if (c.WorldGatewayKm != null) city.Gate(WorldGate, TransportMode.Road);
            if (c.Steel != null) city.Resource(Resource, c.Steel.Output, c.Steel.InputUse, c.Steel.Capacity, c.Steel.Confidence);
            snapshots.Add(new PublishedSnapshot(city.Capture(), ++regionSeq));
        }

        return new RegionState(manifest, snapshots, regionSeq, RegionScenario.DefaultResources());
    }

    public static Analysis Analyze(PlaygroundModel model)
    {
        var result = new Analysis();
        RegionState state;
        try
        {
            state = ToState(model);
            var problems = RegionStateValidator.Validate(state);
            if (problems.Count > 0) { result.Errors.AddRange(problems); return result; }
        }
        catch (ArgumentException ex) { result.Errors.Add(ex.Message); return result; }

        var engine = new LandedCostEngine();
        try
        {
            foreach (var c in model.Cities)
            {
                var analysis = new CityAnalysis { Quote = engine.Quote(state, new QuoteRequest { DestCityId = c.Id, ResourceId = Resource }) };
                var obs = state.LatestSnapshots.First(p => p.Snapshot.CityId == c.Id).Snapshot.Resources.FirstOrDefault();
                if (obs != null)
                {
                    if (obs.Confidence != Confidence.Unknown && obs.RealizedOutputPerGameDay is double output && obs.InputUsePerGameDay is double use)
                        analysis.Demand = Math.Max(0, use - output);
                    var supply = SupplyRule.Evaluate(obs);
                    analysis.EffectiveOffer = supply.EffectiveOfferPerGameDay;
                    analysis.SupplyExcluded = supply.ExcludedReason;
                    analysis.SupplyNotes = supply.Notes;
                }
                result.Cities[c.Id] = analysis;
            }

            var report = engine.Check(state);
            result.RulesPassed = report.Passed;
            result.QuotesChecked = report.QuotesChecked;
            result.Violations = report.Violations.Select(v => v.ToString()).ToList();
            result.Policies = engine.Forecast(state);
            result.Markets = engine.Markets(state, Resource).Select(m => new PlaygroundMarket
            {
                CityId = m.CityId, BasePrice = m.BasePrice, SpareSupply = m.SpareSupply, Slope = m.PremiumSlope, Cap = m.MaxPremium,
                Premium = m.Premium, GatePrice = m.GatePrice, Load = m.Load, Pressure = m.Pressure,
                Buyers = m.Buyers.Select(b => new PlaygroundBuyer
                {
                    CityId = b.CityId, Demand = b.Demand, Status = b.Status,
                    SwitchPrice = double.IsInfinity(b.SwitchPrice) ? null : b.SwitchPrice,
                }).ToList(),
            }).ToList();
        }
        catch (RegionStateException ex)
        {
            result.Errors.AddRange(ex.Problems);
            result.Cities.Clear();
        }
        return result;
    }

    /// <summary>Converts a built-in scenario into an editable sandbox. Cities without a placement are laid out on a circle.</summary>
    public static PlaygroundModel FromScenario(RegionScenario scenario)
    {
        var m = scenario.Manifest;
        var model = new PlaygroundModel
        {
            Costs = m.Parameters.ModeCosts.Select(c => new PlaygroundModeCost { Mode = c.Mode, Fixed = c.FixedCost, PerKm = c.CostPerKm }).ToList(),
            RegionalPrice = m.Parameters.ResourcePrices.FirstOrDefault(p => p.ResourceId == Resource)?.RegionalProducerPriceIndex ?? 100,
            WorldPrice = m.Parameters.ResourcePrices.FirstOrDefault(p => p.ResourceId == Resource)?.WorldProducerPriceIndex ?? 128,
            TariffPercent = m.Parameters.Policies.ImportTariffs.FirstOrDefault(t => t.ResourceId == Resource)?.AdValoremPercent ?? 0,
            // Examples without a scarcity rule open with the sandbox default switched on, so supply and demand matter.
            ScarcityEnabled = true,
            ScarcitySlope = m.Parameters.Scarcity?.PremiumSlope ?? DefaultScarcitySlope,
            ScarcityCap = m.Parameters.Scarcity?.MaxPremium ?? DefaultScarcityCap,
            Subsidies = Modes.Select(mode =>
            {
                var s = m.Parameters.Policies.FreightSubsidies.FirstOrDefault(x => x.ResourceId == Resource && x.Mode == mode);
                return new PlaygroundSubsidy { Mode = mode, Percent = s?.PercentOfFreight ?? 0, Budget = s?.BudgetPerGameDay ?? 5000 };
            }).ToList(),
        };

        var latest = scenario.LatestSnapshots.ToDictionary(p => p.Snapshot.CityId);
        var n = m.Cities.Count;
        for (var i = 0; i < n; i++)
        {
            var c = m.Cities[i];
            var angle = Math.PI + 2 * Math.PI * i / Math.Max(1, n);
            var steel = latest.TryGetValue(c.CityId, out var pub) ? pub.Snapshot.Resources.FirstOrDefault(r => r.ResourceId == Resource) : null;
            model.Cities.Add(new PlaygroundCity
            {
                Id = c.CityId,
                Name = c.DisplayName,
                X = Math.Round(500 + 280 * Math.Cos(angle)),
                Y = Math.Round(350 + 230 * Math.Sin(angle)),
                Steel = steel == null ? null : new PlaygroundSteel
                {
                    Output = steel.RealizedOutputPerGameDay, InputUse = steel.InputUsePerGameDay,
                    Capacity = steel.CapacityEstimatePerGameDay, Confidence = steel.Confidence,
                },
                WorldGatewayKm = m.WorldGateways.FirstOrDefault(g => g.CityId == c.CityId)?.DistanceKm,
            });
        }

        foreach (var l in m.Links)
            model.Links.Add(new PlaygroundLink
            {
                Id = l.LinkId, A = l.A.CityId, B = l.B.CityId, Mode = l.Mode, DistanceKm = l.DistanceKm, AutoDistance = false,
                OneWay = !l.Bidirectional, Broken = l.Status is LinkStatus.Broken or LinkStatus.Unresolved,
            });
        return model;
    }
}
