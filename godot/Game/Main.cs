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
        if (_dev.TryGetValue("zoom", out var z) && float.TryParse(z, System.Globalization.CultureInfo.InvariantCulture, out float zf))
            _view!.ZoomBy(zf);
        if (_dev.TryGetValue("shot", out var shot)) TakeShot(shot);
        await Task.CompletedTask;
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
