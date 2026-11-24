using Stations.Core.Map;

namespace Stations.Core.Sim;

public sealed record TrackPlan(SegRouteResult Route, List<int> Segs, double NewLength, double Cost);

/// <summary>
/// Complete game state and simulation: building, lines, vehicles, staff, passengers, economy.
/// Has no engine dependency; the Godot layer calls <see cref="Update"/> every frame and renders the state.
/// </summary>
public sealed partial class Game
{
    public static readonly double[] Speeds = { 0, 5, 20, 60 }; // game seconds per real second
    public const double Fare = 2.8;
    private const double MinLayover = 60;
    private const double DwellEstimate = 15;
    private const double MaxWait = 40 * 60;
    private const double TripsPerResident = 6;
    private const double Accel = 1.2;
    private const double Brake = 1.0;

    /// <summary>Max driving time per driver and day.</summary>
    public const double ShiftMax = 8 * 3600;
    /// <summary>A driver only starts a new trip with at least this much shift time left.</summary>
    public const double ShiftMinLeft = 45 * 60;
    public const double DriverWageDay = 190;
    public const double DriverHireCost = 1_500;
    /// <summary>Vehicles leave the depot this long before the timetable needs them.</summary>
    private const double DispatchLead = 15 * 60;
    private const double DispatchInterval = 15;

    public MapData Map { get; }
    public Random Rng { get; }

    public double Time = 5 * 3600;
    public int Speed = 1;
    public double Money;
    public bool Sandbox;

    public readonly Dictionary<int, Stop> Stops = new();
    public readonly Dictionary<int, Depot> Depots = new();
    public readonly Dictionary<int, TransitLine> Lines = new();
    public readonly Dictionary<int, Vehicle> Vehicles = new();
    public readonly Dictionary<int, Driver> Drivers = new();

    /// <summary>Tram tracks: road segments with track laid on them.</summary>
    public readonly HashSet<int> Tracks = new();
    /// <summary>Incremented when tracks change (renderer cache).</summary>
    public int TrackVersion;

    public DayStats Today;
    public List<DayStats> History = new();

    public Router? Router { get; private set; }
    /// <summary>Incremented whenever the served network changes (for overlays).</summary>
    public int NetworkVersion { get; private set; }

    private bool _routerDirty = true;
    private double _demandAcc;
    private double _expireTimer;
    private double _dispatchTimer;
    private string _headwaySig = "";
    private int _nextId = 1;
    private int _vehicleCounter;
    private int _passengerId = 1;

    /// <summary>Message for the player; the bool is true for errors.</summary>
    public event Action<string, bool>? Toast;
    /// <summary>Structural change (something added/removed) – UI should re-render lists.</summary>
    public event Action? Changed;

    public Game(MapData map, bool sandbox, double money = 5_000_000, int? seed = null)
    {
        Map = map;
        Sandbox = sandbox;
        Money = money;
        Rng = seed is int s ? new Random(s) : new Random();
        Today = new DayStats { Day = Day };
    }

    public int Day => (int)Math.Floor(Time / 86400) + 1;

    private void Notify(string msg, bool error = false) => Toast?.Invoke(msg, error);
    private void OnChange() => Changed?.Invoke();

    // ---------------------------------------------------------------- economy

    public bool CanAfford(double cost) => Sandbox || Money >= cost;

    private bool Pay(double cost)
    {
        if (!CanAfford(cost))
        {
            Notify("Nicht genug Geld", true);
            return false;
        }
        Money -= cost;
        return true;
    }

    // ---------------------------------------------------------------- building

    /// <summary>Nearest stop within radius; with a mode only stops of that mode.</summary>
    public Stop? StopAt(double x, double y, double radius, ModeId? mode = null)
    {
        Stop? best = null;
        double bd = radius;
        foreach (var s in Stops.Values)
        {
            if (mode != null && s.Mode != mode) continue;
            double d = Util.Hypot(s.Pos.X - x, s.Pos.Y - y);
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }

    private string StreetName(RoadPos pos)
    {
        int way = Map.Graph.SegWay[pos.Seg];
        return way >= 0 && way < Map.Roads.Count ? Map.Roads[way].Name : "";
    }

    /// <summary>Stop names are unique per mode, so a bus and a tram stop may share a name.</summary>
    private string UniqueStopName(string baseName, ModeId mode)
    {
        var names = Stops.Values.Where(s => s.Mode == mode).Select(s => s.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    public static bool UsesTracks(ModeId mode) => Modes.Get(mode).Infra == Infra.TramTrack;

    /// <summary>Where a vehicle of this mode can be: roads for buses, tracks for trams.</summary>
    public RoadPos? SnapFor(ModeId mode, double x, double y, double maxDist) =>
        UsesTracks(mode) ? Map.Graph.Snap(x, y, maxDist, Tracks.Contains) : Map.Graph.Snap(x, y, maxDist);

    /// <summary>Trams have their own right of way: faster than road traffic, capped by the vehicle.</summary>
    private double TrackSpeed(int seg) => Map.Graph.SegSpd[seg] / MapData.TrafficFactor * 0.9;

    public RouteResult? RouteFor(ModeId mode, RoadPos a, RoadPos b) =>
        UsesTracks(mode) ? Map.Graph.RouteSegs(a, b, Tracks.Contains, TrackSpeed) : Map.Graph.Route(a, b);

    /// <summary>Route for laying new track along any road (both directions).</summary>
    public TrackPlan? PlanTrack(RoadPos a, RoadPos b)
    {
        var g = Map.Graph;
        var r = g.RouteSegs(a, b, _ => true, s => Tracks.Contains(s) ? 30 : g.SegSpd[s]);
        if (r == null) return null;
        var segs = r.Segs.Distinct().ToList();
        double newLength = segs.Where(s => !Tracks.Contains(s)).Sum(s => g.SegLen[s]);
        return new TrackPlan(r, segs, newLength, newLength * Modes.Get(ModeId.Tram).TrackCostPerM);
    }

    public bool BuildTrack(RoadPos a, RoadPos b)
    {
        var plan = PlanTrack(a, b);
        if (plan == null)
        {
            Notify("Keine Straßenverbindung für Gleise", true);
            return false;
        }
        if (plan.NewLength == 0) return true;
        if (!Pay(plan.Cost)) return false;
        foreach (int s in plan.Segs) Tracks.Add(s);
        TrackVersion++;
        OnChange();
        return true;
    }

    public double TrackLength => Tracks.Sum(s => Map.Graph.SegLen[s]);

    /// <summary>
    /// Remove the track piece near (x, y) up to the next junction, end, stop or depot.
    /// Refused if a stop, depot or line still needs it. Returns false if no track was hit.
    /// </summary>
    public bool RemoveTrackAt(double x, double y, double radius)
    {
        var g = Map.Graph;
        var hit = g.Snap(x, y, radius, Tracks.Contains);
        if (hit == null) return false;
        int hs = hit.Value.Seg;
        int Degree(int v) => g.VSegs[v].Count(Tracks.Contains);
        // Stops and depots split the track into pieces, so tails beyond them can be removed.
        var occupied = Stops.Values.Where(o => UsesTracks(o.Mode)).Select(o => o.Pos.Seg)
            .Concat(Depots.Values.Where(o => UsesTracks(o.Mode)).Select(o => o.Pos.Seg)).ToHashSet();
        var piece = new HashSet<int> { hs };
        foreach (int start in new[] { g.SegA[hs], g.SegB[hs] })
        {
            int v = start, from = hs;
            while (Degree(v) == 2)
            {
                int next = g.VSegs[v].FirstOrDefault(s => s != from && Tracks.Contains(s), -1);
                if (next < 0 || piece.Contains(next) || occupied.Contains(next)) break;
                piece.Add(next);
                v = g.SegA[next] == v ? g.SegB[next] : g.SegA[next];
                from = next;
            }
        }
        string? blocker = Stops.Values.Where(o => UsesTracks(o.Mode) && piece.Contains(o.Pos.Seg)).Select(o => o.Name)
            .Concat(Depots.Values.Where(o => UsesTracks(o.Mode) && piece.Contains(o.Pos.Seg)).Select(o => o.Name))
            .FirstOrDefault();
        if (blocker != null)
        {
            Notify($"Auf diesem Gleis liegt noch „{blocker}“", true);
            return true;
        }
        foreach (int s in piece) Tracks.Remove(s);
        var broken = Lines.Values.FirstOrDefault(l => UsesTracks(l.Mode) && BuildRuns(l.StopIds, l.Circular, l.Mode, out _) == null);
        if (broken != null)
        {
            foreach (int s in piece) Tracks.Add(s);
            Notify($"Linie {broken.Name} fährt über dieses Gleis", true);
            return true;
        }
        TrackVersion++;
        OnChange();
        return true;
    }

    public Stop? AddStop(RoadPos pos, ModeId mode = ModeId.Bus)
    {
        if (StopAt(pos.X, pos.Y, 30, mode) != null)
        {
            Notify("Zu nah an einer anderen Haltestelle", true);
            return null;
        }
        if (UsesTracks(mode) && !Tracks.Contains(pos.Seg))
        {
            Notify("Tram-Haltestellen müssen auf Gleisen liegen", true);
            return null;
        }
        if (!Pay(Modes.Get(mode).StopCost)) return null;
        int id = _nextId++;
        string street = StreetName(pos);
        var stop = new Stop { Id = id, Mode = mode, Pos = pos, Name = UniqueStopName(street != "" ? street : $"Haltestelle {id}", mode) };
        Stops[id] = stop;
        OnChange();
        return stop;
    }

    public void RemoveStop(int id)
    {
        if (!Stops.TryGetValue(id, out var stop)) return;
        Today.Lost += stop.Waiting.Count;
        Stops.Remove(id);
        foreach (var line in Lines.Values.ToList())
        {
            if (!line.StopIds.Contains(id)) continue;
            line.StopIds = line.StopIds.Where(s => s != id).ToList();
            if (line.StopIds.Count < 2)
            {
                DeleteLine(line.Id);
                Notify($"Linie {line.Name} wurde aufgelöst (zu wenige Haltestellen)");
            }
            else if (RebuildLine(line) is string err)
            {
                DeleteLine(line.Id);
                Notify($"Linie {line.Name} aufgelöst: {err}", true);
            }
        }
        _routerDirty = true;
        OnChange();
    }

    public Depot? AddDepot(RoadPos pos, ModeId mode = ModeId.Bus)
    {
        if (UsesTracks(mode) && !Tracks.Contains(pos.Seg))
        {
            Notify("Tram-Betriebshöfe müssen an Gleisen liegen", true);
            return null;
        }
        if (!Pay(Modes.Get(mode).DepotCost)) return null;
        int id = _nextId++;
        string street = StreetName(pos);
        string kind = mode == ModeId.Tram ? "Tram-Betriebshof" : "Betriebshof";
        var depot = new Depot
        {
            Id = id, Mode = mode, Pos = pos, Name = street != "" ? $"{kind} {street}" : $"{kind} {id}",
            Layout = DepotLayout.Starter(mode),
        };
        Depots[id] = depot;
        OnChange();
        return depot;
    }

    public void RemoveDepot(int id)
    {
        if (Vehicles.Values.Any(v => v.DepotId == id))
        {
            Notify("Der Betriebshof hat noch Fahrzeuge. Verkaufe sie zuerst.", true);
            return;
        }
        foreach (var d in Drivers.Values.Where(d => d.DepotId == id).ToList()) Drivers.Remove(d.Id);
        Depots.Remove(id);
        OnChange();
    }

    // ---------------------------------------------------------------- staff

    public List<Driver> DriversOf(int depotId) => Drivers.Values.Where(d => d.DepotId == depotId).ToList();

    public void HireDrivers(int depotId, int n)
    {
        if (!Depots.TryGetValue(depotId, out var depot)) return;
        int free = depot.Layout.DriverCapacity() - DriversOf(depotId).Count;
        if (free <= 0)
        {
            Notify("Kein Platz für weitere Fahrer – baue ein Sozialgebäude im Betriebshof", true);
            return;
        }
        if (n > free)
        {
            Notify($"Nur noch Platz für {free} Fahrer – baue ein weiteres Sozialgebäude", true);
            n = free;
        }
        if (!Pay(DriverHireCost * n)) return;
        for (int i = 0; i < n; i++)
        {
            int id = _nextId++;
            Drivers[id] = new Driver { Id = id, Name = Staff.RandomName(Rng), DepotId = depotId };
        }
        OnChange();
    }

    /// <summary>Dismiss one off-duty driver (prefers those who already worked today).</summary>
    public void FireDriver(int depotId)
    {
        var off = DriversOf(depotId).Where(d => d.VehicleId == null).OrderByDescending(d => d.Worked).FirstOrDefault();
        if (off == null)
        {
            Notify("Alle Fahrer sind gerade im Dienst", true);
            return;
        }
        Drivers.Remove(off.Id);
        OnChange();
    }

    private Driver? AvailableDriver(int depotId) =>
        Drivers.Values.FirstOrDefault(d => d.DepotId == depotId && d.VehicleId == null && d.Worked < ShiftMax - ShiftMinLeft);

    private bool TakeDriver(Vehicle v)
    {
        if (v.DriverId != null) return true;
        var d = AvailableDriver(v.DepotId);
        if (d == null) return false;
        d.VehicleId = v.Id;
        v.DriverId = d.Id;
        return true;
    }

    private void ReleaseDriver(Vehicle v)
    {
        if (v.DriverId is int id && Drivers.TryGetValue(id, out var d)) d.VehicleId = null;
        v.DriverId = null;
    }

    /// <summary>
    /// Estimated drivers a depot needs per day: vehicle hours of the timetables, split by where
    /// the line's vehicles are based, in 8 h shifts plus ~30 % reserve (shifts rarely end exactly at 8 h).
    /// </summary>
    public int DriverDemand(int depotId)
    {
        double hours = 0;
        foreach (var l in Lines.Values)
        {
            var pool = VehiclesOf(l);
            if (pool.Count == 0) continue;
            double share = (double)pool.Count(v => v.DepotId == depotId) / pool.Count;
            hours += l.Timetable.VehicleHours(l.CycleTime) * share;
        }
        return (int)Math.Ceiling(hours / (ShiftMax / 3600) * 1.3);
    }

    /// <summary>Hire drivers so every depot covers its estimated demand. Returns how many were hired.</summary>
    public int HireMissingDrivers(int? depotId = null)
    {
        int hired = 0;
        foreach (var d in Depots.Values.ToList())
        {
            if (depotId != null && d.Id != depotId) continue;
            int have = DriversOf(d.Id).Count;
            int missing = Math.Min(DriverDemand(d.Id) - have, d.Layout.DriverCapacity() - have);
            if (missing > 0 && CanAfford(missing * DriverHireCost))
            {
                HireDrivers(d.Id, missing);
                hired += missing;
            }
        }
        return hired;
    }
}
