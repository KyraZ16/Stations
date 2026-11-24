using Godot;
using Stations.Core;
using Stations.Core.Map;
using Stations.Core.Sim;
using Stations.View;

namespace Stations;

public enum Tool { Select, Track, Stop, Line, Depot, Bulldoze, Edit }

/// <summary>What the player has selected on the map.</summary>
public readonly record struct Selection(string Kind, int Id);

/// <summary>
/// Player interaction with the map: current tool and mode, selection, line drafts,
/// line editing and track planning. UI and rendering read this state; it changes the game.
/// </summary>
public partial class Interaction : Node
{
    public Game Game = null!;
    public MapView Map = null!;

    public Tool Tool { get; private set; } = Tool.Select;
    public Tier Tier { get; private set; } = Tier.Local;
    public ModeId Mode { get; private set; } = ModeId.Bus;
    public Selection? Selected { get; private set; }
    public bool Coverage { get; private set; }

    // Line drawing
    public readonly List<Stop> DraftStops = new();
    public readonly List<RouteResult> DraftPaths = new();
    public bool DraftCircular;

    // Line editing
    public int? EditLineId { get; private set; }

    // Track planning
    public RoadPos? TrackStart { get; private set; }
    public TrackPlan? TrackPlan { get; private set; }
    private string _planKey = "";

    // Hover feedback
    public RoadPos? HoverPos { get; private set; }
    public Stop? HoverStop { get; private set; }
    public Vector2? MouseWorld { get; private set; }

    /// <summary>Tool, mode, selection or draft changed: toolbar, panel and hint should update.</summary>
    public event Action? StateChanged;
    public event Action<string, bool>? Notify;
    /// <summary>The player wants to open the 3D yard editor of a depot.</summary>
    public event Action<int>? EditDepotRequested;
    public void EditDepot(int depotId) => EditDepotRequested?.Invoke(depotId);

    /// <summary>Ask the player to confirm a destructive action.</summary>
    public event Action<string, Action>? ConfirmRequested;

    public static readonly (Tool Tool, string Name, string Icon, Key Key, bool TracksOnly)[] Tools =
    {
        (Tool.Select, "Auswählen", "↖", Key.V, false),
        (Tool.Track, "Gleise bauen", "═", Key.G, true),
        (Tool.Stop, "Haltestelle", "◉", Key.H, false),
        (Tool.Line, "Linie zeichnen", "〰", Key.L, false),
        (Tool.Depot, "Betriebshof", "▣", Key.B, false),
        (Tool.Bulldoze, "Abreißen", "✕", Key.X, false),
    };

    public static string Hint(Tool t) => t switch
    {
        Tool.Select => "Klick: Auswählen · Ziehen: Verschieben · Mausrad: Zoomen · Leertaste: Pause · F5: Speichern",
        Tool.Stop => "Klicke auf eine Straße, um eine Haltestelle zu setzen · Rechtsklick: zurück",
        Tool.Line => "Haltestellen oder Straßen nacheinander anklicken · Enter: fertig · Rechtsklick: letzte entfernen · erste Haltestelle erneut: Ringlinie",
        Tool.Depot => "Klicke auf eine Straße, um einen Betriebshof zu bauen",
        Tool.Bulldoze => "Klicke auf Haltestelle, Linie, Betriebshof oder Gleis, um es zu entfernen",
        Tool.Edit => "Klick auf Straße/Haltestelle fügt sie passend ein · Klick auf eine Haltestelle der Linie entfernt sie · Enter: fertig",
        Tool.Track => "Start- und Zielpunkt auf Straßen klicken – das Gleis folgt den Straßen · weitere Klicks verlängern · Rechtsklick/Esc: neu ansetzen",
        _ => "",
    };

    private const string NeedsTrack = "Hier liegen keine Gleise – baue zuerst Gleise (G)";

    public bool ModeUsesTracks => Game.UsesTracks(Mode);

    private void Changed() => StateChanged?.Invoke();
    private void Toast(string msg, bool error = false) => Notify?.Invoke(msg, error);

    /// <summary>Show a message to the player (used by UI panels).</summary>
    public void EmitNotify(string msg, bool error) => Notify?.Invoke(msg, error);
    private double SnapDist => Math.Max(25, 20 / Map.Zoom);
    private double PickRadius => 12 / Map.Zoom;

    // ---------------------------------------------------------------- state changes

    public void SetTool(Tool t)
    {
        if (Tool == Tool.Line && t != Tool.Line && DraftStops.Count > 0) CancelDraft();
        if (t != Tool.Edit) EditLineId = null;
        if (t == Tool.Track && !ModeUsesTracks) t = Tool.Select;
        ResetTrack(notify: false);
        Tool = t;
        HoverPos = null;
        HoverStop = null;
        Changed();
    }

    public void SetTier(Tier tier)
    {
        Tier = tier;
        var first = Modes.All.Values.FirstOrDefault(m => m.Tier == tier && m.Available);
        if (first != null) SwitchMode(first.Id);
        Changed();
    }

    public void SwitchMode(ModeId mode)
    {
        var def = Modes.Get(mode);
        if (!def.Available)
        {
            Toast($"{def.Name} ist noch in Planung – kommt in einer späteren Version");
            return;
        }
        if (mode == Mode) return;
        Mode = mode;
        if (DraftStops.Count > 0) CancelDraft();
        // Keep the building tool, but leave tools the new mode does not have
        SetTool(Tool is Tool.Track or Tool.Edit ? Tool.Select : Tool);
        if (ModeUsesTracks && Game.Tracks.Count == 0)
            Toast("Trams brauchen Gleise: baue sie mit „Gleise bauen“ (G) auf Straßen");
    }

    public void Select(Selection? s)
    {
        Selected = s;
        Changed();
    }

    public void ToggleCoverage()
    {
        Coverage = !Coverage;
        Changed();
    }

    /// <summary>Mode the current tool builds for (the edited line's mode while editing).</summary>
    private ModeId ToolMode =>
        Tool == Tool.Edit && EditLineId is int id && Game.Lines.TryGetValue(id, out var l) ? l.Mode : Mode;

    // ---------------------------------------------------------------- input from the map

    public void Hover(Vector2 world)
    {
        MouseWorld = world;
        HoverStop = null;
        HoverPos = null;
        if (Tool == Tool.Select) return;
        if (Tool == Tool.Track)
        {
            HoverPos = Game.Map.Graph.Snap(world.X, world.Y, SnapDist);
            UpdateTrackPlan();
            return;
        }
        var mode = ToolMode;
        HoverStop = Game.StopAt(world.X, world.Y, PickRadius, Tool == Tool.Bulldoze ? null : mode);
        if (Tool is Tool.Stop or Tool.Line or Tool.Edit or Tool.Depot)
            HoverPos = Game.SnapFor(mode, world.X, world.Y, SnapDist);
    }

    public void Click(Vector2 w, MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            RightClick();
            return;
        }
        if (button != MouseButton.Left) return;
        switch (Tool)
        {
            case Tool.Select:
                Select(Pick(w.X, w.Y));
                break;
            case Tool.Stop:
                if (Game.StopAt(w.X, w.Y, PickRadius, Mode) != null)
                {
                    Select(Pick(w.X, w.Y));
                    return;
                }
                if (SnapBuild(Mode, w.X, w.Y) is RoadPos sp && Game.AddStop(sp, Mode) is Stop s) Select(new("stop", s.Id));
                break;
            case Tool.Depot:
                if (SnapBuild(Mode, w.X, w.Y) is RoadPos dp && Game.AddDepot(dp, Mode) is Depot d)
                {
                    SetTool(Tool.Select);
                    Select(new("depot", d.Id));
                }
                break;
            case Tool.Line:
                LineClick(w.X, w.Y);
                break;
            case Tool.Edit:
                EditClick(w.X, w.Y);
                break;
            case Tool.Track:
                TrackClick(w.X, w.Y);
                break;
            case Tool.Bulldoze:
                if (Pick(w.X, w.Y, vehicles: false) is Selection p)
                {
                    Selected = p;
                    DeleteSelected();
                }
                else
                {
                    Game.RemoveTrackAt(w.X, w.Y, 10 / Map.Zoom);
                }
                break;
        }
    }

    private void RightClick()
    {
        if (Tool == Tool.Track && TrackStart != null) ResetTrack();
        else if (Tool == Tool.Line && DraftStops.Count > 0)
        {
            DraftStops.RemoveAt(DraftStops.Count - 1);
            if (DraftPaths.Count > 0) DraftPaths.RemoveAt(DraftPaths.Count - 1);
            DraftCircular = false;
            Changed();
        }
        else if (Tool != Tool.Select) SetTool(Tool.Select);
    }

    /// <summary>Handle Escape: step back one level (draft → tool → selection).</summary>
    public void Escape()
    {
        if (DraftStops.Count > 0) CancelDraft();
        else if (Tool == Tool.Track && TrackStart != null) ResetTrack();
        else if (Tool != Tool.Select) SetTool(Tool.Select);
        else Select(null);
    }

    public void Enter()
    {
        if (Tool == Tool.Line) FinishDraft();
        else if (Tool == Tool.Edit) SetTool(Tool.Select);
    }

    private RoadPos? SnapBuild(ModeId mode, double x, double y)
    {
        var pos = Game.SnapFor(mode, x, y, SnapDist);
        if (pos == null) Toast(Game.UsesTracks(mode) ? NeedsTrack : "Hier ist keine befahrbare Straße", true);
        return pos;
    }

    public Selection? Pick(double x, double y, bool vehicles = true)
    {
        double r = PickRadius;
        if (vehicles)
        {
            var v = Game.Vehicles.Values.Where(v => v.State != VehicleState.Depot)
                .MinBy(v => Util.Hypot(v.X - x, v.Y - y));
            if (v != null && Util.Hypot(v.X - x, v.Y - y) < r) return new("vehicle", v.Id);
        }
        if (Game.StopAt(x, y, r) is Stop s) return new("stop", s.Id);
        var d = Game.Depots.Values.FirstOrDefault(d => Util.Hypot(d.Pos.X - x, d.Pos.Y - y) < r);
        if (d != null) return new("depot", d.Id);
        double lr = 7 / Map.Zoom;
        foreach (var l in Game.Lines.Values)
            foreach (var run in l.Runs)
            {
                var p = run.Path;
                for (int i = 0; i + 1 < p.Count; i++)
                    if (Util.DistToSegment(x, y, p.Xs[i], p.Ys[i], p.Xs[i + 1], p.Ys[i + 1]).D < lr) return new("line", l.Id);
            }
        return null;
    }

    // ---------------------------------------------------------------- line drawing

    private void LineClick(double x, double y)
    {
        var stop = Game.StopAt(x, y, PickRadius, Mode);
        if (stop == null)
        {
            if (SnapBuild(Mode, x, y) is not RoadPos pos) return;
            stop = Game.AddStop(pos, Mode);
            if (stop == null) return;
        }
        if (DraftStops.Count > 0 && DraftStops[^1].Id == stop.Id) return;
        if (DraftStops.Count >= 2 && DraftStops[0].Id == stop.Id)
        {
            if (Game.PreviewRoute(DraftStops[^1], stop) == null)
            {
                Toast("Keine befahrbare Verbindung zur ersten Haltestelle", true);
                return;
            }
            DraftCircular = true;
            FinishDraft();
            return;
        }
        if (DraftStops.Count > 0)
        {
            var r = Game.PreviewRoute(DraftStops[^1], stop);
            if (r == null)
            {
                Toast($"Keine befahrbare Verbindung nach „{stop.Name}“", true);
                return;
            }
            DraftPaths.Add(r);
        }
        DraftStops.Add(stop);
        Changed();
    }

    public void FinishDraft()
    {
        if (DraftStops.Count < 2)
        {
            Toast("Eine Linie braucht mindestens zwei Haltestellen", true);
            return;
        }
        var line = Game.CreateLine(DraftStops.Select(s => s.Id).ToList(), DraftCircular, Mode);
        if (line == null) return;
        DraftStops.Clear();
        DraftPaths.Clear();
        DraftCircular = false;
        SetTool(Tool.Select);
        Select(new("line", line.Id));
        bool tram = line.Mode == ModeId.Tram;
        if (Game.Depots.Values.Any(d => d.Mode == line.Mode))
        {
            // Equip the line for its default timetable: vehicles for the peak, drivers for the shifts.
            Game.AssignMissing(line);
            int hired = Game.HireMissingDrivers();
            int n = Game.VehiclesOf(line).Count;
            string word = tram ? (n == 1 ? "Tram" : "Trams") : n == 1 ? "Bus" : "Busse";
            Toast($"Linie {line.Name} erstellt – {n} {word} zugewiesen" + (hired > 0 ? $", {hired} Fahrer eingestellt" : ""));
        }
        else
        {
            Toast($"Linie {line.Name} erstellt – baue einen {(tram ? "Tram-Betriebshof" : "Betriebshof")}, um Fahrzeuge einzusetzen");
        }
    }

    public void CancelDraft()
    {
        DraftStops.Clear();
        DraftPaths.Clear();
        DraftCircular = false;
        Changed();
    }

    public void SetDraftCircular(bool v)
    {
        DraftCircular = v;
        Changed();
    }

    // ---------------------------------------------------------------- line editing

    public void StartEdit(int lineId)
    {
        if (!Game.Lines.ContainsKey(lineId)) return;
        SetTool(Tool.Edit);
        EditLineId = lineId;
        Selected = new("line", lineId);
        Changed();
    }

    private void EditClick(double x, double y)
    {
        if (EditLineId is not int id || !Game.Lines.TryGetValue(id, out var line))
        {
            SetTool(Tool.Select);
            return;
        }
        var stop = Game.StopAt(x, y, PickRadius, line.Mode);
        if (stop != null && line.StopIds.Contains(stop.Id))
        {
            if (line.StopIds.Count <= 2)
            {
                Toast("Eine Linie braucht mindestens zwei Haltestellen", true);
                return;
            }
            int sid = stop.Id;
            Game.SetLineStops(line, line.StopIds.Where(s => s != sid).ToList());
            Changed();
            return;
        }
        bool created = false;
        if (stop == null)
        {
            if (SnapBuild(line.Mode, x, y) is not RoadPos pos) return;
            stop = Game.AddStop(pos, line.Mode);
            if (stop == null) return;
            created = true;
        }
        bool ok = Game.SetLineStops(line, InsertBest(line.StopIds, line.Circular, stop));
        if (!ok && created) Game.RemoveStop(stop.Id);
        Changed();
    }

    /// <summary>Insert a stop where it adds the smallest detour (straight-line distances).</summary>
    private List<int> InsertBest(List<int> ids, bool circular, Stop stop)
    {
        double D(RoadPos a, RoadPos b) => Util.Hypot(a.X - b.X, a.Y - b.Y);
        RoadPos P(int id) => Game.Stops[id].Pos;
        int n = ids.Count;
        double best = double.PositiveInfinity;
        int at = n;
        for (int i = 0; i <= n; i++)
        {
            double cost;
            if (i > 0 && i < n) cost = D(P(ids[i - 1]), stop.Pos) + D(stop.Pos, P(ids[i])) - D(P(ids[i - 1]), P(ids[i]));
            else if (circular) cost = D(P(ids[n - 1]), stop.Pos) + D(stop.Pos, P(ids[0])) - D(P(ids[n - 1]), P(ids[0]));
            else cost = D(stop.Pos, P(ids[i == 0 ? 0 : n - 1]));
            if (cost < best)
            {
                best = cost;
                at = i == 0 && circular ? n : i;
            }
        }
        var outIds = ids.ToList();
        outIds.Insert(at, stop.Id);
        return outIds;
    }

    /// <summary>Move (dir = ±1) or remove (dir = 0) the stop at index i of the edited line.</summary>
    public void EditStopAt(int i, int dir)
    {
        if (EditLineId is not int id || !Game.Lines.TryGetValue(id, out var line)) return;
        var ids = line.StopIds.ToList();
        if (dir == 0) ids.RemoveAt(i);
        else
        {
            int j = i + dir;
            if (j < 0 || j >= ids.Count) return;
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        Game.SetLineStops(line, ids);
        Changed();
    }

    // ---------------------------------------------------------------- tracks

    private void UpdateTrackPlan()
    {
        if (TrackStart is not RoadPos a || HoverPos is not RoadPos b)
        {
            if (TrackPlan != null) Changed();
            TrackPlan = null;
            _planKey = "";
            return;
        }
        string key = $"{b.Seg}:{b.T:F2}";
        if (key == _planKey) return;
        _planKey = key;
        TrackPlan = Game.PlanTrack(a, b);
        Changed();
    }

    public void ResetTrack(bool notify = true)
    {
        TrackStart = null;
        TrackPlan = null;
        _planKey = "";
        if (notify) Changed();
    }

    private void TrackClick(double x, double y)
    {
        if (Game.Map.Graph.Snap(x, y, SnapDist) is not RoadPos pos)
        {
            Toast("Gleise können nur auf Straßen verlegt werden", true);
            return;
        }
        if (TrackStart is not RoadPos start)
        {
            TrackStart = pos;
            Changed();
            return;
        }
        if (Game.BuildTrack(start, pos))
        {
            TrackStart = pos;
            TrackPlan = null;
            _planKey = "";
            Changed();
        }
    }

    // ---------------------------------------------------------------- deleting

    public void DeleteSelected()
    {
        if (Selected is not Selection s) return;
        switch (s.Kind)
        {
            case "stop":
            {
                if (!Game.Stops.TryGetValue(s.Id, out var stop)) return;
                int used = Game.LinesAtStop(s.Id).Count;
                if (used > 0)
                {
                    ConfirmRequested?.Invoke($"„{stop.Name}“ wird von {used} Linie(n) bedient. Trotzdem abreißen?", () =>
                    {
                        Game.RemoveStop(s.Id);
                        Select(null);
                    });
                    return;
                }
                Game.RemoveStop(s.Id);
                break;
            }
            case "line":
            {
                if (!Game.Lines.TryGetValue(s.Id, out var line)) return;
                ConfirmRequested?.Invoke($"Linie {line.Name} löschen? Die Fahrzeuge fahren zurück in den Betriebshof.", () =>
                {
                    Game.DeleteLine(s.Id);
                    Select(null);
                });
                return;
            }
            case "depot":
                Game.RemoveDepot(s.Id);
                if (Game.Depots.ContainsKey(s.Id)) return;
                break;
            default:
                return;
        }
        Select(null);
    }

    // ---------------------------------------------------------------- keyboard

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Escape: Escape(); break;
            case Key.Enter or Key.KpEnter: Enter(); break;
            case Key.C: ToggleCoverage(); break;
            case Key.Delete: DeleteSelected(); break;
            default:
                var t = Tools.FirstOrDefault(x => x.Key == key.Keycode && (!x.TracksOnly || ModeUsesTracks));
                if (t.Name == null) return;
                SetTool(t.Tool);
                break;
        }
        GetViewport().SetInputAsHandled();
    }
}
