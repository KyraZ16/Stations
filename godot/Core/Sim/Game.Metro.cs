using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>The underground network the player builds: points and straight tunnel pieces between them.</summary>
public sealed class MetroNet
{
    public readonly List<(double X, double Y)> Nodes = new();
    public readonly List<(int A, int B)> Edges = new();
}

public sealed record TunnelPlan(double X1, double Y1, double X2, double Y2, double Length, double Cost, string? Error);

/// <summary>
/// U-Bahn: tunnels are laid freely through the city (not along streets), point by point. Tunnels that
/// cross without a shared point lie at different depths and are not connected. Stations and depots sit
/// on tunnels; lines and trains are routed over the tunnel graph, which is rebuilt after every change.
/// </summary>
public sealed partial class Game
{
    public const double MetroSpeed = 80 / 3.6;
    public const double MinStationGap = 250; // m between two U-Bahn stations
    private const double MinTunnel = 40; // m

    public readonly MetroNet Metro = new();
    /// <summary>Routing graph of the tunnels (vertex = node index, segment = edge index).</summary>
    public RoadGraph MetroGraph { get; private set; } = new();
    /// <summary>Incremented when tunnels change (renderer cache).</summary>
    public int MetroVersion;

    public static bool IsMetro(ModeId mode) => Modes.Get(mode).Infra == Infra.Metro;

    public double TunnelLength => Metro.Edges.Sum(e => Dist(Metro.Nodes[e.A], Metro.Nodes[e.B]));

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Util.Hypot(a.X - b.X, a.Y - b.Y);

    /// <summary>Rebuild the routing graph and move stations and depots onto the new segment numbers.</summary>
    private void RebuildMetroGraph()
    {
        var g = new RoadGraph();
        for (int i = 0; i < Metro.Nodes.Count; i++) g.Vertex(i, Metro.Nodes[i].X, Metro.Nodes[i].Y);
        foreach (var (a, b) in Metro.Edges) g.AddSegment(a, b, true, true, MetroSpeed, -1);
        MetroGraph = g;
        MetroVersion++;
        foreach (var s in Stops.Values.Where(s => IsMetro(s.Mode)))
            if (g.Snap(s.Pos.X, s.Pos.Y, 2) is RoadPos p) s.Pos = p;
        foreach (var d in Depots.Values.Where(d => IsMetro(d.Mode)))
            if (g.Snap(d.Pos.X, d.Pos.Y, 2) is RoadPos p) d.Pos = p;
    }

    /// <summary>Snap a point to an existing tunnel point or tunnel (within r), else keep it.</summary>
    public (double X, double Y) SnapTunnelPoint(double x, double y, double r)
    {
        int best = -1;
        double bd = r;
        for (int i = 0; i < Metro.Nodes.Count; i++)
        {
            double d = Util.Hypot(Metro.Nodes[i].X - x, Metro.Nodes[i].Y - y);
            if (d < bd) { bd = d; best = i; }
        }
        if (best >= 0) return Metro.Nodes[best];
        if (MetroGraph.Snap(x, y, r) is RoadPos p) return (p.X, p.Y);
        return (x, y);
    }

    public TunnelPlan PlanTunnel((double X, double Y) a, (double X, double Y) b)
    {
        double len = Dist(a, b);
        string? err = len < MinTunnel ? "Tunnelstück zu kurz" : null;
        if (err == null && Metro.Edges.Any(e => SameEdge(e, a, b))) err = "Hier liegt schon ein Tunnel";
        return new TunnelPlan(a.X, a.Y, b.X, b.Y, len, len * Modes.Get(ModeId.Metro).TrackCostPerM, err);
    }

    private bool SameEdge((int A, int B) e, (double X, double Y) a, (double X, double Y) b)
    {
        var p = Metro.Nodes[e.A];
        var q = Metro.Nodes[e.B];
        return (Dist(p, a) < 1 && Dist(q, b) < 1) || (Dist(p, b) < 1 && Dist(q, a) < 1);
    }

    /// <summary>Lay a straight tunnel from a to b (both may be on existing tunnels: they get connected).</summary>
    public bool BuildTunnel((double X, double Y) a, (double X, double Y) b)
    {
        var plan = PlanTunnel(a, b);
        if (plan.Error != null)
        {
            Notify(plan.Error, true);
            return false;
        }
        if (!Pay(plan.Cost)) return false;
        int ia = NodeFor(a), ib = NodeFor(b);
        if (ia != ib) Metro.Edges.Add((ia, ib));
        RebuildMetroGraph();
        OnChange();
        return true;
    }

    /// <summary>Node at this point: an existing one, a new one splitting a tunnel, or a new free one.</summary>
    private int NodeFor((double X, double Y) p)
    {
        for (int i = 0; i < Metro.Nodes.Count; i++)
            if (Dist(Metro.Nodes[i], p) < 1) return i;
        for (int e = 0; e < Metro.Edges.Count; e++)
        {
            var (a, b) = Metro.Edges[e];
            var (d, t) = Util.DistToSegment(p.X, p.Y, Metro.Nodes[a].X, Metro.Nodes[a].Y, Metro.Nodes[b].X, Metro.Nodes[b].Y);
            if (d > 1 || t <= 0.001 || t >= 0.999) continue;
            int k = Metro.Nodes.Count;
            Metro.Nodes.Add(p);
            Metro.Edges[e] = (a, k);
            Metro.Edges.Add((k, b));
            return k;
        }
        Metro.Nodes.Add(p);
        return Metro.Nodes.Count - 1;
    }

    /// <summary>
    /// Remove the tunnel piece near (x, y). Refused if a station or depot lies on it or a line runs through it.
    /// Returns false if no tunnel was hit.
    /// </summary>
    public bool RemoveTunnelAt(double x, double y, double radius)
    {
        if (MetroGraph.Snap(x, y, radius) is not RoadPos hit) return false;
        int e = hit.Seg;
        string? blocker = Stops.Values.Where(s => IsMetro(s.Mode) && s.Pos.Seg == e).Select(s => s.Name)
            .Concat(Depots.Values.Where(d => IsMetro(d.Mode) && d.Pos.Seg == e).Select(d => d.Name)).FirstOrDefault();
        if (blocker != null)
        {
            Notify($"Im Tunnel liegt noch „{blocker}“", true);
            return true;
        }
        var removed = Metro.Edges[e];
        Metro.Edges.RemoveAt(e);
        RebuildMetroGraph();
        var broken = Lines.Values.FirstOrDefault(l => IsMetro(l.Mode) && BuildRuns(l.StopIds, l.Circular, l.Mode, out _) == null);
        if (broken != null)
        {
            Metro.Edges.Insert(e, removed);
            RebuildMetroGraph();
            Notify($"Linie {broken.Name} fährt durch diesen Tunnel", true);
            return true;
        }
        // drop points that no tunnel uses any more
        var used = Metro.Edges.SelectMany(t => new[] { t.A, t.B }).ToHashSet();
        var map = new int[Metro.Nodes.Count];
        var nodes = new List<(double, double)>();
        for (int i = 0; i < Metro.Nodes.Count; i++)
        {
            map[i] = used.Contains(i) ? nodes.Count : -1;
            if (used.Contains(i)) nodes.Add(Metro.Nodes[i]);
        }
        var edges = Metro.Edges.Select(t => (map[t.A], map[t.B])).ToList();
        Metro.Nodes.Clear();
        Metro.Nodes.AddRange(nodes);
        Metro.Edges.Clear();
        Metro.Edges.AddRange(edges);
        RebuildMetroGraph();
        OnChange();
        return true;
    }

    /// <summary>Restore tunnels from a save.</summary>
    private void LoadMetro(List<double[]>? nodes, List<int[]>? edges)
    {
        Metro.Nodes.Clear();
        Metro.Edges.Clear();
        foreach (var n in nodes ?? new()) Metro.Nodes.Add((n[0], n[1]));
        foreach (var e in edges ?? new())
            if (e[0] >= 0 && e[1] >= 0 && e[0] < Metro.Nodes.Count && e[1] < Metro.Nodes.Count && e[0] != e[1]) Metro.Edges.Add((e[0], e[1]));
        RebuildMetroGraph();
    }
}
