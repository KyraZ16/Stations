using Godot;
using Stations.Core.Map;
using Stations.Core.Sim;
using Stations.View;

namespace Stations.UI;

/// <summary>
/// Overview map in the lower left: water and main roads (drawn once), lines, vehicles and the
/// visible area (updated a few times per second). Click or drag to move the main map there.
/// </summary>
public partial class MiniMap : PanelContainer
{
    public Game Game = null!;
    public MapView Map = null!;
    private const float MiniSize = 190;

    public override void _Ready()
    {
        AnchorLeft = 0;
        AnchorRight = 0;
        AnchorTop = 1;
        AnchorBottom = 1;
        OffsetLeft = 10;
        OffsetRight = 10 + MiniSize + 12;
        OffsetTop = -(MiniSize + 12) - 10;
        OffsetBottom = -10;
        AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 6));
        var stack = new Control { CustomMinimumSize = new Vector2(MiniSize, MiniSize), ClipContents = true };
        AddChild(stack);
        stack.AddChild(new Base { Game = Game });
        stack.AddChild(new Overlay { Game = Game, Map = Map });
    }

    private static Vector2 ToMini(MapData m, double x, double y)
    {
        float s = MiniSize / (float)(2 * m.Half);
        return new Vector2((float)(x + m.Half) * s, (float)(y + m.Half) * s);
    }

    /// <summary>Static part: background, water, main roads.</summary>
    private partial class Base : Control
    {
        public Game Game = null!;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(MiniSize, MiniSize);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var m = Game.Map;
            DrawRect(new Rect2(0, 0, MiniSize, MiniSize), Palette.Background);
            var pts = new List<Vector2>();
            var idx = new List<int>();
            foreach (var poly in m.Polys.Where(p => p.Kind is PolyKind.Water or PolyKind.Park or PolyKind.Forest))
            {
                foreach (var ring in poly.Rings)
                {
                    var r = new Vector2[ring.Length / 2];
                    for (int i = 0; i < r.Length; i++) r[i] = ToMini(m, ring[2 * i], ring[2 * i + 1]);
                    var tri = Geometry2D.TriangulatePolygon(r);
                    if (tri.Length == 0) continue;
                    int start = pts.Count;
                    pts.AddRange(r);
                    foreach (int t in tri) idx.Add(start + t);
                    var col = poly.Kind == PolyKind.Water ? Palette.Water : Palette.Poly(poly.Kind);
                    // per-vertex colors are added below
                    for (int i = 0; i < r.Length; i++) _colors.Add(col);
                }
            }
            if (idx.Count > 0) RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), idx.ToArray(), pts.ToArray(), _colors.ToArray());
            foreach (var l in m.Lines.Where(l => l.Kind is MapLineKind.River or MapLineKind.Canal && !l.Tunnel))
                DrawPolyline(Flat(m, l.Pts), Palette.Water, l.Kind == MapLineKind.River ? 2.5f : 1.5f);
            foreach (var r in m.Roads.Where(r => r.Cls is RoadClass.Trunk or RoadClass.Primary or RoadClass.Secondary or RoadClass.Motorway && !r.Tunnel))
                DrawPolyline(Flat(m, r.Pts), new Color("d9cdb8"), r.Cls == RoadClass.Secondary ? 0.8f : 1.3f);
        }

        private readonly List<Color> _colors = new();

        private static Vector2[] Flat(MapData m, double[] p)
        {
            var v = new Vector2[p.Length / 2];
            for (int i = 0; i < v.Length; i++) v[i] = ToMini(m, p[2 * i], p[2 * i + 1]);
            return v;
        }
    }

    /// <summary>Dynamic part: lines, vehicles, the visible area; handles clicks.</summary>
    private partial class Overlay : Control
    {
        public Game Game = null!;
        public MapView Map = null!;
        private double _timer;
        private bool _dragging;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(MiniSize, MiniSize);
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = CursorShape.PointingHand;
        }

        public override void _Process(double delta)
        {
            _timer += delta;
            if (_timer < 0.1) return;
            _timer = 0;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var m = Game.Map;
            foreach (var line in Game.Lines.Values)
            {
                var color = new Color(line.Color);
                foreach (var run in line.Runs)
                {
                    var p = run.Path;
                    if (p.Count < 2) continue;
                    var v = new Vector2[p.Count];
                    for (int i = 0; i < v.Length; i++) v[i] = ToMini(m, p.Xs[i], p.Ys[i]);
                    DrawPolyline(v, color, 1.6f);
                }
            }
            foreach (var v in Game.Vehicles.Values)
            {
                if (Game.InYard(v)) continue;
                var c = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? new Color(l.Color) : new Color("7d8590");
                DrawCircle(ToMini(m, v.X, v.Y), 1.8f, Palette.Ink);
                DrawCircle(ToMini(m, v.X, v.Y), 1.2f, c.Lightened(0.3f));
            }
            foreach (var d in Game.Depots.Values)
                DrawRect(new Rect2(ToMini(m, d.Pos.X, d.Pos.Y) - new Vector2(2.5f, 2.5f), new Vector2(5, 5)), new Color("2d3640"));

            // Visible area of the main map
            var view = Map.ViewRect();
            var a = ToMini(m, view.Position.X, view.Position.Y);
            var b = ToMini(m, view.End.X, view.End.Y);
            var r = new Rect2(a, b - a);
            DrawRect(r, new Color(0.04f, 0.43f, 0.74f, 0.12f));
            DrawRect(r, Ui.Accent, false, 1.5f);
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                    _dragging = b.Pressed;
                    if (b.Pressed) Jump(b.Position);
                    AcceptEvent();
                    break;
                case InputEventMouseMotion mm when _dragging:
                    Jump(mm.Position);
                    AcceptEvent();
                    break;
            }
        }

        private void Jump(Vector2 local)
        {
            var m = Game.Map;
            float s = (float)(2 * m.Half) / MiniSize;
            Map.CenterOn(new Vector2(local.X * s - (float)m.Half, local.Y * s - (float)m.Half));
            QueueRedraw();
        }
    }
}
