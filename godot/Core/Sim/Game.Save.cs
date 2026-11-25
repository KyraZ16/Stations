using System.Text.Json;
using System.Text.Json.Serialization;
using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>Persistent game state. Field names match the browser prototype's save format (v2).</summary>
public sealed class SaveData
{
    public int V { get; set; } = 2;
    public required Area Area { get; set; }
    public double Time { get; set; }
    public double Money { get; set; }
    public bool Sandbox { get; set; }
    public int NextId { get; set; }
    public int VehicleCounter { get; set; }
    public List<PlacedDto> Stops { get; set; } = new();
    public List<PlacedDto> Depots { get; set; } = new();
    public List<LineDto> Lines { get; set; } = new();
    public List<VehicleDto> Vehicles { get; set; } = new();
    public List<DriverDto>? Drivers { get; set; }
    /// <summary>Mechanics and cleaners (null in saves from before yard staff).</summary>
    public List<WorkerDto>? Workers { get; set; }
    /// <summary>Midpoints of road segments with tram track.</summary>
    public List<double[]>? Tracks { get; set; }
    /// <summary>U-Bahn tunnels: points (x, y) and pieces (index pairs).</summary>
    public List<double[]>? MetroNodes { get; set; }
    public List<int[]>? MetroEdges { get; set; }
    /// <summary>S-Bahn tracks the player laid (x1, y1, x2, y2), on top of the railway from the map.</summary>
    public List<double[]>? RailPieces { get; set; }
    public List<DayStats> History { get; set; } = new();
    /// <summary>Monthly passes in circulation (sold at customer centers).</summary>
    public double Subscribers { get; set; }
    /// <summary>Research: points, finished projects, the running project and its progress (researcher-hours).</summary>
    public double Points { get; set; }
    public List<string>? Researched { get; set; }
    public string? ActiveResearch { get; set; }
    public double ResearchProgress { get; set; }

    public sealed class PlacedDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ModeId Mode { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Regional exit at the map edge (stops only).</summary>
        public bool Portal { get; set; }
        /// <summary>Expansion level of a stop (0 in older saves = 1).</summary>
        public int Level { get; set; }
        /// <summary>Depot yard (depots only).</summary>
        public DepotLayout? Layout { get; set; }
    }

    public sealed class LineDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Color { get; set; } = "";
        public ModeId Mode { get; set; }
        public List<int> StopIds { get; set; } = new();
        public bool Circular { get; set; }
        public Timetable? Timetable { get; set; }
        public string? PreferredModel { get; set; }
    }

    public sealed class VehicleDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ModeId Mode { get; set; }
        public int DepotId { get; set; }
        public int? LineId { get; set; }
        public string? Model { get; set; }
        public double Condition { get; set; } = 1;
        public double Cleanliness { get; set; } = 1;
        public double Charge { get; set; } = 1;
        public int Breakdowns { get; set; }
        public double BoughtAt { get; set; }
        public double Km { get; set; }
    }

    public sealed class WorkerDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int DepotId { get; set; }
        public StaffRole Role { get; set; }
        public ShiftId Shift { get; set; }
    }

    public sealed class DriverDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int DepotId { get; set; }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SaveData FromJson(string json) =>
        JsonSerializer.Deserialize<SaveData>(json, Json) ?? throw new InvalidDataException("Leerer Spielstand");
}

public sealed partial class Game
{
    public SaveData Serialize()
    {
        var g = Map.Graph;
        return new SaveData
        {
            Area = Map.Area,
            Time = Time,
            Money = Money,
            Sandbox = Sandbox,
            NextId = _nextId,
            VehicleCounter = _vehicleCounter,
            Stops = Stops.Values.Select(s => new SaveData.PlacedDto { Id = s.Id, Name = s.Name, Mode = s.Mode, X = s.Pos.X, Y = s.Pos.Y, Portal = s.Portal, Level = s.Level }).ToList(),
            Depots = Depots.Values.Select(d => new SaveData.PlacedDto { Id = d.Id, Name = d.Name, Mode = d.Mode, X = d.Pos.X, Y = d.Pos.Y, Layout = d.Layout }).ToList(),
            Lines = Lines.Values.Select(l => new SaveData.LineDto
            {
                Id = l.Id, Name = l.Name, Color = l.Color, Mode = l.Mode, StopIds = l.StopIds.ToList(), Circular = l.Circular,
                Timetable = l.Timetable.Clone(), PreferredModel = l.PreferredModel,
            }).ToList(),
            Vehicles = Vehicles.Values.Select(v => new SaveData.VehicleDto
            {
                Id = v.Id, Name = v.Name, Mode = v.Mode, DepotId = v.DepotId, LineId = v.LineId, Model = v.ModelId,
                Condition = v.Condition, Cleanliness = v.Cleanliness, Charge = v.Charge, Breakdowns = v.Breakdowns,
                BoughtAt = v.BoughtAt, Km = v.KmTotal,
            }).ToList(),
            Drivers = Drivers.Values.Select(d => new SaveData.DriverDto { Id = d.Id, Name = d.Name, DepotId = d.DepotId }).ToList(),
            Workers = Workers.Values.Select(w => new SaveData.WorkerDto { Id = w.Id, Name = w.Name, DepotId = w.DepotId, Role = w.Role, Shift = w.Shift }).ToList(),
            Tracks = Tracks.Select(s => new[]
            {
                Math.Round((g.Vx[g.SegA[s]] + g.Vx[g.SegB[s]]) / 2, 1),
                Math.Round((g.Vy[g.SegA[s]] + g.Vy[g.SegB[s]]) / 2, 1),
            }).ToList(),
            History = History.ToList(),
            MetroNodes = Metro.Nodes.Select(n => new[] { Math.Round(n.X, 2), Math.Round(n.Y, 2) }).ToList(),
            MetroEdges = Metro.Edges.Select(e => new[] { e.A, e.B }).ToList(),
            RailPieces = SaveRail(),
            Subscribers = Subscribers,
            Points = Points,
            Researched = Researched.ToList(),
            ActiveResearch = ActiveResearch,
            ResearchProgress = ResearchProgress,
        };
    }

    public static Game Restore(MapData map, SaveData data)
    {
        var g = new Game(map, data.Sandbox, data.Money)
        {
            Time = data.Time,
            _nextId = data.NextId,
            _vehicleCounter = data.VehicleCounter,
            History = data.History ?? new(),
            Subscribers = data.Subscribers,
            Points = data.Points,
            ActiveResearch = data.ActiveResearch,
            ResearchProgress = data.ResearchProgress,
        };
        g.Today = new DayStats { Day = g.Day };
        foreach (var id in data.Researched ?? new()) if (ResearchTree.All.Any(r => r.Id == id)) g.Researched.Add(id);
        var graph = map.Graph;
        g.LoadMetro(data.MetroNodes, data.MetroEdges);
        g.LoadRail(data.RailPieces);
        foreach (var t in data.Tracks ?? new())
        {
            if (graph.Snap(t[0], t[1], 1.5) is RoadPos p) g.Tracks.Add(p.Seg);
        }
        foreach (var s in data.Stops)
        {
            if (g.SnapFor(s.Mode, s.X, s.Y, 60) is RoadPos pos)
                g.Stops[s.Id] = new Stop { Id = s.Id, Name = s.Name, Mode = s.Mode, Pos = pos, Portal = s.Portal, Level = Math.Clamp(s.Level, 1, MaxStopLevel) };
        }
        foreach (var d in data.Depots)
        {
            if (g.SnapFor(d.Mode, d.X, d.Y, 60) is RoadPos pos)
                g.Depots[d.Id] = new Depot
                {
                    Id = d.Id, Name = d.Name, Mode = d.Mode, Pos = pos,
                    // Saves from before the yard editor get a yard that fits their vehicles and drivers
                    Layout = d.Layout ?? DepotLayout.Starter(d.Mode,
                        data.Vehicles.Count(v => v.DepotId == d.Id),
                        data.Drivers?.Count(x => x.DepotId == d.Id) ?? (int)Math.Ceiling(data.Vehicles.Count(v => v.DepotId == d.Id) * 1.6)),
                };
        }
        foreach (var l in data.Lines)
        {
            var stopIds = l.StopIds.Where(g.Stops.ContainsKey).ToList();
            if (stopIds.Count < 2) continue;
            var runs = g.BuildRuns(stopIds, l.Circular, l.Mode, out _);
            if (runs == null) continue;
            g.Lines[l.Id] = new TransitLine
            {
                Id = l.Id, Name = l.Name, Color = l.Color, Mode = l.Mode, StopIds = stopIds, Circular = l.Circular,
                Runs = runs, CycleTime = CycleTime(runs), Timetable = l.Timetable ?? Timetable.Default(l.Mode), PreferredModel = l.PreferredModel,
            };
        }
        foreach (var vd in data.Vehicles)
        {
            if (!g.Depots.TryGetValue(vd.DepotId, out var depot)) continue;
            g.Vehicles[vd.Id] = new Vehicle
            {
                Id = vd.Id, Name = vd.Name, Mode = vd.Mode, DepotId = vd.DepotId,
                ModelId = Modes.Model(vd.Mode, vd.Model).Id, Condition = vd.Condition, Cleanliness = vd.Cleanliness,
                Charge = vd.Charge, Breakdowns = vd.Breakdowns, BoughtAt = vd.BoughtAt, KmTotal = vd.Km,
                LineId = vd.LineId is int lid && g.Lines.ContainsKey(lid) ? lid : null,
                X = depot.Pos.X, Y = depot.Pos.Y,
            };
        }
        if (data.Drivers != null)
        {
            foreach (var d in data.Drivers)
                if (g.Depots.ContainsKey(d.DepotId)) g.Drivers[d.Id] = new Driver { Id = d.Id, Name = d.Name, DepotId = d.DepotId };
        }
        else
        {
            // Saves from before staff existed: give every depot enough drivers for its vehicles
            foreach (var depot in g.Depots.Values)
            {
                int n = g.Vehicles.Values.Count(v => v.DepotId == depot.Id);
                for (int i = 0; i < Math.Ceiling(n * 1.6); i++)
                {
                    int id = g._nextId++;
                    g.Drivers[id] = new Driver { Id = id, Name = Staff.RandomName(g.Rng), DepotId = depot.Id };
                }
            }
        }
        if (data.Workers != null)
        {
            foreach (var w in data.Workers)
                if (g.Depots.ContainsKey(w.DepotId))
                    g.Workers[w.Id] = new Worker { Id = w.Id, Name = w.Name, DepotId = w.DepotId, Role = w.Role, Shift = w.Shift };
        }
        else
        {
            // Saves from before yard staff: workshops and wash bays get their crews (free of charge)
            foreach (var depot in g.Depots.Values)
                foreach (var role in new[] { StaffRole.Mechanic, StaffRole.Cleaner })
                    for (int i = 0; i < g.WorkerDemand(depot.Id, role); i++)
                    {
                        int id = g._nextId++;
                        g.Workers[id] = new Worker { Id = id, Name = Staff.RandomName(g.Rng), DepotId = depot.Id, Role = role, Shift = (ShiftId)(i % 3) };
                    }
        }
        // Older saves may already use models and modules that now need research: keep them usable
        foreach (var v in g.Vehicles.Values)
            if (ResearchTree.ForModel(v.ModelId) is string need) g.Researched.Add(need);
        foreach (var d in g.Depots.Values)
            foreach (var m in d.Layout.Modules)
                if (ResearchTree.ForModule(m.Type) is string need) g.Researched.Add(need);
        if (g.Has("ebus")) g.Researched.Add("charge");
        g.NamePortals(); // exits from older saves get city names once the names are known
        foreach (var d in g.Depots.Values) g.EnsureHomeSpots(d);
        // Vehicles start in the depot; the dispatcher sends them out per timetable.
        g._dispatchTimer = DispatchInterval;
        return g;
    }
}
