namespace Stations.Core.Sim;

/// <summary>
/// Interior of an office in local meters: x across, z toward the door (door in the middle of the +z wall).
/// Generated from the inner size: left of the partition a break room with as many tables as fit,
/// lockers along the back wall on the right, a dispatch desk at the front right (if there is room)
/// and a kitchen corner by the door. The 3D view builds its furniture from the same plan.
/// </summary>
public sealed class OfficePlan
{
    public const double WallHeight = 3.2;
    /// <summary>Wall margin: the inner half size is half the cells minus this.</summary>
    public const double Margin = 0.4;

    public double HalfX, HalfZ;
    /// <summary>The partition between break room (x &lt; 0) and the rest ends here (z), leaving a hallway at the door.</summary>
    public double PartitionEnd;
    public readonly List<(double X, double Z)> Tables = new();
    /// <summary>Chairs: four around each table, facing it.</summary>
    public readonly List<(double X, double Z, double FaceX, double FaceZ)> Seats = new();
    /// <summary>Lockers along the back wall (x positions); people stand in front of them.</summary>
    public readonly List<double> Lockers = new();
    public double LockerZ, LockerStandZ;
    public (double X, double Z)? Desk;
    public (double X, double Z) Kitchen;
    /// <summary>Places to stand when all chairs are taken.</summary>
    public readonly List<(double X, double Z)> Standing = new();
    /// <summary>Waypoints: just inside the door, in the hallway.</summary>
    public double InsideZ => HalfZ - 0.8;
    public double HallZ => Math.Max(HalfZ - 1.9, -HalfZ + 0.6);

    public static OfficePlan Create(double halfX, double halfZ)
    {
        var p = new OfficePlan { HalfX = halfX, HalfZ = halfZ };
        p.PartitionEnd = Math.Max(halfZ - 3.1, -halfZ + 1.0);

        // Break room: tables in a grid (each table with its chairs needs about 2.0 × 2.4 m)
        double x0 = -halfX + 0.3, x1 = -0.5, z0 = -halfZ + 0.3, z1 = p.PartitionEnd;
        int nx = Math.Max(1, (int)((x1 - x0) / 2.0)), nz = Math.Max(1, (int)((z1 - z0) / 2.4));
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < nz; j++)
        {
            double tx = x0 + (x1 - x0) * (i + 0.5) / nx, tz = z0 + (z1 - z0) * (j + 0.5) / nz;
            p.Tables.Add((tx, tz));
            p.Seats.Add((tx - 0.45, tz - 0.8, 0, 1));
            p.Seats.Add((tx + 0.45, tz - 0.8, 0, 1));
            p.Seats.Add((tx - 0.45, tz + 0.8, 0, -1));
            p.Seats.Add((tx + 0.45, tz + 0.8, 0, -1));
        }

        // Lockers on the right half of the back wall
        p.LockerZ = -halfZ + 0.3;
        p.LockerStandZ = -halfZ + 1.1;
        for (double x = 0.9; x <= halfX - 0.35; x += 0.6) p.Lockers.Add(x);
        if (p.Lockers.Count == 0) p.Lockers.Add(Math.Min(0.9, halfX - 0.35));

        if (halfX >= 3.0 && halfZ >= 2.6) p.Desk = (halfX - 1.5, halfZ - 2.5);
        p.Kitchen = (-halfX + 0.4, halfZ - 1.4);

        // Standing room: by the kitchen and the hallway, and in front of the lockers
        foreach (var (x, z) in new[]
        {
            (-halfX + 1.1, halfZ - 1.1), (-halfX + 1.9, halfZ - 0.8), (-1.2, halfZ - 1.1),
            (1.2, p.LockerStandZ + 0.9), (halfX - 1.0, p.LockerStandZ + 0.9), (1.0, p.PartitionEnd - 0.5),
        })
            p.Standing.Add((Math.Clamp(x, -halfX + 0.4, halfX - 0.4), Math.Clamp(z, -halfZ + 0.4, halfZ - 0.4)));
        return p;
    }
}

/// <summary>
/// Interior of a customer center in local meters (door in the middle of the +z wall): counters along the
/// back wall with the service desk behind them, rows of waiting chairs facing the counters, a ticket machine.
/// </summary>
public sealed class ServicePlan
{
    public double HalfX, HalfZ;
    /// <summary>Counter positions (x); staff stand at StaffZ, customers at CustomerZ.</summary>
    public readonly List<double> Counters = new();
    public double CounterZ, StaffZ, CustomerZ;
    public readonly List<(double X, double Z)> Seats = new();
    /// <summary>Standing room in the queue when all chairs are taken.</summary>
    public readonly List<(double X, double Z)> Standing = new();
    public (double X, double Z) Machine;

    public static ServicePlan Create(double halfX, double halfZ)
    {
        var p = new ServicePlan { HalfX = halfX, HalfZ = halfZ };
        double span = 2 * halfX - 0.6;
        int n = Math.Clamp((int)(span / 3.0), 1, 8);
        for (int i = 0; i < n; i++) p.Counters.Add(-halfX + 0.3 + span * (i + 0.5) / n);
        p.StaffZ = -halfZ + 0.55;
        p.CounterZ = -halfZ + 1.05;
        p.CustomerZ = -halfZ + 1.65;
        for (double z = p.CustomerZ + 1.4; z <= halfZ - 1.5; z += 1.1)
            for (double x = -halfX + 0.6; x <= halfX - 0.6; x += 0.7)
                if (Math.Abs(x) > 0.8) p.Seats.Add((x, z));
        // About six chairs per counter, the rows nearest to the counters first
        int keep = Math.Max(6, p.Counters.Count * 6);
        var nearest = p.Seats.OrderBy(q => q.Z).ThenBy(q => Math.Abs(q.X)).Take(keep).ToList();
        p.Seats.Clear();
        p.Seats.AddRange(nearest);
        p.Machine = (halfX - 0.45, halfZ - 1.3);
        for (double x = -halfX + 0.8; x <= halfX - 1.2; x += 0.9)
            if (Math.Abs(x) > 0.9) p.Standing.Add((x, halfZ - 0.8));
        if (p.Standing.Count == 0) p.Standing.Add((0, halfZ - 1.0));
        return p;
    }
}

/// <summary>
/// Interior of a planning office in local meters (door in the middle of the +z wall): desks in two blocks left and
/// right of the middle aisle, each with a chair on the door side; a whiteboard on the back wall.
/// </summary>
public sealed class DeskPlan
{
    public double HalfX, HalfZ;
    /// <summary>Desk centers; the chair is ChairOffset toward the door, the researcher faces the back wall.</summary>
    public readonly List<(double X, double Z)> Desks = new();
    public const double ChairOffset = 0.75;

    public static DeskPlan Create(double halfX, double halfZ)
    {
        var p = new DeskPlan { HalfX = halfX, HalfZ = halfZ };
        for (double z = -halfZ + 1.2; z <= halfZ - 2.0; z += 2.4)
            for (double x = 1.2; x <= halfX - 0.85; x += 2.0)
            {
                p.Desks.Add((-x, z));
                p.Desks.Add((x, z));
            }
        if (p.Desks.Count == 0) p.Desks.Add((Math.Min(1.0, halfX - 0.8), -halfZ + 1.0));
        return p;
    }
}

public sealed partial class DepotLayout
{
    /// <summary>Cells people can walk on: empty ground, driveways, parking spots, open halls.</summary>
    public bool Walkable(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        var m = ModuleAt(x, y);
        return m == null || m.Type is not (ModuleType.Office or ModuleType.Charger or ModuleType.ServiceCenter or ModuleType.PlanningOffice);
    }

    /// <summary>Closest walkable cell (breadth-first from the cell itself).</summary>
    public (int X, int Y) NearestWalkable((int X, int Y) c)
    {
        c = (Math.Clamp(c.X, 0, Width - 1), Math.Clamp(c.Y, 0, Height - 1));
        if (Walkable(c.X, c.Y)) return c;
        var seen = new HashSet<(int, int)> { c };
        var q = new Queue<(int X, int Y)>();
        q.Enqueue(c);
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var n in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= Width || n.Item2 >= Height || !seen.Add(n)) continue;
                if (Walkable(n.Item1, n.Item2)) return n;
                q.Enqueue(n);
            }
        }
        return c;
    }

    /// <summary>Shortest walk over walkable cells (8 directions, no corner cutting), or null.</summary>
    public List<(int X, int Y)>? WalkPath((int X, int Y) from, (int X, int Y) to)
    {
        if (!Walkable(from.X, from.Y) || !Walkable(to.X, to.Y)) return null;
        var dist = new Dictionary<(int, int), double> { [from] = 0 };
        var prev = new Dictionary<(int, int), (int, int)>();
        var pq = new PriorityQueue<(int X, int Y), double>();
        pq.Enqueue(from, 0);
        while (pq.TryDequeue(out var cur, out double d))
        {
            if (cur == to) break;
            if (d > dist[cur]) continue;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cur.X + dx, ny = cur.Y + dy;
                if (!Walkable(nx, ny)) continue;
                if (dx != 0 && dy != 0 && (!Walkable(cur.X + dx, cur.Y) || !Walkable(cur.X, cur.Y + dy))) continue;
                double nd = d + (dx != 0 && dy != 0 ? 1.414 : 1);
                if (dist.TryGetValue((nx, ny), out double old) && old <= nd) continue;
                dist[(nx, ny)] = nd;
                prev[(nx, ny)] = cur;
                pq.Enqueue((nx, ny), nd);
            }
        }
        if (!dist.ContainsKey(to)) return null;
        var path = new List<(int X, int Y)> { to };
        while (path[^1] != from) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    // ---------------------------------------------------------------- pedestrian entrance

    /// <summary>Where staff arrive from and leave to: the pavement right of the gate.</summary>
    public (double X, double Y) PedestrianStart() => (CellSize * 2.5, Height * CellSize / 2 + 3.5);

    /// <summary>The cell people use to come in (next to the gate).</summary>
    public (int X, int Y) PedestrianGate() => NearestWalkable((Width / 2, Height - 1));

    // ---------------------------------------------------------------- offices

    /// <summary>
    /// Door side of an office: the side whose middle cell is a driveway connected to the gate,
    /// else any walkable middle cell. Returns the outward direction and the cell in front of the door.
    /// </summary>
    public ((int X, int Y) Dir, (int X, int Y) Cell) OfficeDoor(PlacedModule m, HashSet<(int, int)>? reach = null)
    {
        reach ??= Reachable();
        int mx = m.X + m.W / 2, my = m.Y + m.H / 2;
        var sides = new (int, int)[] { (0, 1), (1, 0), (-1, 0), (0, -1) }; // bottom (toward the gate) first
        (int, int) Front((int X, int Y) d) => d.X != 0 ? (d.X > 0 ? m.X + m.W : m.X - 1, my) : (mx, d.Y > 0 ? m.Y + m.H : m.Y - 1);
        foreach (var d in sides)
            if (reach.Contains(Front(d))) return (d, Front(d));
        foreach (var d in sides)
        {
            var f = Front(d);
            if (Walkable(f.Item1, f.Item2)) return (d, f);
        }
        return ((0, 1), NearestWalkable(Front((0, 1))));
    }

    /// <summary>Interior plan of an office whose door faces the given way (the local frame turns with the door).</summary>
    public static OfficePlan OfficePlanFor(PlacedModule m, (int X, int Y) door)
    {
        double hw = m.W * CellSize / 2 - OfficePlan.Margin, hh = m.H * CellSize / 2 - OfficePlan.Margin;
        return door.X != 0 ? OfficePlan.Create(hh, hw) : OfficePlan.Create(hw, hh);
    }

    /// <summary>Interior of a planning office whose door faces the given way.</summary>
    public static DeskPlan DeskPlanFor(PlacedModule m, (int X, int Y) door)
    {
        double hw = m.W * CellSize / 2 - OfficePlan.Margin, hh = m.H * CellSize / 2 - OfficePlan.Margin;
        return door.X != 0 ? DeskPlan.Create(hh, hw) : DeskPlan.Create(hw, hh);
    }

    /// <summary>Interior of a customer center whose door faces the given way.</summary>
    public static ServicePlan ServicePlanFor(PlacedModule m, (int X, int Y) door)
    {
        double hw = m.W * CellSize / 2 - OfficePlan.Margin, hh = m.H * CellSize / 2 - OfficePlan.Margin;
        return door.X != 0 ? ServicePlan.Create(hh, hw) : ServicePlan.Create(hw, hh);
    }

    /// <summary>Office-local point (x across, z toward the door) in yard meters.</summary>
    public (double X, double Y) OfficePoint(PlacedModule m, (int X, int Y) door, double lx, double lz)
    {
        var c = CellCenter(m.X + (m.W - 1) / 2.0, m.Y + (m.H - 1) / 2.0);
        double fx = door.X, fy = door.Y, rx = fy, ry = -fx;
        return (c.X + lx * rx + lz * fx, c.Y + lx * ry + lz * fy);
    }

    /// <summary>Heading (yard angle) of an office-local direction.</summary>
    public static double OfficeAngle((int X, int Y) door, double dx, double dz)
    {
        double fx = door.X, fy = door.Y, rx = fy, ry = -fx;
        return Math.Atan2(dx * ry + dz * fy, dx * rx + dz * fx);
    }

    // ---------------------------------------------------------------- halls

    /// <summary>Center and unit vectors along the vehicle lane and across it.</summary>
    private (double X, double Y, double Ax, double Ay, double Cx, double Cy, double HalfLen, double HalfWidth) Frame(PlacedModule m)
    {
        var (_, _, ang) = Pose(m);
        var c = CellCenter(m.X + (m.W - 1) / 2.0, m.Y + (m.H - 1) / 2.0);
        double ax = Math.Cos(ang), ay = Math.Sin(ang);
        bool vertical = m.H >= m.W;
        double len = (vertical ? m.H : m.W) * CellSize / 2, wid = (vertical ? m.W : m.H) * CellSize / 2;
        return (c.X, c.Y, ax, ay, -ay, ax, len, wid);
    }

    /// <summary>Point in a hall: across = offset from the middle line, along = offset along the lanes.</summary>
    private (double X, double Y) HallPoint(PlacedModule m, double across, double along)
    {
        var f = Frame(m);
        return (f.X + f.Cx * across + f.Ax * along, f.Y + f.Cy * across + f.Ay * along);
    }

    /// <summary>Where the mechanic of a workshop lane stands while repairing: in the gap beside the vehicle.</summary>
    public (double X, double Y, double Ang) WorkshopWorkPoint(PlacedModule m, int bay)
    {
        var f = Frame(m);
        var (x, y) = HallPoint(m, LaneOffset(m, bay) + 1.85, bay % 2 == 0 ? -1.5 : 1.5);
        return (x, y, Math.Atan2(-f.Cy, -f.Cx)); // facing the vehicle
    }

    /// <summary>Where an idle mechanic waits: by the lane's tool trolley at the far end of the hall.</summary>
    public (double X, double Y, double Ang) WorkshopBench(PlacedModule m, int bay)
    {
        var f = Frame(m);
        var (x, y) = HallPoint(m, LaneOffset(m, bay) + 1.85, f.HalfLen - 2.0);
        return (x, y, Math.Atan2(f.Ay, f.Ax)); // facing the trolley
    }

    /// <summary>Tool trolley of a workshop lane (the 3D view draws it here).</summary>
    public (double X, double Y) WorkshopTrolley(PlacedModule m, int bay) =>
        HallPoint(m, LaneOffset(m, bay) + 1.85, Frame(m).HalfLen - 1.2);

    /// <summary>Control desk at the entry end of a wash lane.</summary>
    public (double X, double Y, double Ang) WashPost(PlacedModule m, int bay = 0)
    {
        var f = Frame(m);
        // vehicles drive in heading +along from the -along end
        var (x, y) = HallPoint(m, LaneOffset(m, bay) + 2.3, -(f.HalfLen - 1.2));
        return (x, y, Math.Atan2(-f.Cy, -f.Cx));
    }
}
