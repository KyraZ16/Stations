namespace Stations.Core.Sim;

/// <summary>
/// Life in the depot yard: vehicles drive in through the gate, along the driveways to their home spot,
/// into the workshop or wash bay, and out again. Vehicles keep a distance; a vehicle that would
/// drive into another one waits (up to a limit), so cramped layouts cause queues.
/// </summary>
public sealed partial class Game
{
    public const double YardSpeed = 4; // m/s
    private const double YardGap = 2.5; // m of free space between two vehicles
    private const double MaxYardWait = 15; // s before a blocked vehicle squeezes past anyway

    public static bool InYard(Vehicle v) => v.State is VehicleState.Depot or VehicleState.Yard;

    /// <summary>Leaving the yard to start service on a line.</summary>
    public static bool LeavingYard(Vehicle v) => v.State == VehicleState.Yard && v.Task is YardTask.Exit or YardTask.WaitDriver && v.ExitThen == DeadheadTarget.Line;

    private PlacedModule? ModuleAt(Depot d, int x, int y) =>
        d.Layout.Modules.FirstOrDefault(m => m.X == x && m.Y == y);

    /// <summary>
    /// Give every vehicle of the depot a home spot that fits its length (keeping existing ones when
    /// still valid) and move vehicles out of bays that no longer exist.
    /// </summary>
    public void EnsureHomeSpots(Depot d)
    {
        var reach = d.Layout.Reachable();
        var spots = d.Layout.ConnectedSpots(d.Mode);
        var fleet = Vehicles.Values.Where(v => v.DepotId == d.Id).OrderByDescending(v => v.Def.Length).ThenBy(v => v.Id).ToList();
        var taken = new HashSet<(int, int)>();
        foreach (var v in fleet)
        {
            var s = ModuleAt(d, v.SpotX, v.SpotY);
            if (s != null && DepotLayout.IsSpot(s.Type) && spots.Contains(s) && DepotLayout.SpotLength(s) >= v.Def.Length && taken.Add((s.X, s.Y)))
                continue;
            v.SpotX = v.SpotY = -1;
        }
        foreach (var v in fleet.Where(v => v.SpotX < 0))
        {
            var free = spots.Where(s => !taken.Contains((s.X, s.Y)) && DepotLayout.SpotLength(s) >= v.Def.Length)
                .OrderBy(DepotLayout.SpotLength).FirstOrDefault();
            if (free == null) continue;
            v.SpotX = free.X;
            v.SpotY = free.Y;
            taken.Add((free.X, free.Y));
        }
        foreach (var v in fleet.Where(v => v.State == VehicleState.Depot))
        {
            var bay = v.Bay == YardBay.Spot ? null : ModuleAt(d, v.BayX, v.BayY);
            if (v.Bay != YardBay.Spot && (bay == null || !d.Layout.IsConnected(bay, reach))) v.Bay = YardBay.Spot;
            SetPose(v, BayPose(d, v));
        }
    }

    /// <summary>Where the vehicle stands for its current bay (home spot, workshop lane or wash bay).</summary>
    private (double X, double Y, double Ang) BayPose(Depot d, Vehicle v)
    {
        var m = v.Bay == YardBay.Spot ? ModuleAt(d, v.SpotX, v.SpotY) : ModuleAt(d, v.BayX, v.BayY);
        if (m == null)
        {
            var (sx, sy) = d.Layout.StreetPoint();
            return (sx, sy - 9, -Math.PI / 2); // no spot: stands just inside the gate
        }
        return d.Layout.Pose(m, v.BayIndex);
    }

    private static void SetPose(Vehicle v, (double X, double Y, double Ang) p)
    {
        v.YardX = p.X;
        v.YardY = p.Y;
        v.YardAng = p.Ang;
    }

    /// <summary>Plan a trip inside the yard. Without a driveway connection the vehicle simply appears at the goal.</summary>
    private void StartYardTrip(Depot d, Vehicle v, YardTask task, PlacedModule? target = null, int bay = 0)
    {
        var L = d.Layout;
        var reach = L.Reachable();
        var pts = new List<(double X, double Y)>();
        (int X, int Y)? startCell;

        if (task == YardTask.Enter)
        {
            var street = L.StreetPoint();
            pts.Add(street);
            startCell = L.GateCellNear(L.Width / 2);
            SetPose(v, (street.X, street.Y, -Math.PI / 2));
        }
        else
        {
            pts.Add((v.YardX, v.YardY));
            var from = v.Bay == YardBay.Spot ? ModuleAt(d, v.SpotX, v.SpotY) : ModuleAt(d, v.BayX, v.BayY);
            startCell = from != null ? L.AccessCell(from, reach) : L.GateCellNear(L.Width / 2);
        }

        (int X, int Y)? endCell;
        (double X, double Y, double Ang) endPose;
        if (task == YardTask.Exit)
        {
            endCell = startCell is { } sc ? L.GateCellNear(sc.X) : null;
            var st = L.StreetPoint();
            endPose = (st.X, st.Y, Math.PI / 2);
        }
        else
        {
            target ??= ModuleAt(d, v.SpotX, v.SpotY);
            endCell = target != null ? L.AccessCell(target, reach) : null;
            endPose = target != null ? L.Pose(target, bay) : BayPose(d, v);
        }

        var cells = startCell is { } a && endCell is { } b ? L.FindPath(a, b, reach) : null;
        v.Task = task;
        v.YardIdx = 0;
        v.YardWait = 0;
        if (target != null && task is YardTask.ToWorkshop or YardTask.ToWash)
        {
            v.BayX = target.X;
            v.BayY = target.Y;
            v.BayIndex = bay;
        }
        if (cells == null)
        {
            // No connection (or no spot): appear at the goal right away
            SetPose(v, endPose);
            FinishYardTrip(v);
            return;
        }
        foreach (var c in cells) pts.Add(L.CellCenter(c.X, c.Y));
        pts.Add((endPose.X, endPose.Y));
        v.YardPath = pts;
        v.State = VehicleState.Yard;
    }

    private void StepYard(Vehicle v, double dt)
    {
        if (v.Task == YardTask.WaitDriver)
        {
            WaitForDriver(v, dt);
            return;
        }
        var path = v.YardPath;
        if (path == null || v.YardIdx >= path.Count - 1)
        {
            FinishYardTrip(v);
            return;
        }
        var (tx, ty) = path[v.YardIdx + 1];
        double dx = tx - v.YardX, dy = ty - v.YardY;
        double dist = Util.Hypot(dx, dy);
        double hx = dist > 1e-6 ? dx / dist : Math.Cos(v.YardAng), hy = dist > 1e-6 ? dy / dist : Math.Sin(v.YardAng);

        // Keep distance: is the space just in front of our nose taken by another vehicle's body?
        // Bodies are segments along their heading, so merging from the side is caught too.
        if (v.YardWait < MaxYardWait)
        {
            foreach (var o in Vehicles.Values)
            {
                if (o == v || o.DepotId != v.DepotId || o.State != VehicleState.Yard) continue;
                if (!Blocks(v, o, hx, hy)) continue;
                // Two vehicles blocking each other (head-on, merging): the lower number has right of way
                if (Blocks(o, v, Math.Cos(o.YardAng), Math.Sin(o.YardAng)) && v.Id < o.Id) continue;
                v.YardWait += dt;
                return;
            }
        }

        double step = YardSpeed * dt;
        while (step > 0 && v.YardIdx < path.Count - 1)
        {
            (tx, ty) = path[v.YardIdx + 1];
            dx = tx - v.YardX;
            dy = ty - v.YardY;
            dist = Util.Hypot(dx, dy);
            if (dist > 1e-6) v.YardAng = Math.Atan2(dy, dx);
            if (dist <= step)
            {
                v.YardX = tx;
                v.YardY = ty;
                step -= dist;
                v.YardIdx++;
                v.YardWait = 0;
            }
            else
            {
                v.YardX += dx / dist * step;
                v.YardY += dy / dist * step;
                step = 0;
            }
        }
        if (v.YardIdx >= path.Count - 1) FinishYardTrip(v);
    }

    /// <summary>
    /// Is the space just in front of v's nose (heading hx, hy) taken by o's body?
    /// Bodies are segments along their heading, so merging from the side is caught too.
    /// </summary>
    private static bool Blocks(Vehicle v, Vehicle o, double hx, double hy)
    {
        double half = v.Def.Length / 2;
        double probeX = v.YardX + hx * (half + YardGap), probeY = v.YardY + hy * (half + YardGap);
        double oh = o.Def.Length / 2, ca = Math.Cos(o.YardAng), sa = Math.Sin(o.YardAng);
        var (d, _) = Util.DistToSegment(probeX, probeY, o.YardX - ca * oh, o.YardY - sa * oh, o.YardX + ca * oh, o.YardY + sa * oh);
        bool ahead = (o.YardX - v.YardX) * hx + (o.YardY - v.YardY) * hy > 0; // vehicles behind wait for us instead
        return d < 2.2 && ahead;
    }

    private void FinishYardTrip(Vehicle v)
    {
        var task = v.Task;
        v.Task = YardTask.None;
        v.YardPath = null;
        var d = Depots[v.DepotId];
        switch (task)
        {
            case YardTask.Exit:
                v.State = VehicleState.Depot; // RoadTrip starts from the depot position
                RoadTrip(v, v.ExitThen, fromDepot: true);
                return;
            case YardTask.ToWorkshop:
                v.Bay = YardBay.Workshop;
                break;
            case YardTask.ToWash:
                v.Bay = YardBay.Wash;
                break;
            default:
                v.Bay = YardBay.Spot;
                break;
        }
        v.State = VehicleState.Depot;
        SetPose(v, BayPose(d, v));
        ReleaseDriver(v); // the driver's shift on this vehicle ends once it stands in the yard
    }

    /// <summary>A vehicle back from the road drives into the yard: straight to the workshop or wash if needed.</summary>
    private void ArriveAtDepot(Vehicle v)
    {
        var d = Depots[v.DepotId];
        Today.Lost += v.Passengers.Count;
        v.Passengers.Clear();
        v.Path = null;
        v.V = 0;
        v.X = d.Pos.X;
        v.Y = d.Pos.Y;
        EnsureHomeSpots(d);
        var (target, bay, task) = ServiceTarget(d, v);
        // Enter through the gate; worn or dirty vehicles go straight to a free workshop lane or wash bay
        StartYardTrip(d, v, YardTask.Enter, task == YardTask.ToSpot ? null : target, bay);
        if (task != YardTask.ToSpot)
        {
            v.BayX = target!.X;
            v.BayY = target.Y;
            v.BayIndex = bay;
            if (v.State == VehicleState.Yard) v.Task = task;
            else { v.Bay = task == YardTask.ToWorkshop ? YardBay.Workshop : YardBay.Wash; SetPose(v, BayPose(d, v)); }
        }
    }

    /// <summary>Leave the yard, then drive on the road to the line (or back to the depot).</summary>
    private void StartExit(Vehicle v, DeadheadTarget then)
    {
        var d = Depots[v.DepotId];
        v.ExitThen = then;
        StartYardTrip(d, v, YardTask.Exit);
    }

    /// <summary>Change plans for a vehicle that is currently driving inside the yard.</summary>
    private void YardRedirect(Vehicle v, DeadheadTarget to)
    {
        var d = Depots[v.DepotId];
        if (v.Task == YardTask.WaitDriver)
        {
            v.ExitThen = to;
            if (to == DeadheadTarget.Line) return;
            // not needed after all: stay parked, the driver goes back to the break room
            v.Task = YardTask.None;
            v.State = VehicleState.Depot;
            ReleaseDriver(v);
            return;
        }
        if (v.Task == YardTask.Exit)
        {
            if (to == DeadheadTarget.Line) { v.ExitThen = DeadheadTarget.Line; return; }
            // no longer needed on the line: turn around to the home spot
            v.Bay = YardBay.Spot;
            StartYardTrip(d, v, YardTask.ToSpot);
            return;
        }
        if (to == DeadheadTarget.Line) StartExit(v, to);
    }

    /// <summary>Free workshop lane or wash bay for a vehicle that needs it, else its home spot.</summary>
    private (PlacedModule? Target, int Bay, YardTask Task) ServiceTarget(Depot d, Vehicle v)
    {
        var reach = d.Layout.Reachable();
        bool Busy(PlacedModule m, int bay) => Vehicles.Values.Any(o => o != v && o.DepotId == d.Id && o.BayX == m.X && o.BayY == m.Y && o.BayIndex == bay &&
            ((o.State == VehicleState.Depot && o.Bay != YardBay.Spot) || (o.State == VehicleState.Yard && o.Task is YardTask.ToWorkshop or YardTask.ToWash)));
        if (v.Condition < 0.95)
        {
            foreach (var w in d.Layout.Modules.Where(m => m.Type == ModuleType.Workshop && d.Layout.IsConnected(m, reach)))
                for (int b = 0; b < BaysPerWorkshop; b++)
                    if (!Busy(w, b) && Staffed(d, w, b)) return (w, b, YardTask.ToWorkshop);
        }
        if (v.Cleanliness < 0.9)
        {
            foreach (var w in d.Layout.Modules.Where(m => m.Type == ModuleType.Wash && d.Layout.IsConnected(m, reach)))
                if (!Busy(w, 0) && Staffed(d, w, 0)) return (w, 0, YardTask.ToWash);
        }
        return (ModuleAt(d, v.SpotX, v.SpotY), 0, YardTask.ToSpot);
    }

    /// <summary>
    /// Yard services: repair in workshop lanes, wash in wash bays, charge at spots next to a charger,
    /// and send parked vehicles to free service bays (and back when done).
    /// </summary>
    private void ServiceDepots(double dt)
    {
        foreach (var d in Depots.Values)
        {
            var reach = d.Layout.Reachable();
            d.Workshops = d.Layout.Modules.Count(m => m.Type == ModuleType.Workshop && d.Layout.IsConnected(m, reach));
            d.WashBays = d.Layout.Modules.Count(m => m.Type == ModuleType.Wash && d.Layout.IsConnected(m, reach));
            var chargers = d.Layout.Modules.Where(m => m.Type == ModuleType.Charger && d.Layout.IsConnected(m, reach)).ToList();
            d.Chargers = chargers.Count;
            var home = Vehicles.Values.Where(v => v.DepotId == d.Id && v.State == VehicleState.Depot).ToList();
            d.Repairing = d.Washing = d.Charging = 0;

            foreach (var v in home)
            {
                if (v.Bay == YardBay.Workshop)
                {
                    if (!AtWork(d, v.BayX, v.BayY, v.BayIndex)) continue; // no mechanic at work in this lane right now
                    double gain = Math.Min(1 - v.Condition, RepairPerHour * dt / 3600);
                    v.Condition += gain;
                    double c = gain * 100 * v.Def.RepairCostPerPercent;
                    Money -= c;
                    Today.Costs += c;
                    d.Repairing++;
                    if (v.Condition >= 0.999) GoOnFromBay(d, v);
                }
                else if (v.Bay == YardBay.Wash)
                {
                    if (!AtWork(d, v.BayX, v.BayY, 0)) continue; // nobody operating the wash
                    v.Cleanliness = Math.Min(1, v.Cleanliness + dt / (WashMinutes * 60));
                    d.Washing++;
                    if (v.Cleanliness >= 0.999) GoOnFromBay(d, v);
                }
            }

            // Each charger charges the vehicle on one spot right next to it
            var charged = new HashSet<Vehicle>();
            foreach (var ch in chargers)
            {
                var v = home.FirstOrDefault(x => x.Bay == YardBay.Spot && x.Def.Electric && x.Charge < 0.999 && !charged.Contains(x) &&
                    ModuleAt(d, x.SpotX, x.SpotY) is { } s && Adjacent(ch, s));
                if (v == null) continue;
                v.Charge = Math.Min(1, v.Charge + dt / (ChargeHours * 3600));
                charged.Add(v);
            }
            d.Charging = charged.Count;
            InteriorCleaning(d, dt);

            // Parked vehicles that need service drive to a free bay
            foreach (var v in home.Where(v => v.Bay == YardBay.Spot && (v.Condition < 0.95 || v.Cleanliness < 0.9)))
            {
                var (target, bay, task) = ServiceTarget(d, v);
                if (task != YardTask.ToSpot) StartYardTrip(d, v, task, target, bay);
            }
        }
        foreach (var l in Lines.Values)
        {
            var out_ = Vehicles.Values.Where(v => v.LineId == l.Id && !InYard(v)).ToList();
            l.Comfort = out_.Count > 0 ? out_.Average(v => v.Cleanliness) : 1;
        }
    }

    /// <summary>After repair: wash if needed and possible, otherwise back to the home spot.</summary>
    private void GoOnFromBay(Depot d, Vehicle v)
    {
        var (target, bay, task) = ServiceTarget(d, v);
        if (task == YardTask.ToWorkshop && v.Bay == YardBay.Workshop) return; // still needs repair
        if (task == YardTask.ToSpot) StartYardTrip(d, v, YardTask.ToSpot);
        else StartYardTrip(d, v, task, target, bay);
    }

    /// <summary>Is the (1×1) charger directly next to the spot (sharing an edge)?</summary>
    private static bool Adjacent(PlacedModule charger, PlacedModule spot) =>
        spot.Covers(charger.X + 1, charger.Y) || spot.Covers(charger.X - 1, charger.Y) ||
        spot.Covers(charger.X, charger.Y + 1) || spot.Covers(charger.X, charger.Y - 1);
}
