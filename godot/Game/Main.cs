using Godot;
using Stations.Core.Map;
using Stations.Core.Sim;
using Stations.UI;

namespace Stations;

/// <summary>Entry scene: start screen → load OSM data → game view, and back via the menu.</summary>
public partial class Main : Node
{
    private StartScreen? _start;
    private GameView? _view;

    /// <summary>
    /// Developer options after "--" on the command line:
    /// --autostart (Hamburg sandbox), --demo (build demo network, skip to 08:00),
    /// --shot=path.png (save a screenshot and quit), --zoom=factor (zoom in from the overview).
    /// </summary>
    private readonly Dictionary<string, string> _dev = OS.GetCmdlineUserArgs()
        .Select(a => a.TrimStart('-').Split('=', 2))
        .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(new Color("d6d1c7"));
        GetTree().Root.Theme = Ui.Theme;
        ShowStart();
        if (_dev.ContainsKey("autostart"))
            _ = LoadGame(new StartScreen.Choice(new Core.Area("Hamburg Innenstadt", 53.5530, 9.9930, 2500), true, false), null);
        else if (_dev.TryGetValue("shot", out var shot))
            TakeShot(shot);
    }

    private async void TakeShot(string path)
    {
        for (int i = 0; i < 20; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GetTree().Quit();
    }

    private async void RunDevOptions(Game game)
    {
        if (_dev.ContainsKey("demo"))
        {
            DemoNetwork.Build(game);
            game.Advance(3 * 3600); // 05:00 → 08:00, morning peak
        }
        if (_dev.ContainsKey("tooltest")) ToolTest(game, _view!.Interaction);
        if (_dev.TryGetValue("days", out var dd) && int.TryParse(dd, out int days)) game.Advance(days * 86400.0);
        if (_dev.TryGetValue("time", out var hh) && double.TryParse(hh, System.Globalization.CultureInfo.InvariantCulture, out double hour))
        {
            // fast-forward to the given clock hour (next occurrence)
            double target = Math.Floor(game.Time / 86400) * 86400 + hour * 3600;
            if (target <= game.Time) target += 86400;
            game.Advance(target - game.Time);
        }
        if (_dev.ContainsKey("depot") && game.Depots.Count > 0)
        {
            if (_dev["depot"] == "edit")
            {
                // sample edits: a connected wash bay and a workshop without access
                var d0 = game.Depots.Values.First();
                game.PlaceModule(d0, Core.Sim.ModuleType.Road, 16, 8, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.Wash, 17, 8, 1);
                game.PlaceModule(d0, Core.Sim.ModuleType.Workshop, 2, 9, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.LongBusSpot, 18, 0, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.Charger, 17, 6, 0);
            }
            if (_dev["depot"] == "tram") _view!.OpenDepotEditor(game.Depots.Values.First(d => d.Mode == Core.ModeId.Tram).Id);
            else _view!.OpenDepotEditor(game.Depots.Values.First().Id);
        }
        if (_dev.TryGetValue("zoom", out var z) && float.TryParse(z, System.Globalization.CultureInfo.InvariantCulture, out float zf))
            _view!.ZoomBy(zf);
        if (_dev.ContainsKey("debugui"))
        {
            for (int i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            void Dump(Node n, string indent)
            {
                if (n is Control c && c.GetCombinedMinimumSize().X > 360)
                    GD.Print($"{indent}{c.GetType().Name} '{c.Name}' min={c.GetCombinedMinimumSize()} size={c.Size}");
                foreach (var ch in n.GetChildren()) Dump(ch, indent + "  ");
            }
            Dump(GetTree().Root.FindChild("SidePanel", true, false)!, "");
        }
        if (_dev.TryGetValue("select", out var sel))
        {
            var ui = _view!.Interaction;
            if (sel == "depot" && game.Depots.Count > 0) ui.Select(new Selection("depot", game.Depots.Values.First().Id));
            if (sel == "vehicle" && game.Vehicles.Count > 0) ui.Select(new Selection("vehicle", game.Vehicles.Values.MinBy(v => v.Condition)!.Id));
            if (sel == "line" && game.Lines.Count > 0) ui.Select(new Selection("line", game.Lines.Values.First().Id));
        }
        if (_dev.TryGetValue("shot", out var shot)) TakeShot(shot);
    }

    private void ShowStart()
    {
        var save = SaveStore.Load();
        string? info = save == null ? null : $"{save.Area.Name.Split(',')[0]}, Tag {(int)(save.Time / 86400) + 1}";
        _start = new StartScreen { SaveInfo = info };
        _start.Chosen += c => _ = LoadGame(c, c.Continue ? save : null);
        var layer = new CanvasLayer();
        layer.AddChild(_start);
        AddChild(layer);
    }

    /// <summary>
    /// Developer check of the build tools: drives the same Interaction methods a mouse click calls.
    /// --tooltest[=panel] where panel is line, timetable, depot, vehicle, stop, track or draft.
    /// </summary>
    private void ToolTest(Game game, Interaction ui)
    {
        void Click(double x, double y) => ui.Click(new Vector2((float)x, (float)y), MouseButton.Left);
        ui.SetTool(Tool.Depot);
        Click(-200, -900);
        ui.SetTool(Tool.Line);
        foreach (var (x, y) in new[] { (-1600.0, -600.0), (-1100, -600), (-600, -600), (-100, -600), (400, -600) }) Click(x, y);
        ui.Enter();
        ui.SetTier(Core.Tier.Local);
        ui.SwitchMode(Core.ModeId.Tram);
        ui.SetTool(Tool.Track);
        foreach (var (x, y) in new[] { (-900.0, -300.0), (-300, -350), (300, -300) }) Click(x, y);
        ui.SetTool(Tool.Depot);
        Click(-880, -300);
        ui.SetTool(Tool.Line);
        foreach (var (x, y) in new[] { (-800.0, -310.0), (-450, -340), (-100, -330), (300, -300) }) Click(x, y);
        ui.Enter();
        game.Advance(3 * 3600);

        var bus = game.Lines.Values.First(l => l.Mode == Core.ModeId.Bus);
        string panel = _dev["tooltest"];
        switch (panel)
        {
            case "timetable":
                ui.Select(new Selection("line", bus.Id));
                GetTree().Root.FindChild("SidePanel", true, false)?.Call("ShowTab", "timetable");
                break;
            case "depot": ui.Select(new Selection("depot", game.Depots.Values.First().Id)); break;
            case "vehicle": ui.Select(new Selection("vehicle", game.Vehicles.Values.First(v => v.State != VehicleState.Depot).Id)); break;
            case "stop": ui.Select(new Selection("stop", bus.StopIds[1])); break;
            case "track":
                ui.SetTool(Tool.Track);
                Click(300, -300);
                ui.Hover(new Vector2(700, 100));
                break;
            case "draft":
                ui.SwitchMode(Core.ModeId.Bus);
                ui.SetTool(Tool.Line);
                Click(300, -1500);
                Click(300, -1000);
                ui.Hover(new Vector2(300, -500));
                break;
            default: ui.Select(new Selection("line", bus.Id)); break;
        }
        GD.Print($"tooltest: {game.Lines.Count} Linien, {game.Stops.Count} Haltestellen, {game.Depots.Count} Betriebshöfe, " +
                 $"{game.Vehicles.Count} Fahrzeuge, {game.Drivers.Count} Fahrer, Gleise {game.TrackLength:F0} m, Fahrgäste {game.Today.Trips}");
    }

    private async Task LoadGame(StartScreen.Choice choice, SaveData? save)
    {
        var area = save?.Area ?? choice.Area;
        try
        {
            string json = await Overpass.LoadAsync(area, SaveStore.CacheDir, msg => _start?.SetStatus(msg));
            _start?.SetStatus("Baue Straßennetz und Stadtmodell …");
            // Parsing and graph building take a moment: keep the UI responsive.
            var map = await Task.Run(() => MapData.Build(area, OsmData.Parse(json)));
            if (map.Graph.SegmentCount == 0) throw new InvalidDataException("In diesem Gebiet wurden keine Straßen gefunden.");
            var game = save != null ? Game.Restore(map, save) : new Game(map, choice.Sandbox);

            _start?.GetParent().QueueFree();
            _start = null;
            _view = new GameView { Game = game };
            _view.MenuRequested += BackToMenu;
            AddChild(_view);
            RunDevOptions(game);
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            _start?.SetStatus(ex.Message, error: true);
        }
    }

    private void BackToMenu()
    {
        _view?.Save(manual: false);
        _view?.QueueFree();
        _view = null;
        ShowStart();
    }
}
