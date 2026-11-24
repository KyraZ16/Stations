using System.Text.Json.Serialization;

namespace Stations.Core.Sim;

public enum ModuleType { Road, BusSpot, TramSpot, Office, Workshop, Wash, LongBusSpot, Charger, ServiceCenter }

public sealed record ModuleDef(
    ModuleType Type,
    string Name,
    int W, // cells across (before rotation)
    int H, // cells along
    double Cost,
    double Height, // m, for the 3D view
    string Color,
    string Effect);

/// <summary>
/// A building block placed on the depot grid. Rot 1 swaps width and height.
/// Buildings (offices, workshops, wash bays) can be drawn in any size: Sw × Sh cells (0 = standard size).
/// </summary>
public sealed class PlacedModule
{
    public ModuleType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Rot { get; set; }
    public int Sw { get; set; }
    public int Sh { get; set; }

    [JsonIgnore] public int W => Sw > 0 ? Sw : Rot % 2 == 0 ? DepotLayout.Def(Type).W : DepotLayout.Def(Type).H;
    [JsonIgnore] public int H => Sh > 0 ? Sh : Rot % 2 == 0 ? DepotLayout.Def(Type).H : DepotLayout.Def(Type).W;

    public bool Covers(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>
/// The yard of a depot: a grid of 3 m cells with a fixed gate on the bottom edge.
/// Parking spots only count when a short end touches a driveway connected to the gate;
/// offices provide room for drivers.
/// </summary>
public sealed partial class DepotLayout
{
    public const double CellSize = 3; // m
    /// <summary>Staff places per office cell (drivers, mechanics and cleaners share them).</summary>
    public const int StaffPerCell = 3;
    /// <summary>Staff places of a standard (3×3) office.</summary>
    public const int DriversPerOffice = 9 * StaffPerCell;
    /// <summary>Largest side of a drawn building, in cells.</summary>
    public const int MaxBuildingSide = 12;

    public int Width { get; set; } = 24;
    public int Height { get; set; } = 16;
    public List<PlacedModule> Modules { get; set; } = new();

    public static readonly IReadOnlyDictionary<ModuleType, ModuleDef> Defs = new Dictionary<ModuleType, ModuleDef>
    {
        [ModuleType.Road] = new(ModuleType.Road, "Fahrweg", 1, 1, 2_000, 0.05, "4a4f56", "Verbindet Stellplätze und Gebäude mit der Einfahrt"),
        [ModuleType.BusSpot] = new(ModuleType.BusSpot, "Busstellplatz", 1, 5, 10_000, 0.04, "8d9096", "+1 Bus (Stirnseite muss am Fahrweg liegen)"),
        [ModuleType.TramSpot] = new(ModuleType.TramSpot, "Abstellgleis", 1, 12, 60_000, 0.06, "b8ada0", "+1 Tram (Stirnseite muss am Gleis liegen)"),
        [ModuleType.LongBusSpot] = new(ModuleType.LongBusSpot, "Gelenkbus-Stellplatz", 1, 7, 14_000, 0.04, "7f8794", "+1 Bus bis 21 m, auch Gelenkbusse"),
        [ModuleType.Office] = new(ModuleType.Office, "Sozialgebäude", 3, 3, 153_000, 7, "d9cbb0", $"Frei aufziehbar (ab 2×2): {StaffPerCell} Mitarbeiter je Feld (3×3 = {DriversPerOffice}), Umkleide und Pausenraum wachsen mit"),
        [ModuleType.Workshop] = new(ModuleType.Workshop, "Werkstatt", 4, 6, 408_000, 9, "9fb3c8", "Frei aufziehbar (ab 2×6): je 2 Felder Breite eine Spur, je Spur ein Mechaniker im Dienst (+25 % Zustand pro Stunde)"),
        [ModuleType.Wash] = new(ModuleType.Wash, "Waschanlage", 2, 6, 120_000, 5, "a8d0d8", "Frei aufziehbar (ab 2×6): je 2 Felder Breite eine Waschstraße, je Straße eine Reinigungskraft im Dienst (15 min pro Wäsche)"),
        [ModuleType.ServiceCenter] = new(ModuleType.ServiceCenter, "Kundencenter", 3, 3, 180_000, 4.6, "e9e4dc",
            "Frei aufziehbar (ab 3×3): Schalter für Abos, geöffnet 06–22 Uhr. Je Schalter eine Servicekraft im Dienst; Abos bringen Einnahmen und mehr Fahrgäste"),
        [ModuleType.Charger] = new(ModuleType.Charger, "Ladestation", 1, 1, 60_000, 2.2, "3fae6a", "Direkt neben einen Stellplatz bauen: lädt den E-Bus dort (voll in 3 Stunden)"),
    };

    public static ModuleDef Def(ModuleType t) => Defs[t];

    /// <summary>Gate cells: two cells in the middle of the bottom edge (always drivable).</summary>
    public IEnumerable<(int X, int Y)> GateCells()
    {
        yield return (Width / 2 - 1, Height - 1);
        yield return (Width / 2, Height - 1);
    }

    public bool IsGate(int x, int y) => y == Height - 1 && (x == Width / 2 - 1 || x == Width / 2);

    public PlacedModule? ModuleAt(int x, int y) => Modules.FirstOrDefault(m => m.Covers(x, y));

    /// <summary>Buildings that are drawn as a rectangle of any size.</summary>
    public static bool IsDrawable(ModuleType t) => t is ModuleType.Office or ModuleType.Workshop or ModuleType.Wash or ModuleType.ServiceCenter;

    /// <summary>Closed buildings people enter through a door (offices, customer centers).</summary>
    public static bool HasDoor(ModuleType t) => t is ModuleType.Office or ModuleType.ServiceCenter;

    /// <summary>Why a building cannot have this size (cells), or null.</summary>
    public static string? SizeError(ModuleType t, int w, int h)
    {
        int s = Math.Min(w, h), l = Math.Max(w, h);
        if (l > MaxBuildingSide) return $"Höchstens {MaxBuildingSide} Felder lang";
        return t switch
        {
            ModuleType.Office when s < 2 => "Ein Sozialgebäude braucht mindestens 2×2 Felder",
            ModuleType.ServiceCenter when s < 3 => "Ein Kundencenter braucht mindestens 3×3 Felder",
            ModuleType.Workshop or ModuleType.Wash when s < 2 || l < 6 => $"Eine {Def(t).Name} braucht mindestens 2×6 Felder",
            _ => null,
        };
    }

    /// <summary>Price of a module in the given size (buildings: by area, relative to the standard size).</summary>
    public static double Cost(ModuleType t, int w = 0, int h = 0)
    {
        var d = Def(t);
        return IsDrawable(t) && w > 0 && h > 0 ? Math.Round(d.Cost * w * h / (d.W * d.H) / 1000) * 1000 : d.Cost;
    }

    /// <summary>Staff places of an office of that size.</summary>
    public static int OfficeCapacity(int w, int h) => w * h * StaffPerCell;

    /// <summary>Workshop lanes / wash lanes: one per two cells across.</summary>
    public static int LaneCount(int w, int h) => Math.Max(1, Math.Min(w, h) / 2);

    public static int Lanes(PlacedModule m) => m.Type is ModuleType.Workshop or ModuleType.Wash ? LaneCount(m.W, m.H) : 1;

    /// <summary>Short description of what a building of this size offers.</summary>
    public static string SizeInfo(ModuleType t, int w, int h)
    {
        string size = $"{w}×{h} Felder";
        return t switch
        {
            ModuleType.Office => $"{size}: {OfficeCapacity(w, h)} Mitarbeiter, {OfficePlan.Create(w * CellSize / 2 - 0.4, h * CellSize / 2 - 0.4).Seats.Count} Sitzplätze",
            ModuleType.Workshop => $"{size}: {LaneCount(w, h)} Spur(en)",
            ModuleType.Wash => $"{size}: {LaneCount(w, h)} Waschstraße(n)",
            ModuleType.ServiceCenter => $"{size}: {ServicePlan.Create(Math.Max(w, h) * CellSize / 2 - 0.4, Math.Min(w, h) * CellSize / 2 - 0.4).Counters.Count} Schalter",
            _ => size,
        };
    }

    /// <summary>Why a module cannot go there, or null if it fits.</summary>
    public string? CanPlace(ModuleType type, int x, int y, int rot, int w = 0, int h = 0)
    {
        var m = new PlacedModule { Type = type, X = x, Y = y, Rot = rot, Sw = w, Sh = h };
        if (w > 0 && SizeError(type, w, h) is string sizeErr) return sizeErr;
        if (x < 0 || y < 0 || x + m.W > Width || y + m.H > Height) return "Außerhalb des Grundstücks";
        for (int i = x; i < x + m.W; i++)
        for (int j = y; j < y + m.H; j++)
        {
            if (IsGate(i, j) && type != ModuleType.Road) return "Die Einfahrt muss frei bleiben";
            if (ModuleAt(i, j) != null) return "Hier steht schon etwas";
        }
        return null;
    }

    /// <summary>Cells reachable from the gate over driveways.</summary>
    public HashSet<(int, int)> Reachable()
    {
        var road = new HashSet<(int, int)>();
        foreach (var m in Modules.Where(m => m.Type == ModuleType.Road)) road.Add((m.X, m.Y));
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();
        foreach (var g in GateCells())
        {
            seen.Add(g);
            queue.Enqueue(g);
        }
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var n in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (seen.Contains(n) || !road.Contains(n)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return seen;
    }

    private static IEnumerable<(int, int)> Around(PlacedModule m) =>
        Enumerable.Range(m.X, m.W).SelectMany(i => new[] { (i, m.Y - 1), (i, m.Y + m.H) })
            .Concat(Enumerable.Range(m.Y, m.H).SelectMany(j => new[] { (m.X - 1, j), (m.X + m.W, j) }));

    /// <summary>Is the module connected: spots at a short end, buildings on any side,
    /// chargers directly next to a connected parking spot (they charge the vehicle parked there).</summary>
    public bool IsConnected(PlacedModule m, HashSet<(int, int)> reach)
    {
        if (m.Type == ModuleType.Road) return reach.Contains((m.X, m.Y));
        if (m.Type == ModuleType.Charger)
            return Around(m).Select(c => ModuleAt(c.Item1, c.Item2)).Any(s => s != null && IsSpot(s.Type) && IsConnected(s, reach));
        IEnumerable<(int, int)> access;
        bool spot = IsSpot(m.Type);
        if (spot && m.Rot % 2 == 0)
            access = Enumerable.Range(m.X, m.W).SelectMany(i => new[] { (i, m.Y - 1), (i, m.Y + m.H) });
        else if (spot)
            access = Enumerable.Range(m.Y, m.H).SelectMany(j => new[] { (m.X - 1, j), (m.X + m.W, j) });
        else
            access = Enumerable.Range(m.X, m.W).SelectMany(i => new[] { (i, m.Y - 1), (i, m.Y + m.H) })
                .Concat(Enumerable.Range(m.Y, m.H).SelectMany(j => new[] { (m.X - 1, j), (m.X + m.W, j) }));
        return access.Any(reach.Contains);
    }

    public static bool IsSpot(ModuleType t) => t is ModuleType.BusSpot or ModuleType.LongBusSpot or ModuleType.TramSpot;

    private static bool SpotFor(ModuleType t, ModeId mode) =>
        mode == ModeId.Tram ? t == ModuleType.TramSpot : t is ModuleType.BusSpot or ModuleType.LongBusSpot;

    /// <summary>Usable length of a parking spot in meters.</summary>
    public static double SpotLength(PlacedModule m) => Def(m.Type).H * CellSize;

    /// <summary>Number of connected service buildings of a type.</summary>
    public int CountConnected(ModuleType type)
    {
        var reach = Reachable();
        return Modules.Count(m => m.Type == type && IsConnected(m, reach));
    }

    /// <summary>
    /// Assign vehicles (by length) to connected spots: longest vehicles first, each to the
    /// shortest spot that fits. Returns spot per vehicle index (null if it does not fit).
    /// </summary>
    public PlacedModule?[] Assign(IReadOnlyList<double> lengths, ModeId mode)
    {
        var free = ConnectedSpots(mode).OrderBy(SpotLength).ToList();
        var result = new PlacedModule?[lengths.Count];
        foreach (int i in Enumerable.Range(0, lengths.Count).OrderByDescending(i => lengths[i]))
        {
            var spot = free.FirstOrDefault(s => SpotLength(s) >= lengths[i]);
            if (spot == null) continue;
            result[i] = spot;
            free.Remove(spot);
        }
        return result;
    }

    /// <summary>Do all these vehicles find a fitting spot?</summary>
    public bool Fits(IReadOnlyList<double> lengths, ModeId mode) => Assign(lengths, mode).All(s => s != null);

    public int VehicleCapacity(ModeId mode)
    {
        var reach = Reachable();
        return Modules.Count(m => SpotFor(m.Type, mode) && IsConnected(m, reach));
    }

    /// <summary>Staff places (drivers, mechanics and cleaners together).</summary>
    public int DriverCapacity()
    {
        var reach = Reachable();
        return Modules.Where(m => m.Type == ModuleType.Office && IsConnected(m, reach)).Sum(m => OfficeCapacity(m.W, m.H));
    }

    /// <summary>Connected parking spots in a stable order (for showing parked vehicles).</summary>
    public List<PlacedModule> ConnectedSpots(ModeId mode)
    {
        var reach = Reachable();
        return Modules.Where(m => SpotFor(m.Type, mode) && IsConnected(m, reach)).OrderBy(m => m.Y).ThenBy(m => m.X).ToList();
    }

    // ---------------------------------------------------------------- yard geometry (meters, origin = yard center)

    public (double X, double Y) CellCenter(double x, double y) =>
        ((x + 0.5) * CellSize - Width * CellSize / 2, (y + 0.5) * CellSize - Height * CellSize / 2);

    /// <summary>Point on the street in front of the gate.</summary>
    public (double X, double Y) StreetPoint() => (0, Height * CellSize / 2 + 6);

    /// <summary>Cell (in grid units) closest to a yard position.</summary>
    public (int X, int Y) CellAt(double x, double y) =>
        ((int)Math.Floor((x + Width * CellSize / 2) / CellSize), (int)Math.Floor((y + Height * CellSize / 2) / CellSize));

    /// <summary>Where a vehicle stands in a module: center (and lane for workshops/wash bays), heading along the long side.</summary>
    public (double X, double Y, double Ang) Pose(PlacedModule m, int bay = 0)
    {
        var c = CellCenter(m.X + (m.W - 1) / 2.0, m.Y + (m.H - 1) / 2.0);
        bool vertical = m.H >= m.W;
        double ang = vertical ? -Math.PI / 2 : 0; // vehicles face away from the gate side
        if (m.Type is ModuleType.Workshop or ModuleType.Wash)
        {
            double lane = LaneOffset(m, bay);
            c = vertical ? (c.X + lane, c.Y) : (c.X, c.Y + lane);
        }
        return (c.X, c.Y, ang);
    }

    /// <summary>Offset of a lane from the building's middle line (lanes are two cells apart).</summary>
    public static double LaneOffset(PlacedModule m, int bay) => (bay - (Lanes(m) - 1) / 2.0) * 2 * CellSize;

    /// <summary>A driveway cell from which the module is entered (null if not connected).</summary>
    public (int X, int Y)? AccessCell(PlacedModule m, HashSet<(int, int)> reach)
    {
        IEnumerable<(int, int)> cand = IsSpot(m.Type)
            ? (m.H >= m.W
                ? Enumerable.Range(m.X, m.W).SelectMany(i => new[] { (i, m.Y + m.H), (i, m.Y - 1) })
                : Enumerable.Range(m.Y, m.H).SelectMany(j => new[] { (m.X + m.W, j), (m.X - 1, j) }))
            : Around(m);
        foreach (var c in cand) if (reach.Contains(c)) return c;
        return null;
    }

    /// <summary>Shortest driveway path between two drivable cells (4-neighbourhood), or null.</summary>
    public List<(int X, int Y)>? FindPath((int X, int Y) from, (int X, int Y) to, HashSet<(int, int)> reach)
    {
        if (!reach.Contains(from) || !reach.Contains(to)) return null;
        var prev = new Dictionary<(int, int), (int, int)>();
        var queue = new Queue<(int, int)>();
        queue.Enqueue(from);
        prev[from] = from;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == to) break;
            var (x, y) = cur;
            foreach (var n in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (prev.ContainsKey(n) || !reach.Contains(n)) continue;
                prev[n] = cur;
                queue.Enqueue(n);
            }
        }
        if (!prev.ContainsKey(to)) return null;
        var path = new List<(int, int)> { to };
        while (path[^1] != from) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    /// <summary>Gate cell closest to a cell.</summary>
    public (int X, int Y) GateCellNear(int x) => x < Width / 2 ? (Width / 2 - 1, Height - 1) : (Width / 2, Height - 1);

    /// <summary>
    /// Starter yard: driveway from the gate to a cross road, parking spots along it and one office.
    /// Extra spots/offices are added until the given numbers fit (for depots from older saves).
    /// </summary>
    public static DepotLayout Starter(ModeId mode, int minVehicles = 0, int minDrivers = 0)
    {
        var l = new DepotLayout();
        if (mode == ModeId.Tram) l.Height = 20;
        int cx = l.Width / 2;
        int crossY = mode == ModeId.Tram ? 13 : 7;
        void Road(int x, int y)
        {
            if (l.ModuleAt(x, y) == null) l.Modules.Add(new PlacedModule { Type = ModuleType.Road, X = x, Y = y });
        }
        for (int y = crossY; y < l.Height; y++) { Road(cx - 1, y); Road(cx, y); }
        for (int x = 1; x < l.Width - 1; x++) Road(x, crossY);

        var spotType = mode == ModeId.Tram ? ModuleType.TramSpot : ModuleType.BusSpot;
        int len = Def(spotType).H;
        // Candidate spots: above the cross road, left and right of the driveway, outwards from the middle
        var candidates = new List<int>();
        for (int k = 0; k < cx - 1; k++)
        {
            candidates.Add(cx - 2 - k);
            candidates.Add(cx + 1 + k);
        }
        int wanted = Math.Max(mode == ModeId.Tram ? 4 : 8, minVehicles);
        int placed = 0;
        foreach (int x in candidates)
        {
            if (placed >= wanted) break;
            if (x < 1 || x > l.Width - 2) continue;
            if (l.CanPlace(spotType, x, crossY - len, 0) != null) continue;
            l.Modules.Add(new PlacedModule { Type = spotType, X = x, Y = crossY - len });
            placed++;
        }
        // Offices below the cross road, next to the driveway
        int offices = Math.Max(1, (int)Math.Ceiling(minDrivers / (double)DriversPerOffice));
        int ox = cx + 1, oy = crossY + 1;
        for (int i = 0; i < offices; i++)
        {
            int x = i % 2 == 0 ? ox + (i / 2) * 3 : cx - 4 - (i / 2) * 3;
            if (l.CanPlace(ModuleType.Office, x, oy, 0) == null)
                l.Modules.Add(new PlacedModule { Type = ModuleType.Office, X = x, Y = oy });
        }
        return l;
    }
}
