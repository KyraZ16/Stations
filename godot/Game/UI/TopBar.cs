using Godot;
using Stations.Core;
using Stations.Core.Sim;

namespace Stations.UI;

/// <summary>Top bar: clock, speed controls, money and today's key figures.</summary>
public partial class TopBar : PanelContainer
{
    public Game Game = null!;

    /// <summary>Raised for the save and menu buttons.</summary>
    public event Action? SaveRequested;
    public event Action? MenuRequested;
    public event Action? ResearchRequested;

    private Label _clock = null!, _money = null!, _trips = null!, _balance = null!, _riders = null!, _points = null!;
    private readonly Button[] _speed = new Button[4];
    private double _timer;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetLeft = 10;
        OffsetRight = -10;
        OffsetTop = 10;
        AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 14));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        AddChild(row);

        row.AddChild(Ui.Label("Stations.", 18, bold: true));
        _clock = Ui.Label("", 15, bold: true);
        _clock.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(_clock);

        var speeds = new HBoxContainer();
        string[] icons = { "II", "▶", "▶▶", "▶▶▶" };
        for (int i = 0; i < 4; i++)
        {
            int s = i;
            _speed[i] = new Button { Text = icons[i], Flat = true, TooltipText = i == 0 ? "Pause (Leertaste)" : $"Tempo {i} (Taste {i})" };
            _speed[i].Pressed += () => Game.Speed = s;
            speeds.AddChild(_speed[i]);
        }
        row.AddChild(speeds);

        _money = Stat(row, Game.Sandbox ? "Kontostand (Sandbox)" : "Kontostand");
        _trips = Stat(row, "Fahrgäste heute");
        _balance = Stat(row, "Einnahmen / Kosten");
        _riders = Stat(row, "Wartend / unterwegs");
        _points = Stat(row, "Punkte");

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var research = new Button { Text = "Forschung", TooltipText = "Punkte gegen Neuerungen eintauschen" };
        research.Pressed += () => ResearchRequested?.Invoke();
        row.AddChild(research);
        var save = new Button { Text = "Speichern", TooltipText = "F5" };
        save.Pressed += () => SaveRequested?.Invoke();
        row.AddChild(save);
        var menu = new Button { Text = "Menü" };
        menu.Pressed += () => MenuRequested?.Invoke();
        row.AddChild(menu);
        Refresh();
    }

    private static Label Stat(HBoxContainer row, string caption)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", -4);
        box.AddChild(Ui.Label(caption, 11, Ui.Muted));
        var value = Ui.Label("", 15, bold: true);
        box.AddChild(value);
        row.AddChild(box);
        return value;
    }

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer < 0.25) return;
        _timer = 0;
        Refresh();
    }

    private void Refresh()
    {
        var g = Game;
        _clock.Text = $"Tag {g.Day} · {Util.Clock(g.Time)}";
        for (int i = 0; i < 4; i++)
        {
            _speed[i].AddThemeColorOverride("font_color", g.Speed == i ? Ui.Accent : Ui.Text);
            _speed[i].AddThemeColorOverride("font_hover_color", g.Speed == i ? Ui.Accent : Ui.Text);
        }
        _money.Text = Util.Money(g.Money);
        _money.AddThemeColorOverride("font_color", g.Money < 0 ? Ui.Danger : Ui.Text);
        _trips.Text = Util.Num(g.Today.Trips);
        _balance.Text = $"{Util.Money(g.Today.Revenue)} / {Util.Money(g.Today.Costs)}";
        _riders.Text = $"{Util.Num(g.WaitingCount)} / {Util.Num(g.RidingCount)}";
        _points.Text = g.ActiveResearch is string rid
            ? $"{g.Points:F0} · {g.ResearchProgress / ResearchTree.Get(rid).Hours:P0}"
            : $"{g.Points:F0}";
    }
}
