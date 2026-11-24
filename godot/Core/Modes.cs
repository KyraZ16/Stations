namespace Stations.Core;

/// <summary>The three network levels of the game.</summary>
public enum Tier { Local, City, Regional }

public enum ModeId { Bus, Tram, Metro, SBahn, Train }

/// <summary>What a mode runs on. Buses share the OSM street network.</summary>
public enum Infra { Road, TramTrack, Metro, Rail }

public sealed record VehicleDef(
    string Name,
    int Capacity,
    double MaxSpeed, // m/s
    double Price,
    double CostPerHour, // vehicle costs while out of the depot (drivers are paid separately)
    double CostPerKm,
    double Length, // m, for drawing and parking spot size
    double Width)
{
    /// <summary>Stable id used in saves (e.g. "solobus").</summary>
    public string Id { get; init; } = "";
    /// <summary>Kilometers until the condition drops from 100 % to 0 % without maintenance.</summary>
    public double LifeKm { get; init; } = 5000;
    /// <summary>Battery vehicles need charging in the depot.</summary>
    public bool Electric { get; init; }
    public double RangeKm { get; init; }
    /// <summary>Workshop cost to restore 1 % of condition.</summary>
    public double RepairCostPerPercent { get; init; } = 250;
    public string Description { get; init; } = "";
}

public sealed record ModeDef(
    ModeId Id,
    string Name,
    Tier Tier,
    string Icon,
    bool Available,
    Infra Infra,
    VehicleDef Vehicle, // default model
    IReadOnlyList<VehicleDef> Models,
    double StopCost,
    double DepotCost,
    double TrackCostPerM = 0);

public static class Modes
{
    // Vehicle models first: static fields initialize in declaration order and All uses them.
    private static readonly VehicleDef Solobus = new("Solobus", 80, 50 / 3.6, 250_000, 12, 1.1, 12, 2.55)
    {
        Id = "solobus", LifeKm = 5000, RepairCostPerPercent = 250, Description = "12 m · 80 Plätze · Diesel",
    };

    private static readonly VehicleDef Gelenkbus = new("Gelenkbus", 120, 50 / 3.6, 380_000, 14, 1.5, 18, 2.55)
    {
        Id = "gelenkbus", LifeKm = 4500, RepairCostPerPercent = 350,
        Description = "18 m · 120 Plätze · braucht Gelenkbus-Stellplatz",
    };

    private static readonly VehicleDef EBus = new("E-Bus", 75, 50 / 3.6, 520_000, 10, 0.45, 12, 2.55)
    {
        Id = "ebus", LifeKm = 6000, Electric = true, RangeKm = 250, RepairCostPerPercent = 300,
        Description = "12 m · 75 Plätze · elektrisch, 250 km Reichweite, lädt im Betriebshof",
    };

    private static readonly VehicleDef Niederflurtram = new("Niederflurtram", 180, 60 / 3.6, 2_500_000, 25, 2, 30, 2.4)
    {
        Id = "niederflurtram", LifeKm = 12000, RepairCostPerPercent = 1500, Description = "30 m · 180 Plätze",
    };

    public static readonly (Tier Id, string Name, string Desc)[] Tiers =
    {
        (Tier.Local, "Lokal", "Bus & Tram"),
        (Tier.City, "Stadtweit", "U-Bahn & S-Bahn"),
        (Tier.Regional, "Regional", "Regionalzüge"),
    };

    public static readonly IReadOnlyDictionary<ModeId, ModeDef> All = new Dictionary<ModeId, ModeDef>
    {
        [ModeId.Bus] = new(ModeId.Bus, "Bus", Tier.Local, "🚌", true, Infra.Road, Solobus, new[] { Solobus, Gelenkbus, EBus },
            5_000, 400_000),
        [ModeId.Tram] = new(ModeId.Tram, "Tram", Tier.Local, "🚋", true, Infra.TramTrack, Niederflurtram, new[] { Niederflurtram },
            40_000, 3_000_000, 2_500),
        [ModeId.Metro] = new(ModeId.Metro, "U-Bahn", Tier.City, "Ⓤ", false, Infra.Metro,
            new VehicleDef("U-Bahn-Zug", 700, 80 / 3.6, 8_000_000, 120, 6, 100, 2.6), Array.Empty<VehicleDef>(), 30_000_000, 20_000_000),
        [ModeId.SBahn] = new(ModeId.SBahn, "S-Bahn", Tier.City, "Ⓢ", false, Infra.Rail,
            new VehicleDef("S-Bahn-Zug", 900, 120 / 3.6, 10_000_000, 150, 8, 140, 3), Array.Empty<VehicleDef>(), 8_000_000, 25_000_000),
        [ModeId.Train] = new(ModeId.Train, "Regionalzug", Tier.Regional, "🚆", false, Infra.Rail,
            new VehicleDef("Triebwagen", 500, 160 / 3.6, 12_000_000, 180, 10, 120, 3), Array.Empty<VehicleDef>(), 5_000_000, 30_000_000),
    };

    public static ModeDef Get(ModeId id) => All[id];

    /// <summary>Vehicle model by id, falling back to the mode's default model.</summary>
    public static VehicleDef Model(ModeId mode, string? id) =>
        All[mode].Models.FirstOrDefault(m => m.Id == id) ?? All[mode].Vehicle;
}
