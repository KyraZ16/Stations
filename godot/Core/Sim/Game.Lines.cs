using Stations.Core.Map;

namespace Stations.Core.Sim;

public sealed partial class Game
{
    // ---------------------------------------------------------------- lines

    /// <summary>Route a line's runs. Returns null and an error message on failure.</summary>
    private List<Run>? BuildRuns(List<int> stopIds, bool circular, ModeId mode, out string? error)
    {
        var seqs = circular
            ? new List<List<int>> { stopIds.Append(stopIds[0]).ToList() }
            : new List<List<int>> { stopIds.ToList(), Enumerable.Reverse(stopIds).ToList() };
        double vmax = Modes.Get(mode).Vehicle.MaxSpeed;
        var runs = new List<Run>();
        foreach (var seq in seqs)
        {
            var path = new RoutePath();
            var offsets = new List<double> { 0 };
            var times = new List<double> { 0 };
            for (int i = 0; i + 1 < seq.Count; i++)
            {
                var a = Stops[seq[i]];
                var b = Stops[seq[i + 1]];
                var r = RouteFor(mode, a.Pos, b.Pos);
                if (r == null)
                {
                    error = $"Keine befahrbare Verbindung von „{a.Name}“ nach „{b.Name}“";
                    return null;
                }
                double s0 = path.Length;
                path.Append(r);
                offsets.Add(path.Length);
                times.Add(times[i] + path.TravelTime(s0, path.Length, vmax) + DwellEstimate);
            }
            runs.Add(new Run { Path = path, StopIds = seq, Offsets = offsets, Times = times });
        }
        error = null;
        return runs;
    }

    private static double CycleTime(List<Run> runs) => runs.Sum(r => r.Times[^1] + MinLayover);

    /// <summary>Try routing a draft line (for previews).</summary>
    public RouteResult? PreviewRoute(Stop a, Stop b) => RouteFor(a.Mode, a.Pos, b.Pos);

    public TransitLine? CreateLine(List<int> stopIds, bool circular, ModeId mode = ModeId.Bus)
    {
        if (stopIds.Count < 2)
        {
            Notify("Eine Linie braucht mindestens zwei Haltestellen", true);
            return null;
        }
        if (stopIds.Any(id => !Stops.TryGetValue(id, out var s) || s.Mode != mode))
        {
            string name = Modes.Get(mode).Name;
            Notify($"Eine {name}linie kann nur {name}-Haltestellen anfahren", true);
            return null;
        }
        var runs = BuildRuns(stopIds, circular, mode, out var err);
        if (runs == null)
        {
            Notify(err!, true);
            return null;
        }
        var used = Lines.Values.Select(l => l.Name).ToHashSet();
        int n = 1;
        while (used.Contains(n.ToString())) n++;
        int id = _nextId++;
        var line = new TransitLine
        {
            Id = id, Name = n.ToString(), Mode = mode, StopIds = stopIds.ToList(), Circular = circular, Runs = runs,
            CycleTime = CycleTime(runs), Color = Util.LineColors[Lines.Count % Util.LineColors.Length],
        };
        Lines[id] = line;
        _routerDirty = true;
        OnChange();
        return line;
    }

    /// <summary>
    /// Re-route a line after its stops changed. Vehicles in service keep running: each one
    /// drives to its next stop on the new route, and riding passengers are remapped.
    /// Returns an error message, or null on success.
    /// </summary>
    public string? RebuildLine(TransitLine line)
    {
        var runs = BuildRuns(line.StopIds, line.Circular, line.Mode, out var err);
        if (runs == null) return err;
        var oldRuns = line.Runs;
        line.Runs = runs;
        line.CycleTime = CycleTime(runs);
        line.Version++;

        // Waiting passengers planned with the old stop indices: let them re-plan (they are regenerated).
        foreach (var s in Stops.Values)
        {
            int before = s.Waiting.Count;
            s.Waiting.RemoveAll(p => p.Legs.Skip(p.LegIdx).Any(l => l.Line == line.Id));
            Today.Lost += before - s.Waiting.Count;
        }

        foreach (var v in Vehicles.Values)
        {
            if (v.LineId != line.Id || InYard(v)) continue;
            if (v.State == VehicleState.Deadhead && v.DeadheadTo == DeadheadTarget.Depot) continue;
            if (v.State == VehicleState.Deadhead && v.DeadheadTo == DeadheadTarget.Line)
            {
                Dispatch(v, DeadheadTarget.Line);
                continue;
            }
            var oldRun = v.RunIdx < oldRuns.Count ? oldRuns[v.RunIdx] : null;
            int r = Math.Min(v.RunIdx, runs.Count - 1);
            var run = runs[r];
            int k = oldRun != null && v.StopIdx < oldRun.StopIds.Count ? run.StopIds.IndexOf(oldRun.StopIds[v.StopIdx]) : -1;
            if (k < 0)
            {
                // Stop no longer on the line: continue at the nearest stop of the new route.
                double bd = double.PositiveInfinity;
                for (int i = 0; i < run.StopIds.Count - 1; i++)
                {
                    var p = Stops[run.StopIds[i]].Pos;
                    double d = Util.Hypot(p.X - v.X, p.Y - v.Y);
                    if (d < bd) { bd = d; k = i; }
                }
            }
            // Remap riding passengers to the new stop indices
            int kk = k;
            v.Passengers.RemoveAll(p =>
            {
                var leg = p.Legs[p.LegIdx];
                int ai = run.StopIds.IndexOf(leg.AlightStop, kk);
                bool laterUse = p.Legs.Skip(p.LegIdx + 1).Any(l => l.Line == line.Id);
                if (ai < 0 || laterUse)
                {
                    Today.Lost++;
                    return true;
                }
                leg.Alight = ai;
                leg.Run = r;
                leg.Ver = line.Version;
                return false;
            });
            if (v.State == VehicleState.Layover && k == 0)
            {
                double arrived = v.ArrivedAt;
                EnterLayover(v, line, r);
                v.ArrivedAt = arrived;
            }
            else
            {
                Rejoin(v, line, r, k);
            }
        }
        _routerDirty = true;
        OnChange();
        return null;
    }

    /// <summary>Replace a line's stop sequence. Keeps the old one if routing fails.</summary>
    public bool SetLineStops(TransitLine line, List<int> stopIds)
    {
        for (int i = 1; i < stopIds.Count; i++)
        {
            if (stopIds[i] == stopIds[i - 1])
            {
                Notify("Dieselbe Haltestelle kann nicht zweimal hintereinander angefahren werden", true);
                return false;
            }
        }
        if (stopIds.Count < 2)
        {
            Notify("Eine Linie braucht mindestens zwei Haltestellen", true);
            return false;
        }
        var old = line.StopIds;
        line.StopIds = stopIds;
        if (RebuildLine(line) is string err)
        {
            line.StopIds = old;
            Notify(err, true);
            return false;
        }
        return true;
    }

    /// <summary>Send a vehicle in service to stop index k of run r on the (changed) line.</summary>
    private void Rejoin(Vehicle v, TransitLine line, int r, int k)
    {
        var stop = Stops[line.Runs[r].StopIds[k]];
        var from = SnapFor(v.Mode, v.X, v.Y, 80);
        var route = from is RoadPos f ? RouteFor(v.Mode, f, stop.Pos) : null;
        if (route == null)
        {
            Today.Lost += v.Passengers.Count;
            v.Passengers.Clear();
            Dispatch(v, DeadheadTarget.Line);
            return;
        }
        v.Path = RoutePath.From(route);
        v.S = 0;
        v.Seg = 0;
        v.RunIdx = r;
        v.StopIdx = k;
        v.State = VehicleState.Deadhead;
        v.DeadheadTo = DeadheadTarget.Rejoin;
    }

    public void SetCircular(TransitLine line, bool circular)
    {
        bool old = line.Circular;
        line.Circular = circular;
        if (RebuildLine(line) is string err)
        {
            line.Circular = old;
            Notify(err, true);
        }
    }

    public void DeleteLine(int id)
    {
        if (!Lines.ContainsKey(id)) return;
        foreach (var v in Vehicles.Values)
        {
            if (v.LineId != id) continue;
            v.LineId = null;
            Dispatch(v, DeadheadTarget.Depot);
        }
        PurgePassengers(id);
        Lines.Remove(id);
        _routerDirty = true;
        OnChange();
    }

    private void PurgePassengers(int lineId)
    {
        foreach (var s in Stops.Values)
        {
            int before = s.Waiting.Count;
            s.Waiting.RemoveAll(p => p.Legs.Skip(p.LegIdx).Any(l => l.Line == lineId));
            Today.Lost += before - s.Waiting.Count;
        }
        foreach (var v in Vehicles.Values)
        {
            if (v.LineId != lineId) continue;
            Today.Lost += v.Passengers.Count;
            v.Passengers.Clear();
        }
    }

    private static bool HeadingHome(Vehicle v) => v.State == VehicleState.Deadhead && v.DeadheadTo == DeadheadTarget.Depot;

    /// <summary>Vehicles of the line that are out of the depot and not heading back.</summary>
    public int ActiveVehicles(TransitLine line) =>
        Vehicles.Values.Count(v => v.LineId == line.Id &&
            ((v.State is not (VehicleState.Depot or VehicleState.Broken or VehicleState.Yard) && !HeadingHome(v)) || LeavingYard(v)));

    /// <summary>Vehicles the timetable needs now (looking ahead so they leave the depot in time).</summary>
    public int RequiredVehicles(TransitLine line) => Math.Max(
        Timetable.VehiclesFor(line.CycleTime, line.Timetable.TargetHeadway(Time)),
        Timetable.VehiclesFor(line.CycleTime, line.Timetable.TargetHeadway(Time + DispatchLead)));

    public int PeakVehicles(TransitLine line) => line.Timetable.PeakVehicles(line.CycleTime);

    /// <summary>Effective headway right now: the timetable's, or longer if vehicles are missing.</summary>
    public double Headway(TransitLine line)
    {
        double target = line.Timetable.TargetHeadway(Time);
        int n = ActiveVehicles(line);
        if (!double.IsFinite(target) || n == 0) return double.PositiveInfinity;
        return Math.Max(target, line.CycleTime / n);
    }

    public void SetTimetable(TransitLine line, Timetable tt)
    {
        line.Timetable = tt;
        _routerDirty = true;
        OnChange();
    }

    public List<Vehicle> VehiclesOf(TransitLine line) => Vehicles.Values.Where(v => v.LineId == line.Id).ToList();

    public List<TransitLine> LinesAtStop(int stopId) => Lines.Values.Where(l => l.StopIds.Contains(stopId)).ToList();

    // ---------------------------------------------------------------- vehicles

    public Vehicle? BuyVehicle(int depotId, string? modelId = null)
    {
        if (!Depots.TryGetValue(depotId, out var depot)) return null;
        var def = Modes.Model(depot.Mode, modelId);
        if (!CanHouse(depot, def))
        {
            Notify(def.Length > 15 && depot.Mode == ModeId.Bus
                ? $"{depot.Name}: kein freier Gelenkbus-Stellplatz – baue einen (Betriebshof bearbeiten)"
                : $"{depot.Name} ist voll – baue weitere Stellplätze (Betriebshof bearbeiten)", true);
            return null;
        }
        if (!Pay(def.Price)) return null;
        int id = _nextId++;
        _vehicleCounter++;
        var v = new Vehicle
        {
            Id = id, Name = $"{def.Name} {_vehicleCounter}", Mode = depot.Mode, ModelId = def.Id, DepotId = depotId,
            X = depot.Pos.X, Y = depot.Pos.Y, BoughtAt = Time,
        };
        Vehicles[id] = v;
        EnsureHomeSpots(depot);
        OnChange();
        return v;
    }

    /// <summary>Connected parking spots not taken by vehicles based at the depot.</summary>
    public int FreeSpots(Depot d) => d.Layout.VehicleCapacity(d.Mode) - Vehicles.Values.Count(v => v.DepotId == d.Id);

    /// <summary>Lengths of the vehicles based at a depot (for spot assignment).</summary>
    public List<double> FleetLengths(Depot d) => Vehicles.Values.Where(v => v.DepotId == d.Id).OrderBy(v => v.Id).Select(v => v.Def.Length).ToList();

    /// <summary>Is there a fitting free spot for one more vehicle of this model?</summary>
    public bool CanHouse(Depot d, VehicleDef def) => d.Layout.Fits(FleetLengths(d).Append(def.Length).ToList(), d.Mode);

    // ---------------------------------------------------------------- depot yard

    /// <summary>Place a module in a depot yard. Returns an error message or null.</summary>
    /// <summary>Build a module; buildings may be given a drawn size w × h in cells (0 = standard size).</summary>
    public string? PlaceModule(Depot d, ModuleType type, int x, int y, int rot, int w = 0, int h = 0)
    {
        if (!DepotLayout.IsDrawable(type)) w = h = 0;
        if (type == ModuleType.TramSpot && d.Mode != ModeId.Tram) return "Abstellgleise gibt es nur im Tram-Betriebshof";
        if (type is ModuleType.BusSpot or ModuleType.LongBusSpot or ModuleType.Charger && d.Mode == ModeId.Tram)
            return "Im Tram-Betriebshof werden Abstellgleise gebaut";
        if (d.Layout.CanPlace(type, x, y, rot, w, h) is string err) return err;
        if (!Pay(DepotLayout.Cost(type, w, h))) return "Nicht genug Geld";
        d.Layout.Modules.Add(new PlacedModule { Type = type, X = x, Y = y, Rot = w > 0 ? 0 : rot, Sw = w, Sh = h });
        EnsureHomeSpots(d);
        OnChange();
        return null;
    }

    /// <summary>Remove the module at a cell. Refused if vehicles or drivers would lose their place.</summary>
    public string? RemoveModuleAt(Depot d, int x, int y)
    {
        var m = d.Layout.ModuleAt(x, y);
        if (m == null) return null;
        var fleet = FleetLengths(d);
        bool fitsBefore = d.Layout.Fits(fleet, d.Mode);
        d.Layout.Modules.Remove(m);
        // Only refuse if this removal takes away a spot that is needed (older saves may already be over capacity).
        if (fitsBefore && !d.Layout.Fits(fleet, d.Mode))
        {
            d.Layout.Modules.Add(m);
            return "Dann hätten nicht mehr alle Fahrzeuge einen Stellplatz";
        }
        if (d.Layout.DriverCapacity() < StaffCount(d.Id))
        {
            d.Layout.Modules.Add(m);
            return "Dann hätten nicht mehr alle Mitarbeiter Platz";
        }
        EnsureHomeSpots(d);
        OnChange();
        return null;
    }

    /// <summary>What a vehicle sells for: half the price, less for worn vehicles.</summary>
    public static double ResaleValue(Vehicle v) => v.Def.Price * 0.5 * (0.4 + 0.6 * v.Condition);

    public void SellVehicle(int id)
    {
        if (!Vehicles.TryGetValue(id, out var v)) return;
        if (v.State != VehicleState.Depot)
        {
            Notify("Fahrzeuge können nur im Betriebshof verkauft werden", true);
            return;
        }
        Money += ResaleValue(v);
        Vehicles.Remove(id);
        OnChange();
    }

    /// <summary>
    /// Put a vehicle into a line's pool (or take it out with null). Vehicles in the depot wait
    /// for the dispatcher; vehicles on the road switch over directly.
    /// </summary>
    public void AssignVehicle(int id, int? lineId)
    {
        if (!Vehicles.TryGetValue(id, out var v) || v.LineId == lineId) return;
        if (v.LineId != null)
        {
            Today.Lost += v.Passengers.Count;
            v.Passengers.Clear();
        }
        v.LineId = lineId;
        if (v.State != VehicleState.Depot) Dispatch(v, lineId == null ? DeadheadTarget.Depot : DeadheadTarget.Line);
        _dispatchTimer = DispatchInterval; // run the dispatcher on the next step
        _routerDirty = true;
        OnChange();
    }

    /// <summary>Assign (or buy) vehicles until the line's pool covers the timetable's peak.</summary>
    public void AssignMissing(TransitLine line)
    {
        int missing = PeakVehicles(line) - VehiclesOf(line).Count;
        while (missing-- > 0)
        {
            int before = VehiclesOf(line).Count;
            AddVehicleToLine(line);
            if (VehiclesOf(line).Count == before) break;
        }
    }

    /// <summary>Add a vehicle to a line: reuse an idle one or buy a new one at the closest depot.</summary>
    public void AddVehicleToLine(TransitLine line)
    {
        var first = Stops[line.StopIds[0]].Pos;
        double Dist(Depot d) => Util.Hypot(d.Pos.X - first.X, d.Pos.Y - first.Y);
        var def = Modes.Model(line.Mode, line.PreferredModel);
        var idle = Vehicles.Values.Where(v => v.LineId == null && v.Mode == line.Mode && (line.PreferredModel == null || v.ModelId == def.Id))
            .OrderBy(v => Dist(Depots[v.DepotId])).FirstOrDefault();
        if (idle != null)
        {
            AssignVehicle(idle.Id, line.Id);
            return;
        }
        var candidates = Depots.Values.Where(d => d.Mode == line.Mode).OrderBy(Dist).ToList();
        var depot = candidates.FirstOrDefault(d => CanHouse(d, def)) ?? candidates.FirstOrDefault();
        if (depot == null)
        {
            Notify(line.Mode == ModeId.Tram ? "Baue zuerst einen Tram-Betriebshof an den Gleisen" : "Baue zuerst einen Betriebshof", true);
            return;
        }
        var nv = BuyVehicle(depot.Id, def.Id);
        if (nv != null) AssignVehicle(nv.Id, line.Id);
    }

    public void RemoveVehicleFromLine(TransitLine line)
    {
        // Prefer a vehicle in the depot, then one heading home, then the emptiest one
        int Rank(Vehicle v) => v.State == VehicleState.Depot ? -1 : HeadingHome(v) ? 0 : 1;
        var pick = VehiclesOf(line).OrderBy(Rank).ThenBy(v => v.Passengers.Count).FirstOrDefault();
        if (pick != null) AssignVehicle(pick.Id, null);
    }

    /// <summary>
    /// Start vehicles from the depot when the timetable needs more of them.
    /// Surplus vehicles return to the depot from the terminus (see StepVehicle).
    /// </summary>
    private void RunDispatcher()
    {
        foreach (var line in Lines.Values)
        {
            line.DriverShortage = false;
            line.MaintenanceShortage = false;
            int active = ActiveVehicles(line);
            int req = RequiredVehicles(line);
            if (active >= req) continue;
            var first = Stops[line.Runs[0].StopIds[0]].Pos;
            var inDepot = VehiclesOf(line).Where(v => v.State == VehicleState.Depot).ToList();
            // Only vehicles in good shape go out; the best ones first, then the closest depot
            var waiting = inDepot.Where(ReadyForService)
                .OrderByDescending(v => Math.Round(v.Condition, 1))
                .ThenBy(v => Util.Hypot(Depots[v.DepotId].Pos.X - first.X, Depots[v.DepotId].Pos.Y - first.Y)).ToList();
            if (waiting.Count < Math.Min(req - active, inDepot.Count)) line.MaintenanceShortage = true;
            foreach (var v in waiting)
            {
                if (active >= req) break;
                if (!TakeDriver(v))
                {
                    line.DriverShortage = true;
                    continue;
                }
                Dispatch(v, DeadheadTarget.Line);
                if (v.State != VehicleState.Depot) active++;
            }
        }
        string sig = string.Join(",", Lines.Values.Select(l => Math.Round(Headway(l))));
        if (sig != _headwaySig)
        {
            _headwaySig = sig;
            _routerDirty = true;
        }
    }

    /// <summary>Good enough to leave the depot (with some margin so vehicles don't bounce).</summary>
    public static bool ReadyForService(Vehicle v) => v.Condition >= 0.35 && (!v.Def.Electric || v.Charge >= 0.6);

    /// <summary>Should return to the depot for repair or charging at the next terminus.</summary>
    public static bool NeedsService(Vehicle v) => v.Condition < 0.25 || (v.Def.Electric && v.Charge < 0.15);

    private void Dispatch(Vehicle v, DeadheadTarget to)
    {
        if (v.State == VehicleState.Depot)
        {
            // Parked vehicles first drive out of the yard; the road trip starts at the gate
            if (to == DeadheadTarget.Depot || !TakeDriver(v)) return;
            BeginBoarding(v, to);
            return;
        }
        if (v.State == VehicleState.Yard)
        {
            YardRedirect(v, to);
            return;
        }
        RoadTrip(v, to, fromDepot: false);
    }

    /// <summary>Drive on the road network to the line start or back to the depot.</summary>
    private void RoadTrip(Vehicle v, DeadheadTarget to, bool fromDepot)
    {
        var depot = Depots[v.DepotId];
        var from = fromDepot ? depot.Pos : SnapFor(v.Mode, v.X, v.Y, 80) ?? depot.Pos;
        var dest = depot.Pos;
        if (to == DeadheadTarget.Line)
        {
            if (v.LineId is not int lid || !Lines.TryGetValue(lid, out var line)) return;
            dest = Stops[line.Runs[0].StopIds[0]].Pos;
        }
        var r = RouteFor(v.Mode, from, dest);
        if (r == null)
        {
            if (to == DeadheadTarget.Line)
            {
                Notify($"{v.Name} findet keinen Weg vom Betriebshof zur Linie", true);
                v.LineId = null;
            }
            ParkInDepot(v);
            return;
        }
        if (fromDepot)
        {
            v.X = depot.Pos.X;
            v.Y = depot.Pos.Y;
        }
        v.Path = RoutePath.From(r);
        v.S = 0;
        v.Seg = 0;
        v.State = VehicleState.Deadhead;
        v.DeadheadTo = to;
    }

    private void ParkInDepot(Vehicle v)
    {
        var depot = Depots[v.DepotId];
        v.State = VehicleState.Depot;
        v.Task = YardTask.None;
        v.YardPath = null;
        v.Bay = YardBay.Spot;
        v.Path = null;
        v.V = 0;
        v.X = depot.Pos.X;
        v.Y = depot.Pos.Y;
        Today.Lost += v.Passengers.Count;
        v.Passengers.Clear();
        ReleaseDriver(v);
        EnsureHomeSpots(depot);
    }
}
