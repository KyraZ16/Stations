namespace Stations.Core.Map;

/// <summary>A position on the road network: a point on segment Seg at fraction T from SegA to SegB.</summary>
public readonly record struct RoadPos(int Seg, double T, double X, double Y);

/// <summary>A routed polyline. Spd[i] is the speed (m/s) from point i to i+1.</summary>
public class RouteResult
{
    public List<double> Xs = new();
    public List<double> Ys = new();
    public List<double> Spd = new();
    public double Time;
}

public sealed class SegRouteResult : RouteResult
{
    /// <summary>Segments the route runs over (first and last possibly partly).</summary>
    public List<int> Segs = new();
}

/// <summary>
/// Directed street graph built from OSM ways. Every OSM node is a vertex.
/// Segments are the undirected pieces between two consecutive nodes; each has
/// a forward and/or reverse directed edge depending on one-way rules.
/// </summary>
public sealed class RoadGraph
{
    private const double Grid = 60;
    private const double MaxSpeed = 30; // m/s, admissible A* heuristic

    public readonly List<double> Vx = new();
    public readonly List<double> Vy = new();
    private readonly Dictionary<long, int> _vIndex = new();

    public readonly List<int> EFrom = new();
    public readonly List<int> ETo = new();
    public readonly List<double> ECost = new(); // seconds
    public readonly List<double> ESpd = new();
    public readonly List<List<int>> Out = new();

    public readonly List<int> SegA = new();
    public readonly List<int> SegB = new();
    public readonly List<int> SegF = new();
    public readonly List<int> SegR = new();
    public readonly List<double> SegLen = new();
    public readonly List<double> SegSpd = new();
    public readonly List<int> SegWay = new();
    /// <summary>Segments touching each vertex (for direction-free routing, e.g. tram tracks).</summary>
    public readonly List<List<int>> VSegs = new();
    private readonly Dictionary<long, List<int>> _grid = new();

    public int VertexCount => Vx.Count;
    public int SegmentCount => SegA.Count;

    public int Vertex(long osmId, double x, double y)
    {
        if (!_vIndex.TryGetValue(osmId, out int i))
        {
            i = Vx.Count;
            Vx.Add(x);
            Vy.Add(y);
            Out.Add(new List<int>());
            VSegs.Add(new List<int>());
            _vIndex[osmId] = i;
        }
        return i;
    }

    private int AddEdge(int a, int b, double len, double spd)
    {
        int e = EFrom.Count;
        EFrom.Add(a);
        ETo.Add(b);
        ECost.Add(len / spd);
        ESpd.Add(spd);
        Out[a].Add(e);
        return e;
    }

    public void AddSegment(int a, int b, bool fwd, bool bwd, double spd, int way)
    {
        double ax = Vx[a], ay = Vy[a], bx = Vx[b], by = Vy[b];
        double len = Util.Hypot(bx - ax, by - ay);
        if (len < 0.01 || (!fwd && !bwd)) return;
        int s = SegA.Count;
        SegA.Add(a);
        SegB.Add(b);
        SegF.Add(fwd ? AddEdge(a, b, len, spd) : -1);
        SegR.Add(bwd ? AddEdge(b, a, len, spd) : -1);
        SegLen.Add(len);
        SegSpd.Add(spd);
        SegWay.Add(way);
        VSegs[a].Add(s);
        VSegs[b].Add(s);
        int x0 = (int)Math.Floor(Math.Min(ax, bx) / Grid), x1 = (int)Math.Floor(Math.Max(ax, bx) / Grid);
        int y0 = (int)Math.Floor(Math.Min(ay, by) / Grid), y1 = (int)Math.Floor(Math.Max(ay, by) / Grid);
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = y0; cy <= y1; cy++)
        {
            long k = CellKey(cx, cy);
            if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<int>();
            list.Add(s);
        }
    }

    /// <summary>Nearest point on the network within maxDist meters (optionally only allowed segments).</summary>
    public RoadPos? Snap(double x, double y, double maxDist = 40, Func<int, bool>? allow = null)
    {
        int r = (int)Math.Ceiling(maxDist / Grid);
        int cx = (int)Math.Floor(x / Grid), cy = (int)Math.Floor(y / Grid);
        RoadPos? best = null;
        double bestD = maxDist;
        for (int i = cx - r; i <= cx + r; i++)
        for (int j = cy - r; j <= cy + r; j++)
        {
            if (!_grid.TryGetValue(CellKey(i, j), out var list)) continue;
            foreach (int s in list)
            {
                if (allow != null && !allow(s)) continue;
                int a = SegA[s], b = SegB[s];
                var (d, t) = Util.DistToSegment(x, y, Vx[a], Vy[a], Vx[b], Vy[b]);
                if (d < bestD)
                {
                    bestD = d;
                    best = new RoadPos(s, t, Vx[a] + t * (Vx[b] - Vx[a]), Vy[a] + t * (Vy[b] - Vy[a]));
                }
            }
        }
        return best;
    }

    /// <summary>Fastest drivable route between two road positions (A*, respects one-way streets).</summary>
    public RouteResult? Route(RoadPos a, RoadPos b)
    {
        int sa = a.Seg, sb = b.Seg;
        double spdA = SegSpd[sa], lenA = SegLen[sa];
        double spdB = SegSpd[sb], lenB = SegLen[sb];

        if (sa == sb)
        {
            bool fwdOk = SegF[sa] >= 0 && b.T >= a.T;
            bool bwdOk = SegR[sa] >= 0 && b.T <= a.T;
            if (fwdOk || bwdOk)
            {
                double len = Math.Abs(b.T - a.T) * lenA;
                return new RouteResult { Xs = { a.X, b.X }, Ys = { a.Y, b.Y }, Spd = { spdA }, Time = len / spdA };
            }
        }

        int n = Vx.Count;
        var g = new double[n];
        Array.Fill(g, double.PositiveInfinity);
        var prev = new int[n];
        Array.Fill(prev, -1);
        var closed = new bool[n];
        var heap = new MinHeap();
        double H(int v) => Util.Hypot(Vx[v] - b.X, Vy[v] - b.Y) / MaxSpeed;
        void Seed(int v, double cost)
        {
            if (cost < g[v]) { g[v] = cost; prev[v] = -2; heap.Push(cost + H(v), v); }
        }
        if (SegF[sa] >= 0) Seed(SegB[sa], (1 - a.T) * lenA / spdA);
        if (SegR[sa] >= 0) Seed(SegA[sa], a.T * lenA / spdA);

        int endA = SegF[sb] >= 0 ? SegA[sb] : -1; // enter target segment from A going forward
        double extraA = b.T * lenB / spdB;
        int endB = SegR[sb] >= 0 ? SegB[sb] : -1;
        double extraB = (1 - b.T) * lenB / spdB;

        double best = double.PositiveInfinity;
        int bestEnd = -1;
        while (heap.Count > 0)
        {
            int v = heap.Pop();
            if (heap.LastKey >= best) break;
            if (closed[v]) continue;
            closed[v] = true;
            double gv = g[v];
            if (v == endA && gv + extraA < best) { best = gv + extraA; bestEnd = v; }
            if (v == endB && gv + extraB < best) { best = gv + extraB; bestEnd = v; }
            foreach (int e in Out[v])
            {
                int w = ETo[e];
                double ng = gv + ECost[e];
                if (ng < g[w]) { g[w] = ng; prev[w] = e; heap.Push(ng + H(w), w); }
            }
        }
        if (bestEnd < 0) return null;

        var verts = new List<int>();
        var spds = new List<double>();
        for (int v = bestEnd; prev[v] != -2; v = EFrom[prev[v]])
        {
            verts.Add(v);
            spds.Add(ESpd[prev[v]]);
        }
        // the start vertex (prev == -2) is the last one reached by the loop
        int start = bestEnd;
        while (prev[start] != -2) start = EFrom[prev[start]];
        verts.Add(start);
        verts.Reverse();
        spds.Reverse();

        var res = new RouteResult { Time = best };
        res.Xs.Add(a.X); res.Ys.Add(a.Y); res.Spd.Add(spdA);
        foreach (int vv in verts) { res.Xs.Add(Vx[vv]); res.Ys.Add(Vy[vv]); }
        res.Spd.AddRange(spds);
        res.Xs.Add(b.X); res.Ys.Add(b.Y); res.Spd.Add(spdB);
        return res;
    }

    /// <summary>
    /// Shortest route that may use segments in both directions (tram tracks, track planning).
    /// Only segments passing <paramref name="allow"/> are used; <paramref name="speed"/> gives m/s per segment.
    /// </summary>
    public SegRouteResult? RouteSegs(RoadPos a, RoadPos b, Func<int, bool> allow, Func<int, double> speed)
    {
        int sa = a.Seg, sb = b.Seg;
        if (!allow(sa) || !allow(sb)) return null;
        if (sa == sb)
        {
            double len = Math.Abs(b.T - a.T) * SegLen[sa];
            return new SegRouteResult
            {
                Xs = { a.X, b.X }, Ys = { a.Y, b.Y }, Spd = { speed(sa) }, Time = len / speed(sa), Segs = { sa },
            };
        }
        int n = Vx.Count;
        var g = new double[n];
        Array.Fill(g, double.PositiveInfinity);
        var prevSeg = new int[n];
        Array.Fill(prevSeg, -1);
        var closed = new bool[n];
        var heap = new MinHeap();
        double H(int v) => Util.Hypot(Vx[v] - b.X, Vy[v] - b.Y) / MaxSpeed;
        void Seed(int v, double cost)
        {
            if (cost < g[v]) { g[v] = cost; prevSeg[v] = -2; heap.Push(cost + H(v), v); }
        }
        double la = SegLen[sa], lb = SegLen[sb];
        Seed(SegB[sa], (1 - a.T) * la / speed(sa));
        Seed(SegA[sa], a.T * la / speed(sa));
        int endA = SegA[sb], endB = SegB[sb];
        double extraA = b.T * lb / speed(sb), extraB = (1 - b.T) * lb / speed(sb);

        double best = double.PositiveInfinity;
        int bestEnd = -1;
        while (heap.Count > 0)
        {
            int v = heap.Pop();
            if (heap.LastKey >= best) break;
            if (closed[v]) continue;
            closed[v] = true;
            double gv = g[v];
            if (v == endA && gv + extraA < best) { best = gv + extraA; bestEnd = v; }
            if (v == endB && gv + extraB < best) { best = gv + extraB; bestEnd = v; }
            foreach (int s in VSegs[v])
            {
                if (!allow(s)) continue;
                int w = SegA[s] == v ? SegB[s] : SegA[s];
                double ng = gv + SegLen[s] / speed(s);
                if (ng < g[w]) { g[w] = ng; prevSeg[w] = s; heap.Push(ng + H(w), w); }
            }
        }
        if (bestEnd < 0) return null;

        var verts = new List<int>();
        var segs = new List<int>();
        int cur = bestEnd;
        while (prevSeg[cur] != -2)
        {
            int s = prevSeg[cur];
            verts.Add(cur);
            segs.Add(s);
            cur = SegA[s] == cur ? SegB[s] : SegA[s];
        }
        verts.Add(cur);
        verts.Reverse();
        segs.Reverse();
        var res = new SegRouteResult { Time = best };
        res.Xs.Add(a.X); res.Ys.Add(a.Y); res.Spd.Add(speed(sa));
        foreach (int vv in verts) { res.Xs.Add(Vx[vv]); res.Ys.Add(Vy[vv]); }
        foreach (int s in segs) res.Spd.Add(speed(s));
        res.Xs.Add(b.X); res.Ys.Add(b.Y); res.Spd.Add(speed(sb));
        res.Segs.Add(sa);
        res.Segs.AddRange(segs);
        res.Segs.Add(sb);
        return res;
    }

    private static long CellKey(int cx, int cy) => ((long)(cx + 100000) << 32) | (uint)(cy + 100000);
}
