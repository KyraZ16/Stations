using Godot;

namespace Stations.UI;

/// <summary>
/// Shown while a city is loaded: steps with check marks, a progress bar, an animated spinner and tips.
/// <see cref="Report"/> may be called from any thread; the UI picks it up in <c>_Process</c>.
/// </summary>
public partial class LoadingScreen : Control
{
    public string CityName = "";
    public event Action? Cancelled;
    public event Action? Retry;

    public static readonly string[] Steps =
    {
        "Kartendaten laden",
        "Daten lesen",
        "Straßennetz und Gebäude aufbauen",
        "Spielstand vorbereiten",
        "Karte zeichnen",
    };

    // Share of the bar per step (downloading dominates on first visits)
    private static readonly float[] Weights = { 0.45f, 0.15f, 0.25f, 0.05f, 0.10f };

    private static readonly string[] Tips =
    {
        "Tipp: Ein Klick auf die erste Haltestelle beim Linienzeichnen macht daraus eine Ringlinie.",
        "Tipp: Baue früh eine Werkstatt – ohne sie verschleißen die Fahrzeuge und haben Pannen.",
        "Tipp: Mit „C“ siehst du das Einzugsgebiet: grüne Häuser sind erschlossen.",
        "Tipp: Gelenkbusse brauchen im Betriebshof einen langen Stellplatz.",
        "Tipp: Im Fahrplan legst du pro Tageszeit einen eigenen Takt fest.",
        "Tipp: Haltestellen von Bus und Tram nah beieinander ermöglichen Umstiege.",
        "Tipp: Schmutzige Fahrzeuge schrecken Fahrgäste ab – eine Waschanlage hilft.",
        "Tipp: Einmal geladene Städte kommen beim nächsten Mal aus dem Cache und starten sofort.",
    };

    private readonly object _lock = new();
    private int _step;
    private float _stepFraction;
    private string _detail = "";
    private string? _error;
    private bool _dirty = true;

    private Label _title = null!, _detailLabel = null!, _tip = null!, _errorLabel = null!;
    private readonly List<Label> _stepLabels = new();
    private ProgressBar _bar = null!;
    private Spinner _spinner = null!;
    private HBoxContainer _buttons = null!;
    private Button _retry = null!;
    private double _tipTimer;
    private int _tipIndex;
    private float _shownProgress;

    /// <summary>Thread-safe progress update: current step, fraction within the step (0..1) and a detail line.</summary>
    public void Report(int step, float fraction, string? detail = null)
    {
        lock (_lock)
        {
            _step = step;
            _stepFraction = Math.Clamp(fraction, 0, 1);
            if (detail != null) _detail = detail;
            _dirty = true;
        }
    }

    /// <summary>Show an error with retry/back buttons.</summary>
    public void Fail(string message)
    {
        lock (_lock)
        {
            _error = message;
            _dirty = true;
        }
    }

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
        var card = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) };
        card.AddThemeStyleboxOverride("panel", Ui.CardStyle());
        center.AddChild(card);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        card.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 14);
        _spinner = new Spinner { CustomMinimumSize = new Vector2(40, 40) };
        head.AddChild(_spinner);
        var titles = new VBoxContainer();
        titles.AddChild(Ui.Label("Stations.", 28, bold: true));
        _title = Ui.Label($"Lade {CityName.Split(',')[0]} …", 15, Ui.Muted);
        titles.AddChild(_title);
        head.AddChild(titles);
        box.AddChild(head);

        var steps = new VBoxContainer();
        steps.AddThemeConstantOverride("separation", 2);
        foreach (string s in Steps)
        {
            var l = Ui.Label(s, 14, Ui.Muted);
            _stepLabels.Add(l);
            steps.AddChild(l);
        }
        box.AddChild(steps);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 10) };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("ebe7e0"), CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5 });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Accent, CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5 });
        box.AddChild(_bar);

        _detailLabel = Ui.Label("", 13, Ui.Muted);
        _detailLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_detailLabel);

        _errorLabel = Ui.Label("", 13, Ui.Danger);
        _errorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _errorLabel.Visible = false;
        box.AddChild(_errorLabel);

        _tip = Ui.Label(Tips[0], 13, Ui.Text);
        _tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _tip.CustomMinimumSize = new Vector2(500, 40);
        box.AddChild(_tip);

        _buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _buttons.AddThemeConstantOverride("separation", 8);
        _retry = Ui.Btn("Erneut versuchen", () =>
        {
            lock (_lock) { _error = null; _dirty = true; }
            Retry?.Invoke();
        }, primary: true);
        _retry.Visible = false;
        _buttons.AddChild(_retry);
        _buttons.AddChild(Ui.Btn("Abbrechen", () => Cancelled?.Invoke()));
        box.AddChild(_buttons);
        _tipIndex = (int)(Time.GetTicksMsec() / 1000 % (ulong)Tips.Length);
        _tip.Text = Tips[_tipIndex];
    }

    public override void _Process(double delta)
    {
        _tipTimer += delta;
        if (_tipTimer > 6)
        {
            _tipTimer = 0;
            _tipIndex = (_tipIndex + 1) % Tips.Length;
            _tip.Text = Tips[_tipIndex];
        }

        int step;
        float frac;
        string detail;
        string? error;
        lock (_lock)
        {
            step = _step;
            frac = _stepFraction;
            detail = _detail;
            error = _error;
            if (!_dirty && error == null) { AnimateBar(step, frac); return; }
            _dirty = false;
        }

        for (int i = 0; i < _stepLabels.Count; i++)
        {
            var l = _stepLabels[i];
            bool done = i < step, current = i == step;
            l.Text = (done ? "✓  " : current ? "›  " : "    ") + Steps[i];
            l.AddThemeColorOverride("font_color", done ? Ui.Positive : current ? Ui.Text : Ui.Muted);
        }
        _detailLabel.Text = detail;
        _errorLabel.Visible = error != null;
        _errorLabel.Text = error ?? "";
        _retry.Visible = error != null;
        _spinner.Visible = error == null;
        AnimateBar(step, frac);
    }

    /// <summary>Overall progress from step weights; the bar eases towards it so jumps look smooth.</summary>
    private void AnimateBar(int step, float frac)
    {
        float target = Weights.Take(step).Sum() + Weights[Math.Min(step, Weights.Length - 1)] * frac;
        _shownProgress = Mathf.Lerp(_shownProgress, target, 0.15f);
        _bar.Value = _shownProgress;
    }

    /// <summary>Rotating arc to show the game is busy.</summary>
    private partial class Spinner : Control
    {
        private float _angle;

        public override void _Process(double delta)
        {
            _angle += (float)delta * 5;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = Size / 2;
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 3;
            DrawArc(c, r, 0, Mathf.Tau, 48, new Color("e3dfd8"), 4, true);
            DrawArc(c, r, _angle, _angle + 1.8f, 24, Ui.Accent, 4, true);
        }
    }
}
