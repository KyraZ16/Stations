using Godot;
using Stations.Core.Map;
using Stations.Core.Sim;

namespace Stations.View;

/// <summary>
/// Catchment overlay: buildings within walking distance of a served stop in green, others in red.
/// Buildings are triangulated once; colors are recomputed when the served network changes.
/// </summary>
public partial class CoverageLayer : Node2D
{
    public Game Game = null!;

    private Vector2[] _pts = Array.Empty<Vector2>();
    private int[] _idx = Array.Empty<int>();
    private (int Start, int Count, Building B)[] _ranges = Array.Empty<(int, int, Building)>();
    private Color[] _cols = Array.Empty<Color>();
    private int _drawnVersion = -1;

    private static readonly Color Covered = new("9fd39a");
    private static readonly Color Uncovered = new("e3a49a");

    public override void _Ready()
    {
        var pts = new List<Vector2>();
        var idx = new List<int>();
        var ranges = new List<(int, int, Building)>();
        foreach (var b in Game.Map.Buildings)
        {
            if (b.Kind == BuildingKind.Minor) continue;
            var poly = StaticMapLayer.ToVectors(b.Pts);
            if (poly.Length > 3 && poly[0].IsEqualApprox(poly[^1])) poly = poly[..^1];
            if (poly.Length < 3) continue;
            int[] tri = Geometry2D.TriangulatePolygon(poly);
            if (tri.Length == 0) continue;
            int start = pts.Count;
            pts.AddRange(poly);
            foreach (int i in tri) idx.Add(start + i);
            ranges.Add((start, poly.Length, b));
        }
        _pts = pts.ToArray();
        _idx = idx.ToArray();
        _ranges = ranges.ToArray();
        _cols = new Color[_pts.Length];
    }

    public override void _Process(double delta)
    {
        if (Visible && _drawnVersion != Game.NetworkVersion) QueueRedraw();
    }

    public override void _Draw()
    {
        _drawnVersion = Game.NetworkVersion;
        if (_idx.Length == 0) return;
        var served = Game.Lines.Values.Where(l => double.IsFinite(Game.Headway(l)))
            .SelectMany(l => l.StopIds).Distinct()
            .Select(id => Game.Stops.TryGetValue(id, out var s) ? s : null).OfType<Stop>()
            .Select(s => (s.Pos.X, s.Pos.Y, R: s.Mode == Core.ModeId.Metro ? Router.MetroAccessRadius : Router.AccessRadius)).ToArray();
        foreach (var (start, count, b) in _ranges)
        {
            bool ok = false;
            foreach (var (x, y, rr) in served)
            {
                double dx = x - b.Cx, dy = y - b.Cy;
                if (dx * dx + dy * dy < rr * rr) { ok = true; break; }
            }
            var c = ok ? Covered : Uncovered;
            for (int i = 0; i < count; i++) _cols[start + i] = c;
        }
        RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), _idx, _pts, _cols);
    }
}
