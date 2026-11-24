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
    double Length, // m, for drawing
    double Width);

public sealed record ModeDef(
    ModeId Id,
    string Name,
    Tier Tier,
    string Icon,
    bool Available,
    Infra Infra,
    VehicleDef Vehicle,
    double StopCost,
    double DepotCost,
    double TrackCostPerM = 0);

public static class Modes
{
    public static readonly (Tier Id, string Name, string Desc)[] Tiers =
    {
        (Tier.Local, "Lokal", "Bus & Tram"),
        (Tier.City, "Stadtweit", "U-Bahn & S-Bahn"),
        (Tier.Regional, "Regional", "Regionalzüge"),
    };

    public static readonly IReadOnlyDictionary<ModeId, ModeDef> All = new Dictionary<ModeId, ModeDef>
    {
        [ModeId.Bus] = new(ModeId.Bus, "Bus", Tier.Local, "🚌", true, Infra.Road,
            new VehicleDef("Solobus", 80, 50 / 3.6, 250_000, 12, 1.1, 12, 2.55), 5_000, 400_000),
        [ModeId.Tram] = new(ModeId.Tram, "Tram", Tier.Local, "🚋", true, Infra.TramTrack,
            new VehicleDef("Niederflurtram", 180, 60 / 3.6, 2_500_000, 25, 2, 30, 2.4), 40_000, 3_000_000, 2_500),
        [ModeId.Metro] = new(ModeId.Metro, "U-Bahn", Tier.City, "Ⓤ", false, Infra.Metro,
            new VehicleDef("U-Bahn-Zug", 700, 80 / 3.6, 8_000_000, 120, 6, 100, 2.6), 30_000_000, 20_000_000),
        [ModeId.SBahn] = new(ModeId.SBahn, "S-Bahn", Tier.City, "Ⓢ", false, Infra.Rail,
            new VehicleDef("S-Bahn-Zug", 900, 120 / 3.6, 10_000_000, 150, 8, 140, 3), 8_000_000, 25_000_000),
        [ModeId.Train] = new(ModeId.Train, "Regionalzug", Tier.Regional, "🚆", false, Infra.Rail,
            new VehicleDef("Triebwagen", 500, 160 / 3.6, 12_000_000, 180, 10, 120, 3), 5_000_000, 30_000_000),
    };

    public static ModeDef Get(ModeId id) => All[id];
}
