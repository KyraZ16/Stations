namespace Stations.Core.Sim;

/// <summary>
/// Staff on the depot grounds. Drivers come in at the start of the shift day, change at the lockers,
/// wait in the break room and walk to their vehicle when it is dispatched (the vehicle leaves only once
/// the driver is aboard, so a far-away office delays departures). Mechanics and cleaners work in
/// shifts; workshop lanes and wash bays only run while someone is on duty there, and spare cleaners
/// clean parked vehicles from the inside.
/// </summary>
public sealed partial class Game
{
    public const double WalkSpeed = 1.4; // m/s
    public const double WorkerWageDay = 200;
    public const double WorkerHireCost = 1_500;
    private const double ChangeTime = 40; // s at the locker
    private const double InteriorCleanMinutes = 40;
    private const double MaxBoardWait = 10 * 60; // s; after that the vehicle leaves anyway
    private const double PeopleInterval = 1; // s between staff updates

    public readonly Dictionary<int, Worker> Workers = new();
    /// <summary>Staff on the grounds right now, by staff id (runtime only).</summary>
    public readonly Dictionary<int, Person> People = new();
    private readonly HashSet<int> _peopleReady = new();
    private double _peopleTimer = PeopleInterval;

    // ---------------------------------------------------------------- shifts and hiring

    public static bool OnDuty(ShiftId s, double time)
    {
        double h = time % 86400 / 3600;
        return s switch
        {
            ShiftId.Early => h >= 6 && h < 14,
            ShiftId.Late => h >= 14 && h < 22,
            _ => h >= 22 || h < 6,
        };
    }

    public static string ShiftName(ShiftId s) => s switch
    {
        ShiftId.Early => "Früh (06–14)", ShiftId.Late => "Spät (14–22)", _ => "Nacht (22–06)",
    };

    public static string RoleName(StaffRole r, int n = 1) => r switch
    {
        StaffRole.Driver => n == 1 ? "Fahrer" : "Fahrer",
        StaffRole.Mechanic => n == 1 ? "Mechaniker" : "Mechaniker",
        _ => n == 1 ? "Reinigungskraft" : "Reinigungskräfte",
    };

    public List<Worker> WorkersOf(int depotId, StaffRole? role = null) =>
        Workers.Values.Where(w => w.DepotId == depotId && (role == null || w.Role == role)).OrderBy(w => w.Id).ToList();

    /// <summary>All staff of a depot (they share the office places).</summary>
    public int StaffCount(int depotId) => DriversOf(depotId).Count + WorkersOf(depotId).Count;

    public void HireWorkers(int depotId, StaffRole role, int n)
    {
        if (role == StaffRole.Driver)
        {
            HireDrivers(depotId, n);
            return;
        }
        if (!Depots.TryGetValue(depotId, out var depot) || n <= 0) return;
        int free = depot.Layout.DriverCapacity() - StaffCount(depotId);
        if (free <= 0)
        {
            Notify("Kein Platz für weitere Mitarbeiter – baue ein Sozialgebäude im Betriebshof", true);
            return;
        }
        if (n > free)
        {
            Notify($"Nur noch Platz für {free} Mitarbeiter – baue ein weiteres Sozialgebäude", true);
            n = free;
        }
        if (!Pay(WorkerHireCost * n)) return;
        for (int i = 0; i < n; i++)
        {
            // Fill the shift with the fewest people of this role
            var shift = Enum.GetValues<ShiftId>().OrderBy(s => Workers.Values.Count(w => w.DepotId == depotId && w.Role == role && w.Shift == s)).First();
            int id = _nextId++;
            Workers[id] = new Worker { Id = id, Name = Staff.RandomName(Rng), DepotId = depotId, Role = role, Shift = shift };
        }
        OnChange();
    }

    /// <summary>Dismiss one worker of the role, preferring someone who is off duty.</summary>
    public void FireWorker(int depotId, StaffRole role)
    {
        if (role == StaffRole.Driver)
        {
            FireDriver(depotId);
            return;
        }
        var w = WorkersOf(depotId, role).OrderBy(x => OnDuty(x.Shift, Time) ? 1 : 0).ThenByDescending(x => x.Id).FirstOrDefault();
        if (w == null) return;
        Workers.Remove(w.Id);
        People.Remove(w.Id);
        OnChange();
    }

    /// <summary>
    /// Suggested staff: one mechanic per workshop lane and one cleaner per wash bay in every shift,
    /// plus cleaners for interior cleaning (one per 15 vehicles).
    /// </summary>
    public int WorkerDemand(int depotId, StaffRole role)
    {
        if (role == StaffRole.Driver) return DriverDemand(depotId);
        if (!Depots.TryGetValue(depotId, out var d)) return 0;
        var y = Yard(d);
        if (role == StaffRole.Mechanic) return y.Lanes.Count * 3;
        int vehicles = Vehicles.Values.Count(v => v.DepotId == depotId);
        return y.Washes.Count * 3 + (int)Math.Ceiling(vehicles / 15.0);
    }

    // ---------------------------------------------------------------- simulation

    private void StepPeople(double dt)
    {
        _peopleTimer += dt;
        if (_peopleTimer >= PeopleInterval)
        {
            _peopleTimer = 0;
            foreach (var id in People.Keys.Where(id => !Drivers.ContainsKey(id) && !Workers.ContainsKey(id)).ToList()) People.Remove(id);
            foreach (var d in Depots.Values) SyncPeople(d);
        }
        foreach (var p in People.Values.ToList())
        {
            if (p.Act == PersonAct.Walking) Walk(p, dt);
            else if (p.Act == PersonAct.Changing && Time >= p.Until)
            {
                p.Act = PersonAct.Resting;
                if (p.AfterLocker == PersonGoal.Gate) WalkTo(p, PersonGoal.Gate);
                p.AfterLocker = PersonGoal.None;
            }
        }
    }

    private void Walk(Person p, double dt)
    {
        double step = WalkSpeed * dt;
        while (step > 0 && p.PathIdx < p.Path.Count)
        {
            var (tx, ty) = p.Path[p.PathIdx];
            double dx = tx - p.X, dy = ty - p.Y, dist = Util.Hypot(dx, dy);
            if (dist > 1e-6) p.Ang = Math.Atan2(dy, dx);
            if (dist <= step)
            {
                p.X = tx;
                p.Y = ty;
                p.Walked += dist;
                step -= dist;
                p.PathIdx++;
            }
            else
            {
                p.X += dx / dist * step;
                p.Y += dy / dist * step;
                p.Walked += step;
                step = 0;
            }
        }
        if (p.PathIdx >= p.Path.Count) Arrive(p);
    }

    private void Arrive(Person p)
    {
        p.Office = p.TargetOffice;
        if (!double.IsNaN(p.FaceAng)) p.Ang = p.FaceAng;
        var goal = p.Goal;
        p.Goal = PersonGoal.None;
        switch (goal)
        {
            case PersonGoal.Locker:
                p.Act = PersonAct.Changing;
                p.Until = Time + ChangeTime;
                break;
            case PersonGoal.Vehicle:
                if (p.VehicleId is int vid && Vehicles.TryGetValue(vid, out var v) && v.Task == YardTask.WaitDriver && v.DriverId == p.Id)
                {
                    People.Remove(p.Id); // aboard
                    StartExit(v, v.ExitThen);
                }
                else
                {
                    p.VehicleId = null; // departure was cancelled meanwhile
                    p.Act = PersonAct.Resting;
                }
                break;
            case PersonGoal.Gate:
                People.Remove(p.Id);
                break;
            case PersonGoal.Clean:
                p.Hidden = true;
                p.Act = PersonAct.Working;
                break;
            case PersonGoal.Post:
                p.Act = p.Role == StaffRole.Cleaner || LaneVehicle(p) != null ? PersonAct.Working : PersonAct.Waiting;
                break;
            default:
                p.Act = PersonAct.Resting;
                break;
        }
    }

    /// <summary>Bring the people of one depot in line with the staff state: who is on the grounds and where they go.</summary>
    private void SyncPeople(Depot d)
    {
        bool instant = _peopleReady.Add(d.Id); // first time (new game or after loading): place everyone directly
        var yard = Yard(d);

        foreach (var drv in Drivers.Values.Where(x => x.DepotId == d.Id).ToList())
        {
            People.TryGetValue(drv.Id, out var p);
            var v = drv.VehicleId is int vid && Vehicles.TryGetValue(vid, out var vv) ? vv : null;
            bool boarding = v != null && v.State == VehicleState.Yard && v.Task == YardTask.WaitDriver;
            bool available = v == null && drv.Worked < ShiftMax - ShiftMinLeft;
            if (boarding)
            {
                if (p == null || p.Goal != PersonGoal.Vehicle || p.VehicleId != v!.Id) SendDriver(d, drv, v!);
            }
            else if (available)
            {
                if (p == null)
                {
                    p = Spawn(d, drv.Id, StaffRole.Driver, instant);
                    if (instant) PlaceAtRest(d, p);
                    else GoChange(p, PersonGoal.Seat);
                }
                else if (p.Act is PersonAct.Resting && p.Goal == PersonGoal.None && !p.Hidden && (p.Office == null || !yard.Offices.Contains(p.Office)))
                    GoRest(d, p);
            }
            else if (p != null && !Leaving(p))
            {
                // Shift over (back from the road), or taken off to relieve a colleague at the terminus
                if (v != null && !InYard(v)) WalkTo(p, PersonGoal.Gate);
                else GoChange(p, PersonGoal.Gate);
            }
        }

        // Yard staff: on the grounds during their shift
        foreach (var w in Workers.Values.Where(x => x.DepotId == d.Id))
        {
            People.TryGetValue(w.Id, out var p);
            bool duty = OnDuty(w.Shift, Time);
            if (duty && p == null)
            {
                p = Spawn(d, w.Id, w.Role, instant);
                if (!instant) GoChange(p, PersonGoal.Post);
            }
            else if (!duty && p != null && !Leaving(p))
            {
                p.Hidden = false;
                p.Post = null;
                p.VehicleId = null;
                GoChange(p, PersonGoal.Gate);
            }
        }

        AssignPosts(d, yard, instant);
        d.OnSite = People.Values.Count(p => p.DepotId == d.Id);
    }

    private static bool Leaving(Person p) => p.Goal == PersonGoal.Gate || p.AfterLocker == PersonGoal.Gate;

    /// <summary>Busy with the locker on the way in: don't send anywhere else yet.</summary>
    private static bool Arriving(Person p) => p.Goal == PersonGoal.Locker || p.Act == PersonAct.Changing;

    /// <summary>Mechanics to workshop lanes, cleaners to wash bays (spares clean parked vehicles inside).</summary>
    private void AssignPosts(Depot d, YardInfo yard, bool instant)
    {
        var L = d.Layout;
        var lanes = yard.Lanes;
        var washes = yard.Washes;

        var mechanics = People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Mechanic && !Leaving(p)).OrderBy(p => p.Id).ToList();
        var cleaners = People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Cleaner && !Leaving(p)).OrderBy(p => p.Id).ToList();
        for (int i = 0; i < mechanics.Count; i++) mechanics[i].Post = i < lanes.Count ? lanes[i] : null;
        for (int i = 0; i < cleaners.Count; i++)
        {
            var c = cleaners[i];
            var post = i < washes.Count ? washes[i] : ((int, int, int)?)null;
            if (post != null && c.VehicleId != null)
            {
                if (c.Hidden) Unhide(c);
                c.VehicleId = null;
            }
            c.Post = post;
        }
        d.LanesStaffed = mechanics.Count(p => p.Post != null);
        d.WashStaffed = cleaners.Count(p => p.Post != null);

        foreach (var p in mechanics.Concat(cleaners))
        {
            if (Arriving(p)) continue;
            if (p.Post is { } post && ModuleAt(d, post.X, post.Y) is { } m)
            {
                var spot = p.Role == StaffRole.Cleaner ? L.WashPost(m, post.Bay)
                    : LaneVehicle(p) != null ? L.WorkshopWorkPoint(m, post.Bay) : L.WorkshopBench(m, post.Bay);
                if (instant) Place(p, spot, p.Role == StaffRole.Cleaner || LaneVehicle(p) != null ? PersonAct.Working : PersonAct.Waiting);
                else if (Far(p, spot)) WalkTo(p, PersonGoal.Post, (spot.X, spot.Y), face: spot.Ang);
                else if (p.Act != PersonAct.Walking) p.Act = p.Role == StaffRole.Cleaner || LaneVehicle(p) != null ? PersonAct.Working : PersonAct.Waiting;
                continue;
            }
            if (p.Role == StaffRole.Cleaner && CleanInterior(d, p)) continue;
            // nothing to do: break room
            if (p.Goal == PersonGoal.None && !p.Hidden && (p.Act != PersonAct.Resting || p.Office == null || !yard.Offices.Contains(p.Office)))
            {
                if (instant) PlaceAtRest(d, p);
                else GoRest(d, p);
            }
        }
    }

    /// <summary>Spare cleaners: walk to the dirtiest parked vehicle and clean it from the inside.</summary>
    private bool CleanInterior(Depot d, Person p)
    {
        if (p.VehicleId is int cur && Vehicles.TryGetValue(cur, out var cv) && cv.State == VehicleState.Depot && cv.Cleanliness < 0.98)
            return true; // still at it (or on the way)
        if (p.Hidden) Unhide(p);
        p.VehicleId = null;
        var taken = People.Values.Where(o => o != p && o.VehicleId != null).Select(o => o.VehicleId!.Value).ToHashSet();
        var target = Vehicles.Values.Where(v => v.DepotId == d.Id && v.State == VehicleState.Depot && v.Bay == YardBay.Spot &&
                v.Cleanliness < 0.85 && !taken.Contains(v.Id))
            .OrderBy(v => v.Cleanliness).FirstOrDefault();
        if (target == null) return false;
        p.VehicleId = target.Id;
        WalkTo(p, PersonGoal.Clean, BoardPoint(target));
        return true;
    }

    /// <summary>Cleaners working inside vehicles raise their cleanliness; called from the yard services.</summary>
    private void InteriorCleaning(Depot d, double dt)
    {
        foreach (var p in People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Cleaner && p.Hidden && p.Act == PersonAct.Working))
        {
            if (p.VehicleId is not int vid || !Vehicles.TryGetValue(vid, out var v) || v.State != VehicleState.Depot)
            {
                Unhide(p);
                continue;
            }
            v.Cleanliness = Math.Min(1, v.Cleanliness + dt / (InteriorCleanMinutes * 60));
            if (v.Cleanliness >= 0.98) Unhide(p);
        }
    }

    private void Unhide(Person p)
    {
        p.Hidden = false;
        p.VehicleId = null;
        p.Act = PersonAct.Resting;
        p.Goal = PersonGoal.None;
    }

    /// <summary>The vehicle standing in the mechanic's workshop lane, if any.</summary>
    private Vehicle? LaneVehicle(Person p) => p.Post is { } post
        ? Vehicles.Values.FirstOrDefault(v => v.DepotId == p.DepotId && v.State == VehicleState.Depot && v.Bay == YardBay.Workshop &&
            v.BayX == post.X && v.BayY == post.Y && v.BayIndex == post.Bay)
        : null;

    /// <summary>Is someone assigned to this workshop lane or wash bay (on duty, maybe still on the way)?</summary>
    private bool Staffed(Depot d, PlacedModule m, int bay) =>
        People.Values.Any(p => p.DepotId == d.Id && p.Post is { } post && post.X == m.X && post.Y == m.Y && post.Bay == bay);

    /// <summary>Is someone at work in this workshop lane or wash bay right now?</summary>
    private bool AtWork(Depot d, int x, int y, int bay) =>
        People.Values.Any(p => p.DepotId == d.Id && p.Act == PersonAct.Working && p.Post is { } post && post.X == x && post.Y == y && post.Bay == bay);

    // ---------------------------------------------------------------- drivers and vehicles

    /// <summary>A parked vehicle was dispatched: it waits until its driver has walked over and boarded.</summary>
    private void BeginBoarding(Vehicle v, DeadheadTarget to)
    {
        v.ExitThen = to;
        v.State = VehicleState.Yard;
        v.Task = YardTask.WaitDriver;
        v.YardPath = null;
        v.YardWait = 0;
        if (v.DriverId is int id && Drivers.TryGetValue(id, out var drv)) SendDriver(Depots[v.DepotId], drv, v);
        else StartExit(v, to);
    }

    private void SendDriver(Depot d, Driver drv, Vehicle v)
    {
        if (!People.TryGetValue(drv.Id, out var p))
        {
            bool instant = !_peopleReady.Contains(d.Id);
            p = Spawn(d, drv.Id, StaffRole.Driver, instant);
            if (instant) PlaceAtRest(d, p);
        }
        p.VehicleId = v.Id;
        p.AfterLocker = PersonGoal.None;
        p.Seat = -1;
        WalkTo(p, PersonGoal.Vehicle, BoardPoint(v));
    }

    /// <summary>Waiting for the driver; after a long wait (no path, driver stuck) the vehicle leaves anyway.</summary>
    private void WaitForDriver(Vehicle v, double dt)
    {
        v.YardWait += dt;
        if (v.YardWait < MaxBoardWait && v.DriverId is int id && People.ContainsKey(id)) return;
        if (v.DriverId is int did) People.Remove(did);
        StartExit(v, v.ExitThen);
    }

    /// <summary>The driver gets out after parking (or after a cancelled departure).</summary>
    private void DriverGetsOut(Vehicle v, Driver d)
    {
        if (!InYard(v) || !Depots.TryGetValue(v.DepotId, out var depot)) return;
        if (!People.TryGetValue(d.Id, out var p))
        {
            p = Spawn(depot, d.Id, StaffRole.Driver, false);
            (p.X, p.Y) = BoardPoint(v);
            p.Office = null;
        }
        p.Seat = -1;
        p.VehicleId = null;
        p.Path.Clear();
        p.Act = PersonAct.Resting;
        p.Goal = PersonGoal.None;
        p.AfterLocker = PersonGoal.None;
    }

    /// <summary>Where people get on and off: just behind the vehicle's rear end.</summary>
    private static (double X, double Y) BoardPoint(Vehicle v)
    {
        double back = v.Def.Length / 2 + 0.7;
        return (v.YardX - Math.Cos(v.YardAng) * back, v.YardY - Math.Sin(v.YardAng) * back);
    }

    // ---------------------------------------------------------------- moving people around

    private Person Spawn(Depot d, int id, StaffRole role, bool instant)
    {
        var (x, y) = d.Layout.PedestrianStart();
        var p = new Person { Id = id, Role = role, DepotId = d.Id, X = x, Y = y, Ang = -Math.PI / 2, Act = PersonAct.Resting };
        People[id] = p;
        return p;
    }

    private static void Place(Person p, (double X, double Y, double Ang) at, PersonAct act)
    {
        p.X = at.X;
        p.Y = at.Y;
        p.Ang = at.Ang;
        p.Act = act;
        p.Goal = PersonGoal.None;
        p.Path.Clear();
        p.Target = (at.X, at.Y);
        p.Office = null;
    }

    private static bool Far(Person p, (double X, double Y, double Ang) spot) =>
        Util.Hypot(p.Target.X - spot.X, p.Target.Y - spot.Y) > 0.3 || (p.Act != PersonAct.Walking && Util.Hypot(p.X - spot.X, p.Y - spot.Y) > 0.3);

    /// <summary>Layout facts people need often, recomputed only when the layout changes.</summary>
    private sealed class YardInfo
    {
        public long Sig;
        public required HashSet<(int, int)> Reach;
        public required List<PlacedModule> Offices;
        public required Dictionary<PlacedModule, ((int X, int Y) Dir, (int X, int Y) Cell)> Doors;
        public required Dictionary<PlacedModule, OfficePlan> Plans;
        public required List<(int X, int Y, int Bay)> Lanes, Washes;
    }

    private readonly Dictionary<int, YardInfo> _yards = new();

    private YardInfo Yard(Depot d)
    {
        var L = d.Layout;
        long sig = L.Width * 1000 + L.Height;
        foreach (var m in L.Modules) sig = sig * 31 + m.X * 131 + m.Y * 17 + (int)m.Type * 7 + m.Rot + m.W * 1009 + m.H * 3001;
        if (_yards.TryGetValue(d.Id, out var y) && y.Sig == sig) return y;
        var reach = L.Reachable();
        var offices = L.Modules.Where(m => m.Type == ModuleType.Office && L.IsConnected(m, reach)).ToList();
        y = new YardInfo
        {
            Sig = sig, Reach = reach, Offices = offices,
            Doors = L.Modules.Where(m => m.Type == ModuleType.Office).ToDictionary(o => o, o => L.OfficeDoor(o, reach)),
            Plans = new(),
            Lanes = L.Modules.Where(m => m.Type == ModuleType.Workshop && L.IsConnected(m, reach))
                .SelectMany(m => Enumerable.Range(0, DepotLayout.Lanes(m)).Select(b => (m.X, m.Y, b))).ToList(),
            Washes = L.Modules.Where(m => m.Type == ModuleType.Wash && L.IsConnected(m, reach))
                .SelectMany(m => Enumerable.Range(0, DepotLayout.Lanes(m)).Select(b => (m.X, m.Y, b))).ToList(),
        };
        foreach (var (o, door) in y.Doors) y.Plans[o] = DepotLayout.OfficePlanFor(o, door.Dir);
        _yards[d.Id] = y;
        return y;
    }

    private List<PlacedModule> Offices(Depot d) => Yard(d).Offices;

    private OfficePlan Plan(Depot d, PlacedModule office) =>
        Yard(d).Plans.TryGetValue(office, out var plan) ? plan : DepotLayout.OfficePlanFor(office, Door(d, office).Dir);

    private ((int X, int Y) Dir, (int X, int Y) Cell) Door(Depot d, PlacedModule office) =>
        Yard(d).Doors.TryGetValue(office, out var door) ? door : d.Layout.OfficeDoor(office);

    /// <summary>To the locker first (shift start or end), then on to the next goal.</summary>
    private void GoChange(Person p, PersonGoal then)
    {
        var d = Depots[p.DepotId];
        var office = p.Office != null && d.Layout.Modules.Contains(p.Office) ? p.Office : Offices(d).OrderBy(o => p.Id % 7 + o.X).FirstOrDefault();
        p.Seat = -1;
        if (office == null)
        {
            p.Act = PersonAct.Resting;
            p.AfterLocker = PersonGoal.None;
            if (then == PersonGoal.Gate) WalkTo(p, PersonGoal.Gate);
            return;
        }
        var door = Door(d, office).Dir;
        var plan = Plan(d, office);
        double lx = plan.Lockers[p.Id % plan.Lockers.Count];
        var at = d.Layout.OfficePoint(office, door, lx, plan.LockerStandZ);
        p.AfterLocker = then;
        WalkTo(p, PersonGoal.Locker, at, office, DepotLayout.OfficeAngle(door, 0, -1));
    }

    /// <summary>To a free seat in a break room (or a place to stand if all are taken).</summary>
    private void GoRest(Depot d, Person p)
    {
        RestSpot(d, p, out var at, out var office, out var face);
        if (office == p.Office && Util.Hypot(p.X - at.X, p.Y - at.Y) < 0.5)
        {
            p.Act = PersonAct.Resting;
            return;
        }
        WalkTo(p, PersonGoal.Seat, at, office, face);
    }

    private void PlaceAtRest(Depot d, Person p)
    {
        RestSpot(d, p, out var at, out var office, out var face);
        p.X = at.X;
        p.Y = at.Y;
        p.Ang = face;
        p.Office = office;
        p.Target = at;
        p.Path.Clear();
        p.Act = PersonAct.Resting;
        p.Goal = PersonGoal.None;
    }

    private void RestSpot(Depot d, Person p, out (double X, double Y) at, out PlacedModule? office, out double face)
    {
        var L = d.Layout;
        office = null;
        at = (0, 0);
        face = -Math.PI / 2;
        p.Seat = -1;
        var offices = Offices(d);
        foreach (var o in offices)
        {
            var used = People.Values.Where(x => x != p && (x.Office == o || x.TargetOffice == o)).Select(x => x.Seat).ToHashSet();
            // keep the current seat, else the first free one
            var plan = Plan(d, o);
            var order = Enumerable.Range(0, plan.Seats.Count).OrderBy(s => p.Office == o && s == p.Seat ? -1 : s);
            foreach (int s in order)
            {
                if (used.Contains(s)) continue;
                var door = Door(d, o).Dir;
                var seat = plan.Seats[s];
                at = L.OfficePoint(o, door, seat.X, seat.Z);
                face = DepotLayout.OfficeAngle(door, seat.FaceX, seat.FaceZ);
                office = o;
                p.Seat = s;
                return;
            }
        }
        if (offices.Count > 0)
        {
            // All seats taken: stand in the hallway or by the kitchen
            var o = offices[p.Id % offices.Count];
            var door = Door(d, o).Dir;
            var stand = Plan(d, o).Standing;
            var (sx, sz) = stand[p.Id % stand.Count];
            at = L.OfficePoint(o, door, sx, sz);
            face = DepotLayout.OfficeAngle(door, (p.Id % 3) - 1, -1);
            office = o;
            return;
        }
        // No office: wait outside by the entrance
        var (gx, gy) = L.CellCenter(L.PedestrianGate().X, L.PedestrianGate().Y);
        at = (gx + (p.Id % 5 - 2) * 0.8, gy - 1 - p.Id % 3 * 0.8);
    }

    private void WalkTo(Person p, PersonGoal goal) => WalkTo(p, goal, Depots[p.DepotId].Layout.PedestrianStart());

    /// <summary>
    /// Plan a walk: out of the current office through its door, over the walkable cells,
    /// into the target office through its door and the hallway, to the target point.
    /// </summary>
    private void WalkTo(Person p, PersonGoal goal, (double X, double Y) target, PlacedModule? targetOffice = null, double face = double.NaN)
    {
        var d = Depots[p.DepotId];
        var L = d.Layout;
        var pts = new List<(double X, double Y)>();
        var from = p.Office != null && L.Modules.Contains(p.Office) ? p.Office : null;
        if (p.Hidden) p.Hidden = false;

        if (from != null && from == targetOffice)
        {
            // Within the same office: via the hallway when changing sides
            var door = Door(d, from).Dir;
            pts.Add(L.OfficePoint(from, door, 0, Plan(d, from).HallZ));
        }
        else
        {
            (int X, int Y) startCell;
            if (from != null)
            {
                var (dir, cell) = Door(d, from);
                var fp = Plan(d, from);
                pts.Add(L.OfficePoint(from, dir, 0, fp.HallZ));
                pts.Add(L.OfficePoint(from, dir, 0, fp.InsideZ));
                pts.Add(L.OfficePoint(from, dir, 0, fp.HalfZ + 0.9));
                startCell = cell;
            }
            else startCell = L.NearestWalkable(L.CellAt(p.X, p.Y));

            (int X, int Y) endCell;
            (int X, int Y) endDir = (0, 1);
            if (targetOffice != null)
                (endDir, endCell) = Door(d, targetOffice);
            else
                endCell = L.NearestWalkable(L.CellAt(target.X, target.Y));

            var cells = L.WalkPath(startCell, endCell);
            if (cells != null)
            {
                // skip the first cell when we are already standing on it
                for (int i = 0; i < cells.Count; i++)
                {
                    if (i == 0 && from == null && L.CellAt(p.X, p.Y) == cells[0] && cells.Count > 1) continue;
                    pts.Add(L.CellCenter(cells[i].X, cells[i].Y));
                }
            }
            if (targetOffice != null)
            {
                var tp = Plan(d, targetOffice);
                pts.Add(L.OfficePoint(targetOffice, endDir, 0, tp.HalfZ + 0.9));
                pts.Add(L.OfficePoint(targetOffice, endDir, 0, tp.InsideZ));
                pts.Add(L.OfficePoint(targetOffice, endDir, 0, tp.HallZ));
            }
        }
        pts.Add(target);
        p.Path = pts;
        p.PathIdx = 0;
        p.Act = PersonAct.Walking;
        p.Goal = goal;
        p.Target = target;
        p.TargetOffice = targetOffice;
        p.FaceAng = face;
        if (from != targetOffice) p.Office = null;
    }
}
