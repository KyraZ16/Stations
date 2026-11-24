using Godot;
using Stations.Core;
using Stations.Core.Map;

namespace Stations.UI;

/// <summary>City choice: presets, place search, map size, sandbox, continue a saved game.</summary>
public partial class StartScreen : Control
{
    public sealed record Choice(Area Area, bool Sandbox, bool Continue);

    /// <summary>Raised when the player starts a game.</summary>
    public event Action<Choice>? Chosen;

    private static readonly Area[] Presets =
    {
        new("Hamburg Innenstadt", 53.5530, 9.9930, 2500),
        new("Berlin Mitte", 52.5200, 13.4050, 2500),
        new("München Zentrum", 48.1390, 11.5660, 2500),
        new("Köln Innenstadt", 50.9400, 6.9550, 2500),
        new("Leipzig Zentrum", 51.3400, 12.3740, 2500),
        new("Freiburg", 47.9960, 7.8460, 2500),
    };

    private static readonly (double R, string Label)[] Sizes =
    {
        (1500, "Klein (3 × 3 km)"), (2500, "Mittel (5 × 5 km)"), (4000, "Groß (8 × 8 km, lädt länger)"),
    };

    private Area _chosen = Presets[0];
    /// <summary>Description of the saved game for the continue button, or null if there is none.</summary>
    public string? SaveInfo;
    private Label _status = null!;
    private Label _chosenLabel = null!;
    private OptionButton _size = null!;
    private CheckBox _sandbox = null!;
    private LineEdit _search = null!;
    private VBoxContainer _results = null!;
    private readonly ButtonGroup _group = new();
    private bool _busy;


    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = Ui.Theme;
        var bg = new ColorRect { Color = new Color("ece6da") };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var card = new PanelContainer { CustomMinimumSize = new Vector2(640, 0) };
        card.AddThemeStyleboxOverride("panel", Ui.CardStyle());
        center.AddChild(card);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        card.AddChild(box);

        box.AddChild(Ui.Label("Stations.", 34, bold: true));
        box.AddChild(Ui.Label("ÖPNV-Simulation auf echten Straßen (OpenStreetMap)", 15, Ui.Muted));
        box.AddChild(Ui.Label("Stadt wählen", 15, bold: true));

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(grid);
        foreach (var p in Presets)
        {
            var b = new Button { Text = p.Name, ToggleMode = true, ButtonGroup = _group, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.ButtonPressed = p == _chosen;
            b.Pressed += () => Choose(p);
            grid.AddChild(b);
        }

        box.AddChild(Ui.Label("… oder Ort suchen", 15, bold: true));
        var searchRow = new HBoxContainer();
        _search = new LineEdit { PlaceholderText = "z. B. Dresden Neustadt, Wien, Zürich HB", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _search.TextSubmitted += _ => Search();
        var searchBtn = new Button { Text = "Suchen" };
        searchBtn.Pressed += Search;
        searchRow.AddChild(_search);
        searchRow.AddChild(searchBtn);
        box.AddChild(searchRow);
        _results = new VBoxContainer();
        box.AddChild(_results);

        var opts = new HBoxContainer();
        opts.AddThemeConstantOverride("separation", 16);
        opts.AddChild(Ui.Label("Kartengröße", 14));
        _size = new OptionButton();
        foreach (var (_, label) in Sizes) _size.AddItem(label);
        _size.Selected = 1;
        opts.AddChild(_size);
        _sandbox = new CheckBox { Text = "Sandbox (unbegrenztes Geld)", ButtonPressed = true };
        opts.AddChild(_sandbox);
        box.AddChild(opts);

        var go = new HBoxContainer();
        go.AddThemeConstantOverride("separation", 8);
        _chosenLabel = Ui.Label("", 14, Ui.Muted);
        _chosenLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        go.AddChild(_chosenLabel);
        if (SaveInfo != null)
        {
            var cont = new Button { Text = $"Weiterspielen: {SaveInfo}" };
            cont.Pressed += () => Start(continueSave: true);
            go.AddChild(cont);
        }
        var start = new Button { Text = "Neues Spiel" };
        start.AddThemeStyleboxOverride("normal", Ui.ButtonStyle(Ui.Accent));
        start.AddThemeColorOverride("font_color", Colors.White);
        start.Pressed += () => Start(continueSave: false);
        go.AddChild(start);
        box.AddChild(go);

        _status = Ui.Label("", 13, Ui.Muted);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_status);
        box.AddChild(Ui.Label("Kartendaten © OpenStreetMap-Mitwirkende (ODbL), abgerufen über die Overpass API.", 11, Ui.Muted));
        UpdateChosen();
    }

    private void Choose(Area a)
    {
        _chosen = a;
        UpdateChosen();
    }

    private void UpdateChosen() => _chosenLabel.Text = "Ort: " + string.Join(",", _chosen.Name.Split(',').Take(2));

    public void SetStatus(string msg, bool error = false)
    {
        _status.Text = msg;
        _status.AddThemeColorOverride("font_color", error ? Ui.Danger : Ui.Muted);
        if (error) _busy = false;
    }

    private async void Search()
    {
        string q = _search.Text.Trim();
        if (q == "" || _busy) return;
        SetStatus("Suche …");
        try
        {
            var results = await Overpass.SearchPlaceAsync(q);
            foreach (var c in _results.GetChildren()) c.QueueFree();
            foreach (var r in results)
            {
                var area = new Area(r.Name, r.Lat, r.Lon, 2500);
                var b = new Button
                {
                    Text = r.Name, ToggleMode = true, ButtonGroup = _group, Alignment = HorizontalAlignment.Left,
                    ClipText = true, CustomMinimumSize = new Vector2(600, 0),
                };
                b.Pressed += () => Choose(area);
                _results.AddChild(b);
            }
            if (results.Count > 0)
            {
                Choose(new Area(results[0].Name, results[0].Lat, results[0].Lon, 2500));
                ((Button)_results.GetChild(0)).ButtonPressed = true;
            }
            SetStatus(results.Count > 0 ? "" : "Nichts gefunden");
        }
        catch (Exception ex)
        {
            SetStatus($"Suche fehlgeschlagen: {ex.Message}", true);
        }
    }

    private void Start(bool continueSave)
    {
        if (_busy) return;
        _busy = true;
        var area = _chosen with { Radius = Sizes[_size.Selected].R };
        Chosen?.Invoke(new Choice(area, _sandbox.ButtonPressed, continueSave));
    }
}
