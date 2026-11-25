using Stations.Core.Map;

namespace Stations.Core.Sim;

public sealed record TunnelPlan(double X1, double Y1, double X2, double Y2, double Length, double Cost, string? Error);

/// <summary>
/// Rail networks of the city tier. U-Bahn: tunnels the player lays freely through the city.
/// S-Bahn: the real railway lines from the map (OSM rail and light rail, without yards and sidings)
/// plus tracks the player adds. Stations and depots sit on the network; lines and trains are routed
/// over its graph, which is rebuilt after every change.
/// </summary>
public sealed partial class Game
{
    public const double MetroSpeed = 80 / 3.6;
    public const double RailSpeed = 100 / 3.6;
    private const double MinPiece = 40; // m

    public readonly TrackNet Metro = new(MetroSpeed);
    private TrackNet? _rail;
    /// <summary>S-Bahn network (built from the map data on first use).</summary>
    public TrackNet Rail => _rail ??= BuildRailBase();

    public RoadGraph MetroGraph => Metro.Graph;
    public int MetroVersion => Metro.Version;

    public static bool IsMetro(ModeId mode) => Modes.Get(mode).Infra == Infra.Metro;
    public static bool IsRail(ModeId mode) => mode == ModeId.SBahn;
    /// <summary>Modes that run on a network of their own (tunnels or railway) instead of streets.</summary>
    public static bool UsesNet(ModeId mode) => IsMetro(mode) || IsRail(mode);

    public TrackNet? NetFor(ModeId mode) => IsMetro(mode) ? Metro : IsRail(mode) ? Rail : null;

    /// <summary>Minimum distance between two stations of a network mode.</summary>
    public static double MinStationGap(ModeId mode) => IsRail(mode) ? 400 : 250;

    public double TunnelLength => Metro.Length();

    private TrackNet BuildRailBase()
    {
        var net = new TrackNet(RailSpeed);
        foreach (var l in Map.Lines)
            if (l.Kind is MapLineKind.Rail or MapLineKind.LightRail && l.Service is not ("yard" or "siding" or "spur"))
                net.AddBaseLine(l.Pts);
        net.Rebuild();
        return net;
    }

    /// <summary>After a network change: move stations and depots onto the new segment numbers.</summary>
    private void Resnap(ModeId mode)
    {
        var net = NetFor(mode)!;
        foreach (var s in Stops.Values.Where(s => NetFor(s.Mode) == net))
            if (net.Graph.Snap(s.Pos.X, s.Pos.Y, 2) is RoadPos p) s.Pos = p;
        foreach (var d in Depots.Values.Where(d => NetFor(d.Mode) == net))
            if (net.Graph.Snap(d.Pos.X, d.Pos.Y, 2) is RoadPos p) d.Pos = p;
    }

    public (double X, double Y) SnapNetPoint(ModeId mode, double x, double y, double r) => NetFor(mode)!.SnapPoint(x, y, r);

    public TunnelPlan PlanPiece(ModeId mode, (double X, double Y) a, (double X, double Y) b)
    {
        var net = NetFor(mode)!;
        double len = Util.Hypot(a.X - b.X, a.Y - b.Y);
        string? err = len < MinPiece ? (IsMetro(mode) ? "Tunnelstück zu kurz" : "Gleisstück zu kurz") : null;
        if (err == null && net.HasPiece(a, b)) err = IsMetro(mode) ? "Hier liegt schon ein Tunnel" : "Hier liegt schon ein Gleis";
        return new TunnelPlan(a.X, a.Y, b.X, b.Y, len, len * Modes.Get(mode).TrackCostPerM, err);
    }

    /// <summary>Lay a straight tunnel (U-Bahn) or track (S-Bahn) from a to b.</summary>
    public bool BuildPiece(ModeId mode, (double X, double Y) a, (double X, double Y) b)
    {
        var plan = PlanPiece(mode, a, b);
        if (plan.Error != null)
        {
            Notify(plan.Error, true);
            return false;
        }
        if (!Pay(plan.Cost)) return false;
        NetFor(mode)!.AddPiece(a, b);
        Resnap(mode);
        OnChange();
        return true;
    }

    // U-Bahn shortcuts
    public (double X, double Y) SnapTunnelPoint(double x, double y, double r) => SnapNetPoint(ModeId.Metro, x, y, r);
    public TunnelPlan PlanTunnel((double X, double Y) a, (double X, double Y) b) => PlanPiece(ModeId.Metro, a, b);
    public bool BuildTunnel((double X, double Y) a, (double X, double Y) b) => BuildPiece(ModeId.Metro, a, b);
    public bool RemoveTunnelAt(double x, double y, double radius) => RemovePieceAt(ModeId.Metro, x, y, radius);

    /// <summary>
    /// Remove the piece near (x, y) of a mode's network. Refused if a station or depot lies on it, a line runs
    /// through it, or (S-Bahn) it belongs to the existing railway. Returns false if nothing was hit.
    /// </summary>
    public bool RemovePieceAt(ModeId mode, double x, double y, double radius)
    {
        var net = NetFor(mode)!;
        if (net.Graph.Snap(x, y, radius) is not RoadPos hit) return false;
        int e = hit.Seg;
        if (!net.Edges[e].Built)
        {
            Notify("Bestehende Bahnstrecken bleiben – abreißen lassen sich nur selbst gebaute Gleise", true);
            return true;
        }
        string? blocker = Stops.Values.Where(s => NetFor(s.Mode) == net && s.Pos.Seg == e).Select(s => s.Name)
            .Concat(Depots.Values.Where(d => NetFor(d.Mode) == net && d.Pos.Seg == e).Select(d => d.Name)).FirstOrDefault();
        if (blocker != null)
        {
            Notify($"Hier liegt noch „{blocker}“", true);
            return true;
        }
        var removed = net.Edges[e];
        var nodes = net.Nodes.ToList();
        var edges = net.Edges.ToList();
        net.RemovePiece(e);
        Resnap(mode);
        var broken = Lines.Values.FirstOrDefault(l => NetFor(l.Mode) == net && BuildRuns(l.StopIds, l.Circular, l.Mode, out _) == null);
        if (broken != null)
        {
            net.Nodes.Clear();
            net.Nodes.AddRange(nodes);
            net.Edges.Clear();
            net.Edges.AddRange(edges);
            net.Rebuild();
            Resnap(mode);
            Notify($"Linie {broken.Name} fährt hier entlang", true);
            return true;
        }
        OnChange();
        return true;
    }

    /// <summary>Remove a built piece of any network near (x, y) (bulldozer).</summary>
    public bool RemoveNetPieceAt(double x, double y, double radius) =>
        RemovePieceAt(ModeId.Metro, x, y, radius) || (_rail != null && Rail.Edges.Any(e => e.Built) && RemovePieceAt(ModeId.SBahn, x, y, radius));

    /// <summary>Restore tunnels from a save.</summary>
    private void LoadMetro(List<double[]>? nodes, List<int[]>? edges)
    {
        Metro.Nodes.Clear();
        Metro.Edges.Clear();
        foreach (var n in nodes ?? new()) Metro.Nodes.Add((n[0], n[1]));
        foreach (var e in edges ?? new())
            if (e[0] >= 0 && e[1] >= 0 && e[0] < Metro.Nodes.Count && e[1] < Metro.Nodes.Count && e[0] != e[1]) Metro.Edges.Add((e[0], e[1], true));
        Metro.Rebuild();
    }

    /// <summary>Restore the player's S-Bahn tracks from a save (laid again on top of the railway).</summary>
    private void LoadRail(List<double[]>? pieces)
    {
        if (pieces == null || pieces.Count == 0) return;
        foreach (var p in pieces) Rail.AddPiece((p[0], p[1]), (p[2], p[3]));
    }

    private List<double[]> SaveRail() => _rail == null ? new() : Rail.Edges.Where(e => e.Built)
        .Select(e => new[] { Rail.Nodes[e.A].X, Rail.Nodes[e.A].Y, Rail.Nodes[e.B].X, Rail.Nodes[e.B].Y }).ToList();
}
