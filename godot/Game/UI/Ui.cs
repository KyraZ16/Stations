using Godot;

namespace Stations.UI;

/// <summary>Small helpers for building UI in code with a consistent look.</summary>
public static class Ui
{
    public static readonly Color Text = new("1f2328");
    public static readonly Color Muted = new("6b7078");
    public static readonly Color Accent = new("0a6ebd");
    public static readonly Color Danger = new("c62828");
    public static readonly Color Positive = new("2e7d32");
    public static readonly Color Panel = Colors.White;
    public static readonly Color Line = new("e3dfd8");

    public static Label Label(string text, int size = 14, Color? color = null, bool bold = false)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Text);
        if (bold)
        {
            l.AddThemeConstantOverride("outline_size", 1);
            l.AddThemeColorOverride("font_outline_color", color ?? Text);
        }
        return l;
    }

    public static StyleBoxFlat CardStyle(int radius = 14, int margin = 24) => new()
    {
        BgColor = Panel,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin * 0.75f, ContentMarginBottom = margin * 0.75f,
        ShadowColor = new Color(0.12f, 0.1f, 0.06f, 0.18f), ShadowSize = 10, ShadowOffset = new Vector2(0, 3),
    };

    private static Theme? _theme;

    /// <summary>Shared light theme; set on every UI root (themes do not pass through CanvasLayers).</summary>
    public static Theme Theme => _theme ??= CreateTheme();

    /// <summary>Light theme for all controls (Godot's default theme is dark).</summary>
    public static Theme CreateTheme()
    {
        var t = new Theme();
        StyleBoxFlat Box(Color bg, Color border) => new()
        {
            BgColor = bg, BorderColor = border,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5,
        };
        var normal = Box(Colors.White, Line);
        var hover = Box(new Color("f5f3ef"), Line);
        var pressed = Box(new Color("e8f1fa"), Accent);
        foreach (string type in new[] { "Button", "OptionButton", "CheckBox" })
        {
            t.SetStylebox("normal", type, type == "CheckBox" ? new StyleBoxEmpty() : normal);
            t.SetStylebox("hover", type, type == "CheckBox" ? new StyleBoxEmpty() : hover);
            t.SetStylebox("pressed", type, type == "CheckBox" ? new StyleBoxEmpty() : pressed);
            t.SetStylebox("hover_pressed", type, type == "CheckBox" ? new StyleBoxEmpty() : pressed);
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            foreach (string c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_hover_pressed_color" })
                t.SetColor(c, type, Text);
            t.SetColor("font_pressed_color", type, Accent);
        }
        t.SetStylebox("normal", "LineEdit", Box(Colors.White, Line));
        t.SetStylebox("focus", "LineEdit", Box(Colors.White, Accent));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", Muted);
        t.SetColor("font_color", "Label", Text);
        t.SetStylebox("panel", "PopupMenu", Box(Colors.White, Line));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Accent);
        t.SetStylebox("panel", "TooltipPanel", Box(new Color("1f2328"), new Color("1f2328")));
        t.SetColor("font_color", "TooltipLabel", Colors.White);
        return t;
    }

    public static StyleBoxFlat ButtonStyle(Color bg) => new()
    {
        BgColor = bg,
        CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
        ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 5, ContentMarginBottom = 5,
    };
}
