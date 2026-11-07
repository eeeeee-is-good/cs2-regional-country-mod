using Regions.Contracts;

namespace Regions.Playground;

/// <summary>
/// The sandbox's editable region, as the browser holds it. One resource (Steel) in this version.
/// Positions are atlas kilometres (map spec "Freeform atlas"); they are a layout, not game coordinates.
/// The server is stateless: the browser posts the whole model and gets the engine's analysis back.
/// </summary>
public sealed class PlaygroundModel
{
    public List<PlaygroundCity> Cities { get; set; } = new();
    public List<PlaygroundLink> Links { get; set; } = new();
    public List<PlaygroundModeCost> Costs { get; set; } = new();
    public double RegionalPrice { get; set; } = 100;
    public double WorldPrice { get; set; } = 128;
    /// <summary>Import tariff on world steel, percent of the world price. 0 = none.</summary>
    public double TariffPercent { get; set; }
    public List<PlaygroundSubsidy> Subsidies { get; set; } = new();
    /// <summary>Supply/demand pricing (manifest parameters.scarcity). On by default in the sandbox.</summary>
    public bool ScarcityEnabled { get; set; } = true;
    /// <summary>Price rise as a fraction of the producer price per 100% overload.</summary>
    public double ScarcitySlope { get; set; } = 0.25;
    /// <summary>Maximum rise as a fraction of the producer price.</summary>
    public double ScarcityCap { get; set; } = 1.0;
}

public sealed class PlaygroundCity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>null = this city reports no steel at all.</summary>
    public PlaygroundSteel? Steel { get; set; }
    /// <summary>Distance from this city's road gate to the world market; null = no world connection.</summary>
    public double? WorldGatewayKm { get; set; }
}

/// <summary>Per game day. null means unknown, never zero (Contracts rule 3).</summary>
public sealed class PlaygroundSteel
{
    public double? Output { get; set; }
    public double? InputUse { get; set; }
    public double? Capacity { get; set; }
    public Confidence Confidence { get; set; } = Confidence.Validated;
}

public sealed class PlaygroundLink
{
    public string Id { get; set; } = "";
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public TransportMode Mode { get; set; }
    public double DistanceKm { get; set; }
    /// <summary>UI only: the browser keeps the distance equal to the map distance while this is true.</summary>
    public bool AutoDistance { get; set; }
    /// <summary>Goods move from A to B only.</summary>
    public bool OneWay { get; set; }
    public bool Broken { get; set; }
}

public sealed class PlaygroundModeCost
{
    public TransportMode Mode { get; set; }
    public double Fixed { get; set; }
    public double PerKm { get; set; }
}

public sealed class PlaygroundSubsidy
{
    public TransportMode Mode { get; set; }
    public double Percent { get; set; }
    public double Budget { get; set; }
}
