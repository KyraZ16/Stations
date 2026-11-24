using Godot;
using FileAccess = Godot.FileAccess;
using Stations.Core;
using Stations.Core.Sim;
using Stations.UI;
using Stations.View;

namespace Stations;

/// <summary>
/// A running game: map view, top bar, notifications and keyboard shortcuts.
/// Advances the simulation every frame.
/// </summary>
public partial class GameView : Node
{
    public Game Game = null!;
    public event Action? MenuRequested;

    private MapView _map = null!;
    private Label _hint = null!;
    private VBoxContainer _toasts = null!;
    private double _autosave;

    public override void _Ready()
    {
        _map = new MapView { Game = Game };
        AddChild(_map);
        _map.Clicked += OnMapClicked;

        var layer = new CanvasLayer();
        AddChild(layer);
        // One root control carries the theme; it lets mouse events through to the map.
        var ui = new Control { Theme = Ui.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(ui);
        ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var top = new TopBar { Game = Game };
        top.SaveRequested += () => Save(manual: true);
        top.MenuRequested += () => MenuRequested?.Invoke();
        ui.AddChild(top);

        _hint = Ui.Label("", 13, Colors.White);
        var hintBox = new PanelContainer();
        hintBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.14f, 0.16f, 0.88f),
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16, CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        hintBox.MouseFilter = Control.MouseFilterEnum.Ignore;
        hintBox.AddChild(_hint);
        hintBox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        hintBox.GrowHorizontal = Control.GrowDirection.Both;
        hintBox.OffsetBottom = -14;
        hintBox.GrowVertical = Control.GrowDirection.Begin;
        ui.AddChild(hintBox);
        SetHint(Game.Lines.Count == 0
            ? "Mausrad: Zoomen · Ziehen: Verschieben · F9: Demo-Netz bauen (Werkzeuge folgen) · Leertaste: Pause"
            : "Mausrad: Zoomen · Ziehen: Verschieben · Klick: Auswählen · Leertaste: Pause · F5: Speichern");

        _toasts = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _toasts.MouseFilter = Control.MouseFilterEnum.Ignore;
        _toasts.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toasts.GrowHorizontal = Control.GrowDirection.Both;
        _toasts.GrowVertical = Control.GrowDirection.Begin;
        _toasts.OffsetBottom = -60;
        ui.AddChild(_toasts);

        var attribution = Ui.Label("© OpenStreetMap-Mitwirkende", 11, Ui.Muted);
        attribution.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        attribution.GrowHorizontal = Control.GrowDirection.Begin;
        attribution.GrowVertical = Control.GrowDirection.Begin;
        attribution.OffsetRight = -8;
        attribution.OffsetBottom = -4;
        ui.AddChild(attribution);

        Game.Toast += Toast;
        Game.Changed += OnGameChanged;
    }

    private bool _hasNetworkHint;

    private void OnGameChanged()
    {
        if (_hasNetworkHint || Game.Lines.Count == 0) return;
        _hasNetworkHint = true;
        SetHint("Mausrad: Zoomen · Ziehen: Verschieben · Klick: Auswählen · Leertaste: Pause · F5: Speichern");
    }

    public override void _ExitTree()
    {
        Game.Toast -= Toast;
        Game.Changed -= OnGameChanged;
    }

    /// <summary>Zoom the map by a factor around its center (developer option).</summary>
    public void ZoomBy(float factor) => _map.ZoomBy(factor);

    public override void _Process(double delta)
    {
        Game.Update(delta);
        _autosave += delta;
        if (_autosave > 120)
        {
            _autosave = 0;
            Save(manual: false);
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Space: Game.Speed = Game.Speed == 0 ? 1 : 0; break;
            case Key.Key1: Game.Speed = 1; break;
            case Key.Key2: Game.Speed = 2; break;
            case Key.Key3: Game.Speed = 3; break;
            case Key.F5: Save(manual: true); break;
            case Key.F9:
                DemoNetwork.Build(Game);
                Toast($"Demo-Netz gebaut: {Game.Lines.Count} Linien, {Game.Vehicles.Count} Fahrzeuge", false);
                SetHint("Mausrad: Zoomen · Ziehen: Verschieben · Klick: Auswählen · Leertaste: Pause · F5: Speichern");
                break;
            case Key.Escape: _map.Network.Selected = null; SetHint(""); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void SetHint(string text)
    {
        _hint.Text = text;
        _hint.GetParent<Control>().Visible = text != "";
    }

    /// <summary>Simple selection until the panels exist: highlight and describe what was clicked.</summary>
    private void OnMapClicked(Vector2 world, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        double r = 12 / _map.Zoom;
        var v = Game.Vehicles.Values.Where(x => x.State != VehicleState.Depot)
            .OrderBy(x => Util.Hypot(x.X - world.X, x.Y - world.Y)).FirstOrDefault();
        if (v != null && Util.Hypot(v.X - world.X, v.Y - world.Y) < r)
        {
            _map.Network.Selected = ("vehicle", v.Id);
            var line = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? $"Linie {l.Name}" : "ohne Linie";
            SetHint($"{v.Name} · {line} · {v.Passengers.Count}/{Modes.Get(v.Mode).Vehicle.Capacity} Fahrgäste · {Math.Round(v.V * 3.6)} km/h");
            return;
        }
        var stop = Game.StopAt(world.X, world.Y, r);
        if (stop != null)
        {
            _map.Network.Selected = ("stop", stop.Id);
            var lines = string.Join(", ", Game.LinesAtStop(stop.Id).Select(x => x.Name));
            SetHint($"{stop.Name} · Linien {(lines == "" ? "–" : lines)} · {stop.Waiting.Count} wartend · {stop.BoardedToday} Einstiege heute");
            return;
        }
        var depot = Game.Depots.Values.FirstOrDefault(d => Util.Hypot(d.Pos.X - world.X, d.Pos.Y - world.Y) < r);
        if (depot != null)
        {
            _map.Network.Selected = ("depot", depot.Id);
            SetHint($"{depot.Name} · {Game.Vehicles.Values.Count(x => x.DepotId == depot.Id)} Fahrzeuge · {Game.DriversOf(depot.Id).Count} Fahrer");
            return;
        }
        _map.Network.Selected = null;
        SetHint("");
    }

    public void Toast(string msg, bool error)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = error ? Ui.Danger : new Color("1f2328"),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        box.AddChild(Ui.Label(msg, 13, Colors.White));
        _toasts.AddChild(box);
        GetTree().CreateTimer(error ? 4 : 2.6).Timeout += () => box.QueueFree();
    }

    public void Save(bool manual)
    {
        try
        {
            using var f = FileAccess.Open(SaveStore.Path, FileAccess.ModeFlags.Write);
            f.StoreString(Game.Serialize().ToJson());
            if (manual) Toast("Spielstand gespeichert", false);
        }
        catch (Exception ex)
        {
            Toast($"Speichern fehlgeschlagen: {ex.Message}", true);
        }
    }
}

/// <summary>Location of the save game and OSM cache in Godot's user data folder.</summary>
public static class SaveStore
{
    public const string Path = "user://save.json";
    public static string CacheDir => ProjectSettings.GlobalizePath("user://osm-cache");

    public static SaveData? Load()
    {
        if (!FileAccess.FileExists(Path)) return null;
        try
        {
            using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
            return SaveData.FromJson(f.GetAsText());
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Spielstand nicht lesbar: {ex.Message}");
            return null;
        }
    }
}
