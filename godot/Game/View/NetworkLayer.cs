using Godot;
using Stations.Core;
using Stations.Core.Sim;

namespace Stations.View;

/// <summary>
/// Everything the player builds plus the moving vehicles. Redrawn every frame;
/// sizes are given in screen pixels and converted with the camera zoom.
/// </summary>
public partial class NetworkLayer : Node2D
{
    public Game Game = null!;
    /// <summary>Camera zoom in pixels per meter.</summary>
    public float Zoom = 1;
    /// <summary>Tool state: selection, drafts and hover feedback.</summary>
    public Interaction UI = null!;

    private bool IsSel(string kind, int id) => UI.Selected is Selection s && s.Kind == kind && s.Id == id;

    private Vector2[] _trackSegs = Array.Empty<Vector2>();
    private int _trackVersion = -1;
    private Font _font = null!;

    public override void _Ready() => _font = ThemeDB.FallbackFont;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        float px = 1 / Zoom; // one screen pixel in world meters
        DrawTunnels(px);
        DrawRail(px);
        DrawTracks(px);
        DrawTrackPlan(px);
        DrawTunnelPlan(px);
        DrawLines(px);
        DrawDraft(px);
        DrawDepots(px);
        DrawStops(px);
        DrawVehicles(px); // on top of stops, so vehicles waiting at a terminus stay visible
        DrawSnapPreview(px);
        DrawSetTransform(Vector2.Zero);
    }

    private static Vector2 V(double x, double y) => new((float)x, (float)y);

    private void DrawDashedPolyline(IReadOnlyList<Vector2> pts, Color c, float width, float dash)
    {
        for (int i = 0; i + 1 < pts.Count; i++) DrawDashedLine(pts[i], pts[i + 1], c, width, dash);
    }

    private void DrawTrackPlan(float px)
    {
        if (UI.Tool != Tool.Track || UI.TrackPlan is not TrackPlan plan) return;
        var pts = plan.Route.Xs.Select((x, i) => V(x, plan.Route.Ys[i])).ToList();
        DrawDashedPolyline(pts, new Color(Palette.TrackOuter, 0.6f), Mathf.Max(7.5f, 12 * px), 8 * px);
    }

    /// <summary>Line being drawn: routed legs dashed in the next line color, plus a hint line to the cursor.</summary>
    private void DrawDraft(float px)
    {
        if (UI.DraftStops.Count == 0) return;
        var color = new Color(Util.LineColors[Game.Lines.Count % Util.LineColors.Length]);
        foreach (var d in UI.DraftPaths)
            DrawDashedPolyline(d.Xs.Select((x, i) => V(x, d.Ys[i])).ToList(), color, 4 * px, 10 * px);
        var last = UI.DraftStops[^1].Pos;
        Vector2? target = UI.HoverStop is Stop hs ? V(hs.Pos.X, hs.Pos.Y)
            : UI.HoverPos is { } hp ? V(hp.X, hp.Y) : UI.MouseWorld;
        if (target is Vector2 t) DrawDashedLine(V(last.X, last.Y), t, new Color(color, 0.5f), 4 * px, 10 * px);
    }

    /// <summary>Ghost of the stop/depot that a click would place.</summary>
    private void DrawSnapPreview(float px)
    {
        if (UI.Tool == Tool.Track && Game.UsesNet(UI.Mode))
        {
            if (UI.TunnelHover is { } th)
            {
                DrawCircle(V(th.X, th.Y), 5 * px, new Color(1, 1, 1, 0.8f));
                DrawArc(V(th.X, th.Y), 5 * px, 0, Mathf.Tau, 20, new Color("1f4e9c"), 2 * px);
            }
            return;
        }
        if (UI.HoverPos is not { } p || UI.HoverStop != null) return;
        if (UI.Tool is not (Tool.Stop or Tool.Line or Tool.Edit or Tool.Depot or Tool.Track)) return;
        DrawSetTransform(V(p.X, p.Y), 0, new Vector2(px, px));
        var ghost = new Color(1, 1, 1, 0.7f);
        if (UI.Tool == Tool.Depot)
        {
            DrawRect(new Rect2(-9, -9, 18, 18), ghost);
            DrawRect(new Rect2(-9, -9, 18, 18), new Color("333333"), false, 2);
        }
        else
        {
            float r = UI.Tool == Tool.Track ? 5 : Mathf.Clamp(Zoom * 6, 4, 8);
            DrawCircle(Vector2.Zero, r, ghost);
            DrawArc(Vector2.Zero, r, 0, Mathf.Tau, 24, new Color("333333"), 2);
        }
        DrawSetTransform(Vector2.Zero);
    }

    private Vector2[] _tunnelSegs = Array.Empty<Vector2>();
    private int _tunnelVersion = -1;
    private static readonly Color TunnelOuter = new("3b4452") { A = 0.55f };
    private static readonly Color TunnelInner = new("c9ced6") { A = 0.7f };

    /// <summary>U-Bahn tunnels: a dark band with a light dashed core, so they read as "underground".</summary>
    private void DrawTunnels(float px)
    {
        if (Game.Metro.Edges.Count == 0) return;
        if (_tunnelVersion != Game.MetroVersion)
        {
            _tunnelVersion = Game.MetroVersion;
            _tunnelSegs = Game.Metro.Edges.SelectMany(e => new[]
            {
                V(Game.Metro.Nodes[e.A].X, Game.Metro.Nodes[e.A].Y), V(Game.Metro.Nodes[e.B].X, Game.Metro.Nodes[e.B].Y),
            }).ToArray();
        }
        DrawMultiline(_tunnelSegs, TunnelOuter, Mathf.Max(8f, 14 * px));
        for (int i = 0; i + 1 < _tunnelSegs.Length; i += 2)
            DrawDashedLine(_tunnelSegs[i], _tunnelSegs[i + 1], TunnelInner, Mathf.Max(2f, 3 * px), 10 * px);
        if (UI.Tool == Tool.Track && Game.IsMetro(UI.Mode))
            foreach (var n in Game.Metro.Nodes)
                DrawCircle(V(n.X, n.Y), 4 * px, new Color("3b4452"));
    }

    private Vector2[] _railBase = Array.Empty<Vector2>(), _railBuilt = Array.Empty<Vector2>();
    private int _railVersion = -1;
    private static readonly Color RailHighlight = new("008d4f") { A = 0.45f };

    /// <summary>
    /// S-Bahn network: tracks the player laid always (like tram tracks); the railway from the map is
    /// highlighted while the S-Bahn mode is active, so it is clear where stations can go.
    /// </summary>
    private void DrawRail(float px)
    {
        bool sbahn = Game.IsRail(UI.Mode); // S-Bahn or regional mode: show where trains can run
        bool anyBuilt = Game.Stops.Values.Any(s => Game.IsRail(s.Mode)) || sbahn;
        if (!anyBuilt) return;
        var net = Game.Rail;
        if (_railVersion != net.Version)
        {
            _railVersion = net.Version;
            Vector2[] Segs(bool built) => net.Edges.Where(e => e.Built == built).SelectMany(e => new[]
            {
                V(net.Nodes[e.A].X, net.Nodes[e.A].Y), V(net.Nodes[e.B].X, net.Nodes[e.B].Y),
            }).ToArray();
            _railBase = Segs(false);
            _railBuilt = Segs(true);
        }
        if (sbahn && _railBase.Length > 0) DrawMultiline(_railBase, RailHighlight, 4 * px); // thin: station throats have dozens of parallel tracks
        if (_railBuilt.Length > 0)
        {
            DrawMultiline(_railBuilt, Palette.TrackOuter, Mathf.Max(7.5f, 12 * px));
            DrawMultiline(_railBuilt, Palette.TrackInner, Mathf.Max(4.5f, 8 * px));
        }
        if (UI.Tool == Tool.Track && sbahn)
            foreach (var e in net.Edges.Where(e => e.Built))
            {
                DrawCircle(V(net.Nodes[e.A].X, net.Nodes[e.A].Y), 4 * px, new Color("008d4f"));
                DrawCircle(V(net.Nodes[e.B].X, net.Nodes[e.B].Y), 4 * px, new Color("008d4f"));
            }
    }

    /// <summary>Tunnel being planned: dashed from the start point to the cursor, with length and price.</summary>
    private void DrawTunnelPlan(float px)
    {
        if (UI.Tool != Tool.Track || !Game.UsesNet(UI.Mode)) return;
        if (UI.TunnelStart is { } s) DrawCircle(V(s.X, s.Y), 6 * px, new Color("1f4e9c"));
        if (UI.TunnelPlan is not TunnelPlan plan) return;
        var a = V(plan.X1, plan.Y1);
        var b = V(plan.X2, plan.Y2);
        var col = plan.Error == null && Game.CanAfford(plan.Cost) ? new Color(Game.IsRail(UI.Mode) ? "008d4f" : "1f4e9c") : new Color("c62828");
        DrawDashedLine(a, b, new Color(col, 0.75f), Mathf.Max(6f, 10 * px), 12 * px);
        string text = plan.Error ?? $"{Util.Dist(plan.Length)} · {Util.Money(plan.Cost)}";
        DrawSetTransform(b, 0, new Vector2(px, px));
        DrawStringOutline(_font, new Vector2(12, -10), text, HorizontalAlignment.Left, -1, 13, 4, Colors.White);
        DrawString(_font, new Vector2(12, -10), text, HorizontalAlignment.Left, -1, 13, col);
        DrawSetTransform(Vector2.Zero);
    }

    private void DrawTracks(float px)
    {
        if (Game.Tracks.Count == 0) return;
        if (_trackVersion != Game.TrackVersion)
        {
            _trackVersion = Game.TrackVersion;
            var g = Game.Map.Graph;
            _trackSegs = Game.Tracks.SelectMany(s => new[]
            {
                new Vector2((float)g.Vx[g.SegA[s]], (float)g.Vy[g.SegA[s]]),
                new Vector2((float)g.Vx[g.SegB[s]], (float)g.Vy[g.SegB[s]]),
            }).ToArray();
        }
        // Wider than a line stroke so the rails stay visible on both sides of a tram line
        DrawMultiline(_trackSegs, Palette.TrackOuter, Mathf.Max(7.5f, 12 * px));
        DrawMultiline(_trackSegs, Palette.TrackInner, Mathf.Max(4.5f, 8 * px));
    }

    private readonly LineBundles _bundles = new();

    /// <summary>Lines on shared streets run side by side: all casings first, then the colours on top.</summary>
    private void DrawLines(float px)
    {
        _bundles.Update(Game);
        float slot = 5.5f * px;
        var shapes = new List<(Vector2[] Pts, TransitLine Line)>();
        foreach (var line in Game.Lines.Values.OrderBy(l => l.Id))
            foreach (var run in line.Runs)
            {
                var pts = _bundles.Shifted(run, slot);
                if (pts.Length >= 2) shapes.Add((pts, line));
            }
        foreach (var (pts, line) in shapes)
            DrawPolyline(pts, Colors.White, (IsSel("line", line.Id) ? 10 : 7.5f) * px);
        foreach (var (pts, line) in shapes)
        {
            bool sel = IsSel("line", line.Id);
            bool active = double.IsFinite(Game.Headway(line));
            var color = new Color(line.Color) { A = active ? 1 : 0.55f };
            if (active) DrawPolyline(pts, color, (sel ? 6 : 4) * px);
            else for (int i = 0; i + 1 < pts.Length; i++) DrawDashedLine(pts[i], pts[i + 1], color, 4 * px, 8 * px);
        }
    }

    private void DrawVehicles(float px)
    {
        foreach (var v in Game.Vehicles.Values)
        {
            if (Game.InYard(v)) continue; // shown in the 3D yard, not on the map
            var def = v.Def;
            float len = Mathf.Max((float)def.Length, 24 * px), wid = Mathf.Max((float)def.Width, 13 * px);
            bool sel = IsSel("vehicle", v.Id);
            var line = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? l : null;
            bool lineColor = line != null && (v.State != VehicleState.Deadhead || v.DeadheadTo == DeadheadTarget.Rejoin);
            // Vehicles in service ride on their line's slot in a bundle of parallel lines
            var center = new Vector2((float)v.X, (float)v.Y);
            if (line != null && v.State is VehicleState.Moving or VehicleState.Dwell or VehicleState.Layover && v.RunIdx < line.Runs.Count &&
                _bundles.Runs.TryGetValue(line.Runs[v.RunIdx], out var bundle) && bundle.Off.Length > 0)
                center += bundle.Off[Math.Min(v.Seg, bundle.Off.Length - 1)] * 5.5f * px;
            DrawSetTransform(center, (float)v.Ang);
            var body = new Rect2(-len / 2, -wid / 2, len, wid);
            DrawRect(body, lineColor ? new Color(line!.Color) : new Color("7d8590"));
            // Load indicator: white fill grows with occupancy
            float pad = 1.6f * px, nose = 2.5f * px;
            float load = Math.Min((float)v.Passengers.Count / def.Capacity, 1);
            var inner = new Rect2(-len / 2 + pad, -wid / 2 + pad, len - 2 * pad - nose, wid - 2 * pad);
            DrawRect(inner, new Color(1, 1, 1, 0.35f));
            if (load > 0) DrawRect(new Rect2(inner.Position, new Vector2(inner.Size.X * load, inner.Size.Y)), Colors.White);
            // Windscreen marks the driving direction
            DrawRect(new Rect2(len / 2 - nose, -wid / 2, nose, wid), Palette.Ink);
            bool broken = v.State == VehicleState.Broken;
            DrawRect(body, sel ? Palette.Highlight : broken ? new Color("e53935") : Palette.Ink, false, (sel || broken ? 3 : 1.6f) * px);

            // Line number as a small dark badge (screen-sized, upright) once zoomed in a bit
            if (lineColor && Zoom > 0.45f)
            {
                DrawSetTransform(center, 0, new Vector2(px, px));
                var size = _font.GetStringSize(line!.Name, HorizontalAlignment.Left, -1, 11);
                float w = Mathf.Max(size.X + 6, 14);
                DrawRect(new Rect2(-w / 2, -7, w, 14), Palette.Ink);
                DrawString(_font, new Vector2(-size.X / 2, 4), line.Name, HorizontalAlignment.Left, -1, 11, Colors.White);
            }
        }
        DrawSetTransform(Vector2.Zero);
    }

    private void DrawDepots(float px)
    {
        foreach (var d in Game.Depots.Values)
        {
            bool sel = IsSel("depot", d.Id);
            var pos = new Vector2((float)d.Pos.X, (float)d.Pos.Y);
            DrawSetTransform(pos, 0, new Vector2(px, px)); // draw in screen pixels
            var r = new Rect2(-9, -9, 18, 18);
            DrawRect(r, d.Mode switch { ModeId.Tram => new Color("5b3f8c"), ModeId.Metro => new Color("1f4e9c"), ModeId.SBahn => new Color("008d4f"), ModeId.Train => new Color("37474f"), _ => new Color("2d3640") });
            DrawRect(r, sel ? Palette.Highlight : Colors.White, false, 2);
            DrawString(_font, new Vector2(-9, 5), d.Mode switch { ModeId.Tram => "T", ModeId.Metro => "U", ModeId.SBahn => "S", ModeId.Train => "R", _ => "B" }, HorizontalAlignment.Center, 18, 12, Colors.White);
        }
        DrawSetTransform(Vector2.Zero);
    }

    private void DrawStops(float px)
    {
        float stopR = Mathf.Clamp(Zoom * 6, 4, 8);
        var lineColor = new Dictionary<int, Color>();
        foreach (var l in Game.Lines.Values)
            foreach (int id in l.StopIds) lineColor.TryAdd(id, new Color(l.Color));

        foreach (var s in Game.Stops.Values)
        {
            bool sel = IsSel("stop", s.Id);
            DrawSetTransform(new Vector2((float)s.Pos.X, (float)s.Pos.Y), 0, new Vector2(px, px));
            bool hover = UI.HoverStop?.Id == s.Id;
            float r = stopR + (sel || hover ? 2 : 0);
            var ring = UI.DraftStops.Any(d => d.Id == s.Id) ? Palette.Ink
                : lineColor.TryGetValue(s.Id, out var c) ? c : new Color("555555");
            if (s.Portal)
            {
                // Exit to the region: dark tag with an arrow pointing out of the map, name always shown
                var dir = new Vector2((float)s.Pos.X, (float)s.Pos.Y).Normalized();
                DrawCircle(Vector2.Zero, r + 4, ring);
                DrawCircle(Vector2.Zero, r + 2, new Color("37474f"));
                DrawLine(-dir * (r - 1), dir * (r - 1), Colors.White, 2.5f);
                DrawLine(dir * (r - 1), dir * (r - 1) - dir.Rotated(0.6f) * 5, Colors.White, 2.5f);
                DrawLine(dir * (r - 1), dir * (r - 1) - dir.Rotated(-0.6f) * 5, Colors.White, 2.5f);
                var size = _font.GetStringSize(s.Name, HorizontalAlignment.Left, -1, 12);
                var at = new Vector2(-size.X / 2, -r - 10);
                DrawStringOutline(_font, at, s.Name, HorizontalAlignment.Left, -1, 12, 4, new Color(1, 1, 1, 0.9f));
                DrawString(_font, at, s.Name, HorizontalAlignment.Left, -1, 12, new Color("263238"));
            }
            else if (s.Mode == ModeId.Train)
            {
                // Regional station: dark slate circle with a white R
                float m = r + 2.5f;
                DrawCircle(Vector2.Zero, m + 1.5f, ring);
                DrawCircle(Vector2.Zero, m, new Color("37474f"));
                DrawString(_font, new Vector2(-m, 4.5f), "R", HorizontalAlignment.Center, 2 * m, 12, Colors.White);
            }
            else if (s.Mode == ModeId.SBahn)
            {
                // S-Bahnhof: green circle with a white S, ringed in the line color
                float m = r + 2;
                DrawCircle(Vector2.Zero, m + 1.5f, ring);
                DrawCircle(Vector2.Zero, m, new Color("008d4f"));
                DrawString(_font, new Vector2(-m, 4.5f), "S", HorizontalAlignment.Center, 2 * m, 12, Colors.White);
            }
            else if (s.Mode == ModeId.Metro)
            {
                // U-Bahnhof: blue square with a white U, ringed in the line color
                float m = r + 2;
                var rect = new Rect2(-m, -m, 2 * m, 2 * m);
                DrawRect(rect, new Color("1f4e9c"));
                DrawRect(rect, ring, false, sel ? 3.5f : 2.5f);
                DrawString(_font, new Vector2(-m, 4.5f), "U", HorizontalAlignment.Center, 2 * m, 12, Colors.White);
            }
            else if (s.Mode == ModeId.Tram)
            {
                var rect = new Rect2(-r, -r, 2 * r, 2 * r);
                DrawRect(rect, Colors.White);
                DrawRect(rect, ring, false, sel ? 3.5f : 2.5f);
            }
            else
            {
                DrawCircle(Vector2.Zero, r, Colors.White);
                DrawArc(Vector2.Zero, r, 0, Mathf.Tau, 24, ring, sel ? 3.5f : 2.5f);
            }
            int n = s.Waiting.Count;
            if (n > 0 && Zoom > 0.25f)
            {
                string label = n.ToString();
                float w = _font.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X + 8;
                var badge = n > 60 ? new Color("c62828") : n > 25 ? new Color("ef6c00") : new Color("37474f");
                DrawRect(new Rect2(r + 3, -8, w, 16), badge);
                DrawString(_font, new Vector2(r + 7, 4), label, HorizontalAlignment.Left, -1, 11, Colors.White);
            }
            if (Zoom > 1.1f || sel || hover)
            {
                var size = _font.GetStringSize(s.Name, HorizontalAlignment.Left, -1, 11);
                var at = new Vector2(-size.X / 2, r + 14);
                DrawStringOutline(_font, at, s.Name, HorizontalAlignment.Left, -1, 11, 4, new Color(1, 1, 1, 0.9f));
                DrawString(_font, at, s.Name, HorizontalAlignment.Left, -1, 11, new Color("222222"));
            }
        }
        DrawSetTransform(Vector2.Zero);
    }
}
