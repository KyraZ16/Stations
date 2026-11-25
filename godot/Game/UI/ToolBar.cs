using Godot;
using Stations.Core;
using Stations.Core.Sim;

namespace Stations.UI;

/// <summary>Left tool bar: network level, transport mode, building tools and map overlays.</summary>
public partial class ToolBar : PanelContainer
{
    public Interaction UI = null!;
    private VBoxContainer _box = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopLeft);
        OffsetLeft = 10;
        OffsetTop = 78;
        CustomMinimumSize = new Vector2(200, 0);
        AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 12));
        _box = new VBoxContainer();
        _box.AddThemeConstantOverride("separation", 8);
        AddChild(_box);
        UI.StateChanged += Rebuild;
        Rebuild();
    }

    public override void _ExitTree() => UI.StateChanged -= Rebuild;

    private void Rebuild()
    {
        foreach (var c in _box.GetChildren()) c.QueueFree();

        var tiers = new HBoxContainer();
        tiers.AddThemeConstantOverride("separation", 2);
        foreach (var (id, name, desc) in Modes.Tiers)
        {
            var b = Toggle(name, UI.Tier == id, () => UI.SetTier(id));
            b.TooltipText = desc;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.AddThemeFontSizeOverride("font_size", 12);
            tiers.AddChild(b);
        }
        _box.AddChild(tiers);

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 6);
        foreach (var m in Modes.All.Values.Where(m => m.Tier == UI.Tier))
        {
            var b = Toggle(m.Available ? m.Name : $"{m.Name}\ngeplant", UI.Mode == m.Id, () => UI.SwitchMode(m.Id));
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 42);
            if (!m.Available) b.Modulate = new Color(1, 1, 1, 0.5f);
            modes.AddChild(b);
        }
        _box.AddChild(modes);

        _box.AddChild(Ui.Section("Werkzeuge"));
        foreach (var t in Interaction.Tools)
        {
            if (t.TracksOnly && !UI.ModeBuildsInfra) continue;
            string name = t.Tool == Tool.Track ? UI.InfraToolName : t.Tool == Tool.Stop && Game.IsMetro(UI.Mode) ? "U-Bahnhof" : t.Tool == Tool.Stop && Game.IsRail(UI.Mode) ? "S-Bahnhof" : t.Name;
            var b = Toggle($"{t.Icon}   {name}", UI.Tool == t.Tool, () => UI.SetTool(t.Tool));
            b.Alignment = HorizontalAlignment.Left;
            b.TooltipText = $"Taste {t.Key}";
            _box.AddChild(b);
        }

        _box.AddChild(Ui.Section("Ansicht"));
        var cov = new CheckBox { Text = "Einzugsgebiet (C)", ButtonPressed = UI.Coverage };
        cov.Toggled += _ => UI.ToggleCoverage();
        _box.AddChild(cov);
    }

    private static Button Toggle(string text, bool on, Action press)
    {
        var b = new Button { Text = text, Flat = !on };
        if (on)
        {
            b.AddThemeStyleboxOverride("normal", Ui.ButtonStyle(Ui.Accent));
            b.AddThemeStyleboxOverride("hover", Ui.ButtonStyle(Ui.Accent.Lightened(0.1f)));
            foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color" })
                b.AddThemeColorOverride(c, Colors.White);
        }
        b.Pressed += press;
        return b;
    }
}
