using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>
/// Builds a small sample network (3 bus lines, 1 tram line) around the map center.
/// Used by the console test and as a debug shortcut in the game (F9) until the build tools exist.
/// </summary>
public static class DemoNetwork
{
    public static void Build(Game game)
    {
        var map = game.Map;
        List<int> MakeStops(ModeId mode, params (double X, double Y)[] pts)
        {
            var ids = new List<int>();
            foreach (var (x, y) in pts)
            {
                if (game.SnapFor(mode, x, y, 200) is not RoadPos p) continue;
                var s = game.StopAt(p.X, p.Y, 30, mode) ?? game.AddStop(p, mode);
                if (s != null) ids.Add(s.Id);
            }
            return ids;
        }

        if (map.Graph.Snap(-200, -900, 300) is RoadPos dp) game.AddDepot(dp);
        var lines = new List<TransitLine?>
        {
            game.CreateLine(MakeStops(ModeId.Bus, (-1600, -600), (-1100, -600), (-600, -600), (-100, -600), (400, -600), (900, -600), (1400, -600)), false),
            game.CreateLine(MakeStops(ModeId.Bus, (300, -1500), (300, -1000), (300, -500), (300, 0), (300, 500)), false),
            game.CreateLine(MakeStops(ModeId.Bus, (600, 600), (1100, 600), (1100, 1000), (600, 1000)), true),
        };

        var trackPts = new[] { (-900.0, -300.0), (-300, -350), (300, -300), (700, 100) };
        for (int i = 0; i + 1 < trackPts.Length; i++)
        {
            if (map.Graph.Snap(trackPts[i].Item1, trackPts[i].Item2, 200) is RoadPos a &&
                map.Graph.Snap(trackPts[i + 1].Item1, trackPts[i + 1].Item2, 200) is RoadPos b)
                game.BuildTrack(a, b);
        }
        if (game.SnapFor(ModeId.Tram, -880, -300, 80) is RoadPos tp) game.AddDepot(tp, ModeId.Tram);
        lines.Add(game.CreateLine(MakeStops(ModeId.Tram, (-800, -310), (-450, -340), (-100, -330), (300, -300), (650, 80)), false, ModeId.Tram));

        foreach (var l in lines) if (l != null) game.AssignMissing(l);
        game.HireMissingDrivers();
    }
}
