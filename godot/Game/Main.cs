using Godot;
using Stations.Core;

namespace Stations;

/// <summary>
/// Placeholder entry scene: proves that the Godot project builds against the core library.
/// The map view, tools and panels replace this in the next step.
/// </summary>
public partial class Main : Control
{
    public override void _Ready()
    {
        var label = new Label
        {
            Text = $"Stations – Kern geladen ({Modes.All.Count} Verkehrsmittel definiert)\nKartenansicht folgt im nächsten Schritt.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(label);
    }
}
