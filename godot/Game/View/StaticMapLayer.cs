using Godot;
using Stations.Core.Map;

namespace Stations.View;

/// <summary>
/// Land use, water, buildings and railways. Triangulated once and recorded as a few
/// triangle arrays, so the camera can move without redrawing anything.
/// </summary>
public partial class StaticMapLayer : Node2D
{
    public MapData Map = null!;

    private readonly List<Vector2> _pts = new();
    private readonly List<Color> _cols = new();
    private readonly List<int> _idx = new();

    public override void _Draw()
    {
        float half = (float)Map.Half;
        DrawRect(new Rect2(-half * 4, -half * 4, half * 8, half * 8), Palette.Outside);
        DrawRect(new Rect2(-half, -half, half * 2, half * 2), Palette.Background);

        _pts.Clear();
        _cols.Clear();
        _idx.Clear();
        foreach (var poly in Map.Polys)
            foreach (var ring in poly.Rings) AddPolygon(ring, Palette.Poly(poly.Kind));
        FlushTriangles();

        foreach (var line in Map.Lines)
        {
            if (line.Tunnel) continue;
            var pts = ToVectors(line.Pts);
            switch (line.Kind)
            {
                case MapLineKind.River: DrawPolyline(pts, Palette.Water, 18); break;
                case MapLineKind.Canal: DrawPolyline(pts, Palette.Water, 10); break;
            }
        }

        foreach (var b in Map.Buildings) AddPolygon(b.Pts, Palette.Building(b.Kind));
        FlushTriangles();

        foreach (var line in Map.Lines)
        {
            if (line.Tunnel || line.Kind is MapLineKind.River or MapLineKind.Canal) continue;
            DrawPolyline(ToVectors(line.Pts), Palette.Rail, line.Kind == MapLineKind.Tram ? 1.5f : 2.5f);
        }
    }

    private void AddPolygon(double[] flat, Color color)
    {
        var poly = ToVectors(flat);
        if (poly.Length > 3 && poly[0].IsEqualApprox(poly[^1])) poly = poly[..^1]; // closed ways repeat the first node
        if (poly.Length < 3) return;
        int[] tri = Geometry2D.TriangulatePolygon(poly);
        if (tri.Length == 0) return; // self-intersecting OSM data: skip
        int start = _pts.Count;
        _pts.AddRange(poly);
        for (int i = 0; i < poly.Length; i++) _cols.Add(color);
        foreach (int i in tri) _idx.Add(start + i);
    }

    private void FlushTriangles()
    {
        if (_idx.Count == 0) return;
        RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), _idx.ToArray(), _pts.ToArray(), _cols.ToArray());
        _pts.Clear();
        _cols.Clear();
        _idx.Clear();
    }

    public static Vector2[] ToVectors(double[] flat)
    {
        var v = new Vector2[flat.Length / 2];
        for (int i = 0; i < v.Length; i++) v[i] = new Vector2((float)flat[2 * i], (float)flat[2 * i + 1]);
        return v;
    }
}

/// <summary>
/// Dims everything outside the loaded square: OSM returns whole ways and areas that
/// reach beyond the requested bounding box, and the game only plays inside it.
/// </summary>
public partial class OutsideMask : Node2D
{
    public float Half;

    public override void _Draw()
    {
        float h = Half, far = Half * 6;
        var c = new Color(Palette.Outside, 0.88f);
        DrawRect(new Rect2(-far, -far, 2 * far, far - h), c); // top
        DrawRect(new Rect2(-far, h, 2 * far, far - h), c); // bottom
        DrawRect(new Rect2(-far, -h, far - h, 2 * h), c); // left
        DrawRect(new Rect2(h, -h, far - h, 2 * h), c); // right
        DrawRect(new Rect2(-h, -h, 2 * h, 2 * h), new Color(0.4f, 0.37f, 0.32f, 0.5f), false, 3);
    }
}

/// <summary>
/// Streets with casing. Widths are in meters but never thinner than a few pixels,
/// so the layer is redrawn when the zoom changes noticeably.
/// </summary>
public partial class RoadLayer : Node2D
{
    public MapData Map = null!;
    private Vector2[][] _ways = Array.Empty<Vector2[]>();
    private float _drawnZoom = -1;

    /// <summary>Current camera zoom (pixels per meter); triggers a redraw when it changed by more than 20 %.</summary>
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
        _ways = Map.Roads.Select(r => StaticMapLayer.ToVectors(r.Pts)).ToArray();
    }

    public override void _Draw()
    {
        float z = Mathf.Max(_drawnZoom, 0.01f);
        for (int i = 0; i < _ways.Length; i++)
        {
            if (Map.Roads[i].Tunnel) DrawDashedPolyline(_ways[i], Palette.Tunnel, Mathf.Max(5, 1 / z), 6 / z);
        }
        foreach (var cls in Palette.RoadOrder)
        {
            var st = Palette.Road(cls);
            float w = Mathf.Max(st.Width, st.MinPx / z) + Mathf.Max(2, 1 / z);
            for (int i = 0; i < _ways.Length; i++)
                if (Map.Roads[i].Cls == cls && !Map.Roads[i].Tunnel) DrawPolyline(_ways[i], st.Casing, w);
        }
        foreach (var cls in Palette.RoadOrder)
        {
            var st = Palette.Road(cls);
            float w = Mathf.Max(st.Width, st.MinPx / z);
            for (int i = 0; i < _ways.Length; i++)
                if (Map.Roads[i].Cls == cls && !Map.Roads[i].Tunnel) DrawPolyline(_ways[i], st.Fill, w);
        }
    }

    private void DrawDashedPolyline(Vector2[] pts, Color c, float width, float dash)
    {
        for (int i = 0; i + 1 < pts.Length; i++) DrawDashedLine(pts[i], pts[i + 1], c, width, dash);
    }
}
