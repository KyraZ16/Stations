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

    public TrackNet(double speed) => Speed = speed;

    public double Length(bool builtOnly = false) =>
        Edges.Where(e => !builtOnly || e.Built).Sum(e => Dist(Nodes[e.A], Nodes[e.B]));

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Util.Hypot(a.X - b.X, a.Y - b.Y);

    public void Rebuild()
    {
        var g = new RoadGraph();
        for (int i = 0; i < Nodes.Count; i++) g.Vertex(i, Nodes[i].X, Nodes[i].Y);
        foreach (var (a, b, _) in Edges) g.AddSegment(a, b, true, true, Speed, -1);
        Graph = g;
        Version++;
    }

    /// <summary>Add an existing line (OSM railway) as unremovable pieces; shared points are joined.</summary>
    public void AddBaseLine(double[] pts)
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
            if (prev >= 0 && prev != n && Dist(Nodes[prev], Nodes[n]) > 0.05) Edges.Add((prev, n, false));
            prev = n;
        }
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
        if (Graph.Snap(x, y, r) is RoadPos p) return (p.X, p.Y);
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
        _key.Clear();
        Rebuild();
    }
}
