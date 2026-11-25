namespace Stations.Core.Sim;

/// <summary>
/// Station expansion: every stop has a level 1–3. A level sets how many vehicles can stand at the stop at once
/// (bays for buses and trams, tracks for trains – further vehicles wait in front of it), how many waiting
/// passengers fit comfortably (beyond that they give up twice as fast) and the catchment (+15 % per level).
/// </summary>
public sealed partial class Game
{
    public const int MaxStopLevel = 3;

    /// <summary>Vehicles that can stand at a stop at the same time.</summary>
    public static int Bays(Stop s) => UsesNet(s.Mode)
        ? s.Level switch { 1 => 2, 2 => 4, _ => 6 }
        : s.Level switch { 1 => 2, 2 => 4, _ => 8 };

    /// <summary>Waiting passengers that fit comfortably.</summary>
    public static int WaitingCapacity(Stop s)
    {
        int b = s.Mode switch { ModeId.Bus => 40, ModeId.Tram => 80, ModeId.Metro or ModeId.SBahn => 300, _ => 400 };
        return b * s.Level switch { 1 => 1, 2 => 3, _ => 7 };
    }

    /// <summary>Catchment factor of the level (1, 1.15, 1.3).</summary>
    public static double CatchmentFactor(Stop s) => 1 + 0.15 * (s.Level - 1);

    public static string LevelName(Stop s) => (UsesNet(s.Mode), s.Level) switch
    {
        (false, 1) => "Haltestelle",
        (false, 2) => "Haltestelle mit Wartehalle",
        (false, _) => s.Mode == ModeId.Tram ? "Tram-Knoten" : "Busbahnhof",
        (true, 1) => "1 Mittelbahnsteig",
        (true, 2) => "2 Mittelbahnsteige",
        (true, _) => "Großbahnhof",
    };

    /// <summary>Price of the next level (null at the top level).</summary>
    public static double? UpgradeCost(Stop s) => s.Portal || s.Level >= MaxStopLevel ? null
        : Modes.Get(s.Mode).StopCost * (s.Level == 1 ? 1.5 : 3);

    public bool UpgradeStop(Stop s)
    {
        if (UpgradeCost(s) is not double cost)
        {
            Notify(s.Portal ? "Ausfahrten lassen sich nicht ausbauen" : "Höchste Ausbaustufe erreicht", true);
            return false;
        }
        if (!Pay(cost)) return false;
        s.Level++;
        _routerDirty = true;
        OnChange();
        return true;
    }

    /// <summary>Are all bays/tracks of the stop taken by vehicles standing there (other than v)?</summary>
    private bool StopFull(int stopId, Vehicle v)
    {
        if (!Stops.TryGetValue(stopId, out var stop) || stop.Portal) return false;
        int bays = Bays(stop), n = 0;
        foreach (var o in Vehicles.Values)
        {
            if (o == v || o.State != VehicleState.Dwell || o.LineId is not int lid || !Lines.TryGetValue(lid, out var l)) continue;
            if (o.RunIdx >= l.Runs.Count || o.StopIdx >= l.Runs[o.RunIdx].StopIds.Count) continue;
            if (l.Runs[o.RunIdx].StopIds[o.StopIdx] == stopId && ++n >= bays) return true;
        }
        return false;
    }

    /// <summary>
    /// Approaching a stop: if it is full, stop in front of it (one vehicle length plus a gap) and wait.
    /// Returns true while the vehicle has to wait.
    /// </summary>
    private bool QueueAtStop(Vehicle v, Run r, double dt)
    {
        double target = r.Offsets[v.StopIdx];
        double before = target - v.Def.Length - 6;
        if (target - v.S > v.Def.Length + 60 || !StopFull(r.StopIds[v.StopIdx], v))
        {
            v.Queued = false;
            return false;
        }
        v.Queued = true;
        if (v.S < before) Drive(v, before, dt);
        else v.V = 0;
        return true;
    }
}
