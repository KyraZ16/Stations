using Godot;
using Stations.Core.Map;
using Stations.Core.Sim;
using Stations.UI;

namespace Stations;

/// <summary>Entry scene: start screen → load OSM data → game view, and back via the menu.</summary>
public partial class Main : Node
{
    private StartScreen? _start;
    private LoadingScreen? _loading;
    private CancellationTokenSource? _cts;
    private GameView? _view;

    /// <summary>
    /// Developer options after "--" on the command line:
    /// --autostart (Hamburg sandbox), --demo (build demo network, skip to 08:00),
    /// --shot=path.png (save a screenshot and quit), --zoom=factor (zoom in from the overview),
    /// --loadingshot=path.png (screenshot of the loading screen).
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
            StartLoading(new StartScreen.Choice(new Core.Area("Hamburg Innenstadt", 53.5530, 9.9930, 2500), true, false), null);
        else if (_dev.TryGetValue("shot", out var shot))
            TakeShot(shot);
    }

    private async void TakeShot(string path, int frames = 20)
    {
        for (int i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
        if (_dev.TryGetValue("depot", out var dmode) && game.Depots.Count > 0)
        {
            if (dmode == "edit")
            {
                // sample edits: wash bay, workshop, Gelenkbus spot and a charger next to a spot
                var d0 = game.Depots.Values.First();
                game.PlaceModule(d0, Core.Sim.ModuleType.Road, 16, 8, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.Wash, 17, 8, 1);
                game.PlaceModule(d0, Core.Sim.ModuleType.Workshop, 1, 8, 0, 6, 6); // drawn: three lanes
                game.PlaceModule(d0, Core.Sim.ModuleType.LongBusSpot, 18, 0, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.Charger, 17, 6, 0);
                game.PlaceModule(d0, Core.Sim.ModuleType.Office, 7, 8, 0, 4, 4); // drawn: 48 staff places
                game.PlaceModule(d0, Core.Sim.ModuleType.ServiceCenter, 13, 11, 0, 4, 3);
                game.PlaceModule(d0, Core.Sim.ModuleType.PlanningOffice, 8, 12, 0, 3, 3);
                game.HireWorkers(d0.Id, Core.Sim.StaffRole.Researcher, 4);
                game.Points += 100;
                game.StartResearch("pay2");
                game.HireMissingDrivers(d0.Id); // mechanics, cleaners and service staff for the new buildings
                foreach (var v in game.Vehicles.Values.Where(v => v.DepotId == d0.Id && v.State == VehicleState.Depot).Take(3))
                {
                    v.Condition = 0.55; // something for the workshop and the wash to do
                    v.Cleanliness = 0.5;
                }
                game.Advance(600);
            }
        }
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
            if (_dev["depot"] == "tram") _view!.OpenDepotEditor(game.Depots.Values.First(d => d.Mode == Core.ModeId.Tram).Id);
            else _view!.OpenDepotEditor(game.Depots.Values.First().Id);
            float? camDist = _dev.TryGetValue("camdist", out var cd) && float.TryParse(cd, System.Globalization.CultureInfo.InvariantCulture, out float cdv) ? cdv : null;
            float? camYaw = _dev.TryGetValue("camyaw", out var cy) && float.TryParse(cy, System.Globalization.CultureInfo.InvariantCulture, out float cyv) ? cyv : null;
            float? camPitch = _dev.TryGetValue("campitch", out var cp) && float.TryParse(cp, System.Globalization.CultureInfo.InvariantCulture, out float cpv) ? cpv : null;
            Core.Sim.ModuleType? focus = _dev.TryGetValue("camfocus", out var cf) && Enum.TryParse<Core.Sim.ModuleType>(cf, true, out var ft) ? ft : null;
            _view!.DepotEditor?.SetView(_dev.ContainsKey("interior"), camDist, focus, camYaw, camPitch);
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
            if (sel == "research") ui.Select(new Selection("research", 0));
            if (sel == "vehicle" && game.Vehicles.Count > 0)
            {
                var veh = game.Vehicles.Values.Where(v => v.State == VehicleState.Moving).DefaultIfEmpty(game.Vehicles.Values.First()).MinBy(v => v!.Condition)!;
                ui.Select(new Selection("vehicle", veh.Id));
                ui.Map.CenterOn(new Vector2((float)veh.X, (float)veh.Y));
            }
            if (sel == "line" && game.Lines.Count > 0) ui.Select(new Selection("line", game.Lines.Values.First().Id));
        }
        if (_dev.TryGetValue("shot", out var shot)) TakeShot(shot);
    }

    private void ShowStart()
    {
        var save = SaveStore.Load();
        string? info = save == null ? null : $"{save.Area.Name.Split(',')[0]}, Tag {(int)(save.Time / 86400) + 1}";
        _start = new StartScreen { SaveInfo = info };
        _start.Chosen += c => StartLoading(c, c.Continue ? save : null);
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
            case "vehicle": ui.Select(new Selection("vehicle", game.Vehicles.Values.First(v => !Game.InYard(v)).Id)); break;
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

    /// <summary>Replace the start screen with the loading screen and start loading.</summary>
    private void StartLoading(StartScreen.Choice choice, SaveData? save)
    {
        _start?.GetParent().QueueFree();
        _start = null;
        var area = save?.Area ?? choice.Area;
        _loading = new LoadingScreen { CityName = area.Name };
        _loading.Cancelled += () =>
        {
            _cts?.Cancel();
            CloseLoading();
            ShowStart();
        };
        _loading.Retry += () => _ = LoadGame(choice, save);
        var layer = new CanvasLayer { Layer = 10 };
        layer.AddChild(_loading);
        AddChild(layer);
        if (_dev.TryGetValue("loadingshot", out var shot)) TakeShot(shot, frames: _dev.TryGetValue("frames", out var f) && int.TryParse(f, out int fr) ? fr : 12);
        _ = LoadGame(choice, save);
    }

    private void CloseLoading()
    {
        _loading?.GetParent().QueueFree();
        _loading = null;
    }

    private async Task LoadGame(StartScreen.Choice choice, SaveData? save)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var loading = _loading!;
        var area = save?.Area ?? choice.Area;
        try
        {
            loading.Report(0, 0, "Prüfe, ob die Stadt schon im Cache liegt …");
            string json = await Overpass.LoadAsync(area, SaveStore.CacheDir, msg => loading.Report(0, 0.05f, msg), ct,
                // Overpass sends no total size: approach 100 % smoothly with the received megabytes
                bytes => loading.Report(0, 1 - MathF.Exp(-bytes / 15e6f), $"{bytes / 1e6:0.0} MB empfangen"));

            loading.Report(1, 0.3f, $"{json.Length / 1e6:0.0} MB OpenStreetMap-Daten werden gelesen …");
            var osm = await Task.Run(() => OsmData.Parse(json), ct);
            loading.Report(2, 0, $"{osm.Ways.Count:N0} Wege und {osm.Nodes.Count:N0} Knoten");
            var map = await Task.Run(() => MapData.Build(area, osm, p => loading.Report(2, (float)p)), ct);
            if (map.Graph.SegmentCount == 0) throw new InvalidDataException("In diesem Gebiet wurden keine Straßen gefunden.");

            loading.Report(3, 0.3f, save != null ? "Spielstand wird geladen, Linien werden berechnet …" : "Neues Spiel wird angelegt …");
            var game = await Task.Run(() => save != null ? Game.Restore(map, save) : new Game(map, choice.Sandbox), ct);

            // Building the view triangulates all buildings on the main thread: show the step first.
            loading.Report(4, 0.3f, $"{map.Buildings.Count:N0} Gebäude und {map.Roads.Count:N0} Straßen werden gezeichnet …");
            for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (ct.IsCancellationRequested) return;
            _view = new GameView { Game = game };
            _view.MenuRequested += BackToMenu;
            AddChild(_view);
            for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            CloseLoading();
            RunDevOptions(game);
        }
        catch (OperationCanceledException)
        {
            // player went back to the menu
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            if (!ct.IsCancellationRequested) loading.Fail(ex.Message);
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
