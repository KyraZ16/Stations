using Godot;
using FileAccess = Godot.FileAccess;
using Stations.Core;
using Stations.Core.Sim;
using Stations.UI;
using Stations.View;

namespace Stations;

/// <summary>
/// A running game: map view, tool bar, side panel, top bar, notifications and shortcuts.
/// Advances the simulation every frame.
/// </summary>
public partial class GameView : Node
{
    public Game Game = null!;
    public event Action? MenuRequested;

    private MapView _map = null!;
    private Interaction _ui = null!;
    private Label _hint = null!;
    private VBoxContainer _toasts = null!;
    private ConfirmationDialog _confirm = null!;
    private Action? _onConfirm;
    private double _autosave;
    private CanvasLayer _uiLayer = null!;
    private DepotView.DepotEditor? _depotEditor;

    /// <summary>Tool state (for developer automation).</summary>
    public Interaction Interaction => _ui;

    public override void _Ready()
    {
        _ui = new Interaction { Game = Game };
        _map = new MapView { Game = Game, UI = _ui };
        _ui.Map = _map;
        AddChild(_map);
        AddChild(_ui);
        _map.Clicked += _ui.Click;
        _map.Hovered += _ui.Hover;
        _ui.Notify += Toast;
        _ui.ConfirmRequested += Confirm;
        _ui.StateChanged += OnStateChanged;
        _ui.EditDepotRequested += OpenDepotEditor;

        var layer = new CanvasLayer();
        _uiLayer = layer;
        AddChild(layer);
        // One root control carries the theme; it lets mouse events through to the map.
        var ui = new Control { Theme = Ui.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(ui);
        ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var top = new TopBar { Game = Game };
        top.SaveRequested += () => Save(manual: true);
        top.MenuRequested += () => MenuRequested?.Invoke();
        top.ResearchRequested += () => _ui.Select(new Selection("research", 0));
        ui.AddChild(top);
        ui.AddChild(new ToolBar { UI = _ui });
        ui.AddChild(new SidePanel { UI = _ui });
        ui.AddChild(new MiniMap { Game = Game, Map = _map });

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

        _confirm = new ConfirmationDialog { Title = "Bestätigen", OkButtonText = "Ja", CancelButtonText = "Abbrechen" };
        _confirm.Confirmed += () => { _onConfirm?.Invoke(); _onConfirm = null; };
        ui.AddChild(_confirm);

        Game.Toast += Toast;
        OnStateChanged();
    }

    public override void _ExitTree() => Game.Toast -= Toast;

    private void OnStateChanged()
    {
        _hint.Text = Interaction.Hint(_ui.Tool, _ui.Mode);
        _map.CoverageVisible = _ui.Coverage;
    }

    private void Confirm(string question, Action yes)
    {
        _onConfirm = yes;
        _confirm.DialogText = question;
        _confirm.PopupCentered(new Vector2I(420, 0));
    }

    /// <summary>Switch from the map to the 3D yard editor of a depot (the simulation keeps running).</summary>
    public DepotView.DepotEditor? DepotEditor => _depotEditor;

    public void OpenDepotEditor(int depotId)
    {
        if (_depotEditor != null || !Game.Depots.TryGetValue(depotId, out var depot)) return;
        _map.Visible = false;
        _uiLayer.Visible = false;
        _ui.ProcessMode = ProcessModeEnum.Disabled; // no map shortcuts while editing
        _depotEditor = new DepotView.DepotEditor { Game = Game, Depot = depot };
        _depotEditor.Closed += CloseDepotEditor;
        AddChild(_depotEditor);
    }

    private void CloseDepotEditor()
    {
        if (_depotEditor == null) return;
        _depotEditor.QueueFree();
        _depotEditor = null;
        _map.Visible = true;
        _uiLayer.Visible = true;
        _ui.ProcessMode = ProcessModeEnum.Inherit;
        _map.MakeCameraCurrent();
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
                break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
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
        // Keep at most four messages on screen (fast-forward can produce many at once)
        while (_toasts.GetChildCount() > 4) _toasts.GetChild(0).Free();
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
