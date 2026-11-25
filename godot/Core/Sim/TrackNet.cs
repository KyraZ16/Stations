using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>
/// A rail network of points and straight pieces: U-Bahn tunnels (all built by the player) or the S-Bahn
/// network (the real railway lines from OSM plus pieces the player adds). Pieces that only cross without
/// a shared point are not connected. The routing graph is rebuilt after every change.
/// </summary>
public sealed class TrackNet
{
    public readonly List<(double X, double Y)> Nodes = new();
    /// <summary>Pieces between two points; Built = laid by the player (only those can be removed).</summary>
    public readonly List<(int A, int B, bool Built)> Edges = new();
    public RoadGraph Graph { get; private set; } = new();
    public int Version { get; private set; }
    public readonly double Speed;
    private readonly Dictionary<(long, long), int> _key = new();
    /// <summary>Crossovers between neighbouring parallel tracks (not drawn, not removable).</summary>
    public readonly List<(int A, int B)> Links = new();
    /// <summary>Segments below this index are tracks; the rest of the graph are crossovers.</summary>
    public int TrackSegs { get; private set; }
    private readonly List<int> _baseLevel = new(); // level (tunnel -1, ground 0) of the base pieces while building

    public TrackNet(double speed) => Speed = speed;

    public double Length(bool builtOnly = false) =>
        Edges.Where(e => !builtOnly || e.Built).Sum(e => Dist(Nodes[e.A], Nodes[e.B]));

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Util.Hypot(a.X - b.X, a.Y - b.Y);

    public void Rebuild()
    {
        var g = new RoadGraph();
        for (int i = 0; i < Nodes.Count; i++) g.Vertex(i, Nodes[i].X, Nodes[i].Y);
        foreach (var (a, b, _) in Edges) g.AddSegment(a, b, true, true, Speed, -1);
        TrackSegs = g.SegmentCount;
        foreach (var (a, b) in Links) g.AddSegment(a, b, true, true, Speed * 0.5, -1);
        Graph = g;
        Version++;
    }

    /// <summary>Add an existing line (OSM railway) as unremovable pieces; shared points are joined.</summary>
    public void AddBaseLine(double[] pts, int level = 0)
    {
        int prev = -1;
        for (int i = 0; i + 1 < pts.Length; i += 2)
        {
            var key = ((long)Math.Round(pts[i] * 10), (long)Math.Round(pts[i + 1] * 10));
            if (!_key.TryGetValue(key, out int n))
            {
                n = Nodes.Count;
                Nodes.Add((pts[i], pts[i + 1]));
                _key[key] = n;
            }
            if (prev >= 0 && prev != n && Dist(Nodes[prev], Nodes[n]) > 0.05)
            {
                Edges.Add((prev, n, false));
                _baseLevel.Add(level);
            }
            prev = n;
        }
    }

    /// <summary>Nearest point on a track (never on a crossover) within maxDist.</summary>
    public RoadPos? Snap(double x, double y, double maxDist)
    {
        int n = TrackSegs;
        return Graph.Snap(x, y, maxDist, s => s < n);
    }

    /// <summary>
    /// Railway data rarely has every switch: parallel tracks (station throats, multi-track lines) are often
    /// separate ways. To let trains change between them, neighbouring parallel tracks on the same level get
    /// crossovers about every <paramref name="step"/> meters. Call once after the base lines are added.
    /// Returns the number of crossovers.
    /// </summary>
    public int AddCrossovers(double step = 60, double maxGap = 20, double maxAngleDeg = 15)
    {
        // 1. split long pieces so crossovers can sit anywhere along a track
        var edges = new List<(int A, int B, bool Built)>();
        var level = new List<int>();
        for (int e = 0; e < Edges.Count; e++)
        {
            var (a, b, built) = Edges[e];
            int lv = e < _baseLevel.Count ? _baseLevel[e] : 0;
            var pa = Nodes[a];
            var pb = Nodes[b];
            int parts = Math.Max(1, (int)Math.Ceiling(Dist(pa, pb) / step));
            int prev = a;
            for (int k = 1; k < parts; k++)
            {
                double t = (double)k / parts;
                Nodes.Add((pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t));
                edges.Add((prev, Nodes.Count - 1, built));
                level.Add(lv);
                prev = Nodes.Count - 1;
            }
            edges.Add((prev, b, built));
            level.Add(lv);
        }
        Edges.Clear();
        Edges.AddRange(edges);
        _baseLevel.Clear();

        // 2. pieces per grid cell, direction and level per point
        const double cell = 50;
        var grid = new Dictionary<(int, int), List<int>>();
        var dir = new (double X, double Y)[Nodes.Count];
        var nodeLevel = new int[Nodes.Count];
        var used = new bool[Nodes.Count];
        for (int e = 0; e < Edges.Count; e++)
        {
            var (a, b, _) = Edges[e];
            var pa = Nodes[a];
            var pb = Nodes[b];
            double len = Math.Max(0.01, Dist(pa, pb));
            var d = ((pb.X - pa.X) / len, (pb.Y - pa.Y) / len);
            foreach (int n in new[] { a, b })
                if (!used[n]) { used[n] = true; dir[n] = d; nodeLevel[n] = level[e]; }
            int x0 = (int)Math.Floor((Math.Min(pa.X, pb.X) - maxGap) / cell), x1 = (int)Math.Floor((Math.Max(pa.X, pb.X) + maxGap) / cell);
            int y0 = (int)Math.Floor((Math.Min(pa.Y, pb.Y) - maxGap) / cell), y1 = (int)Math.Floor((Math.Max(pa.Y, pb.Y) + maxGap) / cell);
            for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                if (!grid.TryGetValue((cx, cy), out var list)) grid[(cx, cy)] = list = new List<int>();
                list.Add(e);
            }
        }

        // 3. from every point: the nearest parallel track on the left and on the right
        double minCos = Math.Cos(maxAngleDeg * Math.PI / 180);
        var splits = new Dictionary<int, List<(double T, int From)>>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (!used[i]) continue;
            var p = Nodes[i];
            var d = dir[i];
            if (!grid.TryGetValue(((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell)), out var cands)) continue;
            int bestL = -1, bestR = -1;
            double dL = maxGap, dR = maxGap, tL = 0, tR = 0;
            foreach (int e in cands)
            {
                var (a, b, _) = Edges[e];
                if (a == i || b == i || level[e] != nodeLevel[i]) continue;
                var pa = Nodes[a];
                var pb = Nodes[b];
                double len = Dist(pa, pb);
                if (len < 0.5) continue;
                double cos = Math.Abs(((pb.X - pa.X) * d.X + (pb.Y - pa.Y) * d.Y) / len);
                if (cos < minCos) continue;
                var (dist, t) = Util.DistToSegment(p.X, p.Y, pa.X, pa.Y, pb.X, pb.Y);
                if (dist < 1.5 || dist >= maxGap) continue;
                double qx = pa.X + (pb.X - pa.X) * t - p.X, qy = pa.Y + (pb.Y - pa.Y) * t - p.Y;
                // the projection must be (nearly) square to the track, else it is just a track end nearby
                if (Math.Abs(qx * d.X + qy * d.Y) > dist * 0.5) continue;
                bool left = d.X * qy - d.Y * qx > 0;
                if (left && dist < dL) { dL = dist; bestL = e; tL = t; }
                if (!left && dist < dR) { dR = dist; bestR = e; tR = t; }
            }
            void Link(int e, double t, double gap)
            {
                // a crossover runs diagonally like a real one: it lands a few gaps further along the other track
                var (a, b, _) = Edges[e];
                var pa = Nodes[a];
                var pb = Nodes[b];
                double qx = pa.X + (pb.X - pa.X) * t + d.X * gap * 4, qy = pa.Y + (pb.Y - pa.Y) * t + d.Y * gap * 4;
                int best = e;
                double bt = t, bd = Math.Min(3, gap * 0.5);
                if (grid.TryGetValue(((int)Math.Floor(qx / cell), (int)Math.Floor(qy / cell)), out var near))
                    foreach (int o in near)
                    {
                        var (oa, ob, _) = Edges[o];
                        if (level[o] != nodeLevel[i] || oa == i || ob == i) continue;
                        var (od, ot) = Util.DistToSegment(qx, qy, Nodes[oa].X, Nodes[oa].Y, Nodes[ob].X, Nodes[ob].Y);
                        if (od < bd) { bd = od; best = o; bt = ot; }
                    }
                (splits.TryGetValue(best, out var sl) ? sl : splits[best] = new()).Add((bt, i));
            }
            if (bestL >= 0) Link(bestL, tL, dL);
            if (bestR >= 0) Link(bestR, tR, dR);
        }

        // 4. split the target pieces at the crossover points and link
        var links = new HashSet<(int, int)>();
        var extra = new List<(int A, int B, bool Built)>();
        foreach (var (e, list) in splits)
        {
            var (a, b, built) = Edges[e];
            var pa = Nodes[a];
            var pb = Nodes[b];
            double len = Dist(pa, pb);
            int prev = a;
            double prevT = 0;
            foreach (var (t, from) in list.OrderBy(s => s.T))
            {
                int at;
                if (t * len < 2) at = a;
                else if ((1 - t) * len < 2) at = b;
                else if ((t - prevT) * len < 2) at = prev;
                else
                {
                    Nodes.Add((pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t));
                    at = Nodes.Count - 1;
                    if (prev == a) Edges[e] = (a, at, built); else extra.Add((prev, at, built));
                    prev = at;
                    prevT = t;
                }
                if (at != from) links.Add(at < from ? (at, from) : (from, at));
            }
            if (prev != a) extra.Add((prev, b, built));
        }
        Edges.AddRange(extra);
        Links.AddRange(links);
        Rebuild();
        return links.Count;
    }

    /// <summary>Snap to an existing point or piece within r, else keep the point.</summary>
    public (double X, double Y) SnapPoint(double x, double y, double r)
    {
        int best = -1;
        double bd = r;
        for (int i = 0; i < Nodes.Count; i++)
        {
            double d = Util.Hypot(Nodes[i].X - x, Nodes[i].Y - y);
            if (d < bd) { bd = d; best = i; }
        }
        if (best >= 0) return Nodes[best];
        if (Snap(x, y, r) is RoadPos p) return (p.X, p.Y);
        return (x, y);
    }

    public bool HasPiece((double X, double Y) a, (double X, double Y) b) => Edges.Any(e =>
        (Dist(Nodes[e.A], a) < 1 && Dist(Nodes[e.B], b) < 1) || (Dist(Nodes[e.A], b) < 1 && Dist(Nodes[e.B], a) < 1));

    /// <summary>Lay a straight piece from a to b; ends on existing pieces get connected (the piece there is split).</summary>
    public void AddPiece((double X, double Y) a, (double X, double Y) b)
    {
        int ia = NodeFor(a), ib = NodeFor(b);
        if (ia != ib) Edges.Add((ia, ib, true));
        Rebuild();
    }

    private int NodeFor((double X, double Y) p)
    {
        for (int i = 0; i < Nodes.Count; i++)
            if (Dist(Nodes[i], p) < 1) return i;
        for (int e = 0; e < Edges.Count; e++)
        {
            var (a, b, built) = Edges[e];
            var (d, t) = Util.DistToSegment(p.X, p.Y, Nodes[a].X, Nodes[a].Y, Nodes[b].X, Nodes[b].Y);
            if (d > 1 || t <= 0.001 || t >= 0.999) continue;
            int k = Nodes.Count;
            Nodes.Add(p);
            Edges[e] = (a, k, built);
            Edges.Add((k, b, built));
            return k;
        }
        Nodes.Add(p);
        return Nodes.Count - 1;
    }

    /// <summary>Remove a piece by index and drop points nothing uses any more.</summary>
    public void RemovePiece(int e)
    {
        Edges.RemoveAt(e);
        var used = Edges.SelectMany(t => new[] { t.A, t.B }).ToHashSet();
        Links.RemoveAll(l => !used.Contains(l.A) || !used.Contains(l.B));
        var map = new int[Nodes.Count];
        var nodes = new List<(double, double)>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            map[i] = used.Contains(i) ? nodes.Count : -1;
            if (used.Contains(i)) nodes.Add(Nodes[i]);
        }
        var edges = Edges.Select(t => (map[t.A], map[t.B], t.Built)).ToList();
        Nodes.Clear();
        Nodes.AddRange(nodes);
        Edges.Clear();
        Edges.AddRange(edges);
        var links = Links.Select(l => (map[l.A], map[l.B])).ToList();
        Links.Clear();
        Links.AddRange(links);
        _key.Clear();
        Rebuild();
    }
}
