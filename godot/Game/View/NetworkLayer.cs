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

    private readonly Dictionary<Run, Vector2[]> _runCache = new();
    private Vector2[] _trackSegs = Array.Empty<Vector2>();
    private int _trackVersion = -1;
    private Font _font = null!;

    public override void _Ready() => _font = ThemeDB.FallbackFont;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        float px = 1 / Zoom; // one screen pixel in world meters
        DrawTracks(px);
        DrawTrackPlan(px);
        DrawLines(px);
        DrawDraft(px);
        DrawVehicles(px);
        DrawDepots(px);
        DrawStops(px);
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

    private Vector2[] RunPoints(Run run)
    {
        if (_runCache.TryGetValue(run, out var pts)) return pts;
        pts = new Vector2[run.Path.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2((float)run.Path.Xs[i], (float)run.Path.Ys[i]);
        if (_runCache.Count > 500) _runCache.Clear(); // runs are replaced when lines are edited
        _runCache[run] = pts;
        return pts;
    }

    private void DrawLines(float px)
    {
        foreach (var line in Game.Lines.Values)
        {
            bool sel = IsSel("line", line.Id);
            bool active = double.IsFinite(Game.Headway(line));
            var color = new Color(line.Color) { A = active ? 1 : 0.55f };
            foreach (var run in line.Runs)
            {
                var pts = RunPoints(run);
                if (pts.Length < 2) continue;
                DrawPolyline(pts, Colors.White, (sel ? 10 : 7) * px);
                if (active) DrawPolyline(pts, color, (sel ? 6 : 4) * px);
                else for (int i = 0; i + 1 < pts.Length; i++) DrawDashedLine(pts[i], pts[i + 1], color, 4 * px, 8 * px);
            }
        }
    }

    private void DrawVehicles(float px)
    {
        foreach (var v in Game.Vehicles.Values)
        {
            if (v.State == VehicleState.Depot) continue;
            var def = v.Def;
            float len = Mathf.Max((float)def.Length, 15 * px), wid = Mathf.Max((float)def.Width, 7 * px);
            bool sel = IsSel("vehicle", v.Id);
            var line = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? l : null;
            bool lineColor = line != null && (v.State != VehicleState.Deadhead || v.DeadheadTo == DeadheadTarget.Rejoin);
            DrawSetTransform(new Vector2((float)v.X, (float)v.Y), (float)v.Ang);
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
            DrawRect(r, d.Mode == ModeId.Tram ? new Color("5b3f8c") : new Color("2d3640"));
            DrawRect(r, sel ? Palette.Highlight : Colors.White, false, 2);
            DrawString(_font, new Vector2(-9, 5), d.Mode == ModeId.Tram ? "T" : "B", HorizontalAlignment.Center, 18, 12, Colors.White);
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
            if (s.Mode == ModeId.Tram)
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
