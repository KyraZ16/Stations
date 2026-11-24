using Godot;
using Stations.Core.Map;

namespace Stations.View;

/// <summary>Map colors and road styles (same look as the browser prototype).</summary>
public static class Palette
{
    public static readonly Color Background = new("e9e5dd");
    public static readonly Color Outside = new("d6d1c7");
    public static readonly Color Water = new("a9cbe0");
    public static readonly Color Rail = new("a49c90");
    public static readonly Color Tunnel = new(0.59f, 0.55f, 0.49f, 0.35f);
    public static readonly Color TrackOuter = new("4a4f57");
    public static readonly Color TrackInner = new("d9d4cc");
    public static readonly Color Ink = new("1f2328");
    public static readonly Color Highlight = new("f39200");

    public static Color Poly(PolyKind k) => k switch
    {
        PolyKind.Water => Water,
        PolyKind.Park => new("cfe3bf"),
        PolyKind.Forest => new("b9d6a8"),
        PolyKind.Pitch => new("bfdcae"),
        PolyKind.Cemetery => new("c9d9bf"),
        PolyKind.Residential => new("e4dfd5"),
        PolyKind.Commercial => new("eadad6"),
        PolyKind.Industrial => new("ded8e2"),
        _ => new("ddd6cc"),
    };

    public static Color Building(BuildingKind k) => k switch
    {
        BuildingKind.Res => new("d3cbc0"),
        BuildingKind.Com => new("d0c4c4"),
        BuildingKind.Mixed => new("d1c9c0"),
        _ => new("dad4cb"),
    };

    public readonly record struct RoadStyle(float Width, float MinPx, Color Fill, Color Casing);

    /// <summary>Drawing order: minor roads first so major roads end up on top.</summary>
    public static readonly RoadClass[] RoadOrder =
    {
        RoadClass.Pedestrian, RoadClass.Service, RoadClass.Minor, RoadClass.Tertiary,
        RoadClass.Secondary, RoadClass.Primary, RoadClass.Trunk, RoadClass.Motorway,
    };

    public static RoadStyle Road(RoadClass c) => c switch
    {
        RoadClass.Pedestrian => new(5, 0.6f, new("f4f1ec"), new("d8d2c8")),
        RoadClass.Service => new(4.5f, 0.6f, Colors.White, new("d3ccc1")),
        RoadClass.Minor => new(8, 1.0f, Colors.White, new("cfc7ba")),
        RoadClass.Tertiary => new(10, 1.4f, Colors.White, new("c4bbad")),
        RoadClass.Secondary => new(12, 1.8f, new("fdf3d8"), new("cdb98f")),
        RoadClass.Primary => new(14, 2.2f, new("fde8bd"), new("d2aa6c")),
        RoadClass.Trunk => new(16, 2.6f, new("f9d9a6"), new("c79c5c")),
        _ => new(18, 3, new("f6c78f"), new("b98446")),
    };
}
