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
    /// <summary>Midpoints of road segments with tram track.</summary>
    public List<double[]>? Tracks { get; set; }
    public List<DayStats> History { get; set; } = new();

    public sealed class PlacedDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ModeId Mode { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
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
            Stops = Stops.Values.Select(s => new SaveData.PlacedDto { Id = s.Id, Name = s.Name, Mode = s.Mode, X = s.Pos.X, Y = s.Pos.Y }).ToList(),
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
            Tracks = Tracks.Select(s => new[]
            {
                Math.Round((g.Vx[g.SegA[s]] + g.Vx[g.SegB[s]]) / 2, 1),
                Math.Round((g.Vy[g.SegA[s]] + g.Vy[g.SegB[s]]) / 2, 1),
            }).ToList(),
            History = History.ToList(),
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
        };
        g.Today = new DayStats { Day = g.Day };
        var graph = map.Graph;
        foreach (var t in data.Tracks ?? new())
        {
            if (graph.Snap(t[0], t[1], 1.5) is RoadPos p) g.Tracks.Add(p.Seg);
        }
        foreach (var s in data.Stops)
        {
            if (g.SnapFor(s.Mode, s.X, s.Y, 60) is RoadPos pos)
                g.Stops[s.Id] = new Stop { Id = s.Id, Name = s.Name, Mode = s.Mode, Pos = pos };
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
                Runs = runs, CycleTime = CycleTime(runs), Timetable = l.Timetable ?? Timetable.Default(), PreferredModel = l.PreferredModel,
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
        // Vehicles start in the depot; the dispatcher sends them out per timetable.
        g._dispatchTimer = DispatchInterval;
        return g;
    }
}
