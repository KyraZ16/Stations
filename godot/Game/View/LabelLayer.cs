using Godot;
using Stations.Core.Map;

namespace Stations.View;

/// <summary>
/// Street names along the roads. Major roads show first; minor ones only when zoomed in.
/// Labels follow the road direction, stay upright, are skipped when they would overlap
/// another label or not fit on the road, and are redrawn only when the zoom changes noticeably.
/// </summary>
public partial class LabelLayer : Node2D
{
    public MapData Map = null!;

    private sealed record Candidate(string Name, RoadClass Cls, Vector2 Pos, float Angle, float Length);

    private List<Candidate> _candidates = new();
    private float _drawnZoom = -1;
    private Font _font = null!;

    public float Zoom
    {
        set
        {
            if (_drawnZoom > 0 && Mathf.Abs(value / _drawnZoom - 1) < 0.2f) return;
            _drawnZoom = value;
            QueueRedraw();
        }
    }

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _candidates = BuildCandidates();
    }

    /// <summary>
    /// One candidate per straight-ish stretch of a named road: the middle of its longest segment run.
    /// Repeats of the same name closer than 350 m are dropped.
    /// </summary>
    private List<Candidate> BuildCandidates()
    {
        var list = new List<Candidate>();
        foreach (var r in Map.Roads)
        {
            if (string.IsNullOrEmpty(r.Name) || r.Tunnel || r.Cls is RoadClass.Service or RoadClass.Motorway) continue;
            // Longest stretch without sharp bends
            var pts = StaticMapLayer.ToVectors(r.Pts);
            int bestStart = 0, bestEnd = 0;
            float bestLen = 0;
            int start = 0;
            float len = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                if (i >= 2)
                {
                    var d1 = (pts[i - 1] - pts[i - 2]).Normalized();
                    var d2 = (pts[i] - pts[i - 1]).Normalized();
                    if (d1.Dot(d2) < 0.94f) // bend > ~20°
                    {
                        start = i - 1;
                        len = 0;
                    }
                }
                len += pts[i].DistanceTo(pts[i - 1]);
                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = start;
                    bestEnd = i;
                }
            }
            if (bestLen < 40) continue;
            var a = pts[bestStart];
            var b = pts[bestEnd];
            float angle = (b - a).Angle();
            if (Mathf.Cos(angle) < 0) angle += Mathf.Pi; // keep text upright
            list.Add(new Candidate(r.Name, r.Cls, (a + b) / 2, angle, bestLen));
        }
        // Important roads first, longer stretches first
        list.Sort((x, y) => x.Cls != y.Cls ? Rank(x.Cls).CompareTo(Rank(y.Cls)) : y.Length.CompareTo(x.Length));
        var kept = new List<Candidate>();
        foreach (var c in list)
            if (!kept.Any(k => k.Name == c.Name && k.Pos.DistanceTo(c.Pos) < 350)) kept.Add(c);
        return kept;
    }

    private static int Rank(RoadClass c) => c switch
    {
        RoadClass.Trunk => 0, RoadClass.Primary => 1, RoadClass.Secondary => 2, RoadClass.Tertiary => 3,
        RoadClass.Minor => 4, _ => 5,
    };

    /// <summary>Minimum zoom (pixels per meter) at which a road class gets labels.</summary>
    private static float MinZoom(RoadClass c) => c switch
    {
        RoadClass.Trunk or RoadClass.Primary => 0.35f,
        RoadClass.Secondary => 0.5f,
        RoadClass.Tertiary => 0.65f,
        RoadClass.Minor => 1.0f,
        _ => 2.5f,
    };

    public override void _Draw()
    {
        float z = Mathf.Max(_drawnZoom, 0.01f);
        float px = 1 / z;
        var placed = new List<Rect2>(); // label boxes in world units, axis-aligned approximation
        foreach (var c in _candidates)
        {
            if (z < MinZoom(c.Cls)) continue;
            int size = c.Cls is RoadClass.Trunk or RoadClass.Primary ? 13 : c.Cls == RoadClass.Secondary ? 12 : 11;
            var textSize = _font.GetStringSize(c.Name, HorizontalAlignment.Left, -1, size);
            float worldLen = textSize.X * px;
            if (worldLen > c.Length * 1.6f) continue; // much longer than the stretch (OSM splits streets at junctions, so a little overhang is fine)
            var dir = Vector2.FromAngle(c.Angle);
            var half = new Vector2(Mathf.Abs(dir.X) * worldLen / 2 + 6 * px, Mathf.Abs(dir.Y) * worldLen / 2 + 6 * px);
            var box = new Rect2(c.Pos - half, half * 2);
            if (placed.Any(p => p.Intersects(box))) continue;
            placed.Add(box);
            DrawSetTransform(c.Pos, c.Angle, new Vector2(px, px));
            var at = new Vector2(-textSize.X / 2, size * 0.35f);
            DrawStringOutline(_font, at, c.Name, HorizontalAlignment.Left, -1, size, 4, new Color(1, 1, 1, 0.85f));
            DrawString(_font, at, c.Name, HorizontalAlignment.Left, -1, size, new Color("5a564f"));
        }
        DrawSetTransform(Vector2.Zero);
    }
}
