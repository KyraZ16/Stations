namespace Stations.Core.Sim;

/// <summary>
/// Interior of an office in local meters: x across, z toward the door (door in the middle of the +z wall).
/// Left half: break room with two tables; back right: lockers; front right: dispatch desk.
/// The 3D view builds its furniture from the same numbers.
/// </summary>
public static class OfficePlan
{
    /// <summary>Half the inner size (office is 3×3 cells minus the wall margin).</summary>
    public const double Half = 4.1;
    public const double WallHeight = 3.2;
    /// <summary>The partition between break room (x &lt; 0) and the rest ends here (z), leaving a hallway at the door.</summary>
    public const double PartitionEnd = 1.0;

    public static readonly (double X, double Z)[] Tables = { (-2.2, -2.3), (-2.2, 0.4) };

    /// <summary>Chairs: four around each table, facing it.</summary>
    public static readonly (double X, double Z, double FaceX, double FaceZ)[] Seats = Tables
        .SelectMany(t => new[]
        {
            (t.X - 0.45, t.Z - 0.8, 0.0, 1.0), (t.X + 0.45, t.Z - 0.8, 0.0, 1.0),
            (t.X - 0.45, t.Z + 0.8, 0.0, -1.0), (t.X + 0.45, t.Z + 0.8, 0.0, -1.0),
        }).ToArray();

    /// <summary>Lockers along the back wall (x positions); people stand in front of them.</summary>
    public static readonly double[] Lockers = { 0.9, 1.5, 2.1, 2.7, 3.3 };
    public const double LockerZ = -3.8;
    public const double LockerStandZ = -3.0;

    public static readonly (double X, double Z) Desk = (2.6, 1.6);
    public static readonly (double X, double Z) Kitchen = (-3.7, 2.7);

    /// <summary>Waypoints: just inside the door, in the hallway toward each half.</summary>
    public const double InsideZ = 3.3;
    public const double HallZ = 2.2;
}

public sealed partial class DepotLayout
{
    /// <summary>Cells people can walk on: empty ground, driveways, parking spots, open halls.</summary>
    public bool Walkable(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        var m = ModuleAt(x, y);
        return m == null || m.Type is not (ModuleType.Office or ModuleType.Charger);
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
        var (x, y, ang) = Pose(m);
        var c = CellCenter(m.X + (m.W - 1) / 2.0, m.Y + (m.H - 1) / 2.0);
        double ax = Math.Cos(ang), ay = Math.Sin(ang);
        bool vertical = m.H >= m.W;
        double len = (vertical ? m.H : m.W) * CellSize / 2, wid = (vertical ? m.W : m.H) * CellSize / 2;
        return (c.X, c.Y, ax, ay, -ay, ax, len, wid);
    }

    /// <summary>Where the mechanic of a workshop lane stands while repairing (beside the vehicle, toward the hall's middle).</summary>
    public (double X, double Y, double Ang) WorkshopWorkPoint(PlacedModule m, int bay)
    {
        var (lx, ly, _) = Pose(m, bay);
        var f = Frame(m);
        double side = bay == 0 ? 1 : -1; // lane 0 lies on the -across side
        double along = bay == 0 ? -1.5 : 1.5;
        return (lx + f.Cx * side * 1.8 + f.Ax * along, ly + f.Cy * side * 1.8 + f.Ay * along, Math.Atan2(-f.Cy * side, -f.Cx * side));
    }

    /// <summary>
    /// Where an idle mechanic waits: lane 0 at the workbench on the hall's closed side,
    /// lane 1 at the tool trolley on the open side (both just outside their lane).
    /// </summary>
    public (double X, double Y, double Ang) WorkshopBench(PlacedModule m, int bay)
    {
        var f = Frame(m);
        double side = bay == 0 ? -1 : 1;
        double t = -0.3 * 2 * (f.HalfLen - 0.25);
        double out_ = bay == 0 ? f.HalfWidth - 2.0 : f.HalfWidth - 1.3;
        return (f.X + f.Cx * side * out_ + f.Ax * t, f.Y + f.Cy * side * out_ + f.Ay * t, Math.Atan2(f.Cy * side, f.Cx * side));
    }

    /// <summary>Tool trolley next to lane 1 (the 3D view draws it here).</summary>
    public (double X, double Y) WorkshopTrolley(PlacedModule m)
    {
        var f = Frame(m);
        double t = -0.3 * 2 * (f.HalfLen - 0.25) + 0.9;
        double out_ = f.HalfWidth - 0.75;
        return (f.X + f.Cx * out_ + f.Ax * t, f.Y + f.Cy * out_ + f.Ay * t);
    }

    /// <summary>Control desk at the entry end of a wash bay.</summary>
    public (double X, double Y, double Ang) WashPost(PlacedModule m)
    {
        var f = Frame(m);
        // vehicles drive in heading +along from the -along end
        double bx = f.X - f.Ax * (f.HalfLen - 1.2) + f.Cx * (f.HalfWidth - 0.7);
        double by = f.Y - f.Ay * (f.HalfLen - 1.2) + f.Cy * (f.HalfWidth - 0.7);
        return (bx, by, Math.Atan2(-f.Cy, -f.Cx));
    }
}
