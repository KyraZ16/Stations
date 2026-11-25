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

    /// <summary>Sample U-Bahn (dev shortcut): a tunnel across the center with a branch to the depot, line U1.</summary>
    public static TransitLine? BuildMetro(Game game)
    {
        game.BuildTunnel((-1800, -800), (-500, -700));
        game.BuildTunnel((-500, -700), (400, -250));
        game.BuildTunnel((400, -250), (1300, 300));
        game.BuildTunnel((-500, -700), (-500, -1150));
        var ids = new List<int>();
        foreach (var (x, y) in new[] { (-1700.0, -790.0), (-1050, -745), (-500, -700), (0, -450), (400, -250), (850, 25), (1250, 270) })
            if (game.SnapFor(ModeId.Metro, x, y, 80) is RoadPos p && game.AddStop(p, ModeId.Metro) is Stop st) ids.Add(st.Id);
        if (game.SnapFor(ModeId.Metro, -500, -1050, 80) is RoadPos dp) game.AddDepot(dp, ModeId.Metro);
        var line = game.CreateLine(ids, false, ModeId.Metro);
        if (line != null) game.AssignMissing(line);
        game.HireMissingDrivers();
        return line;
    }

    /// <summary>
    /// Sample S-Bahn (dev shortcut and tests): along the longest connected stretch of the railway from the map,
    /// a station about every 1.2 km, a depot near the start, line S1.
    /// </summary>
    public static TransitLine? BuildSBahn(Game game)
    {
        var g = game.Rail.Graph;
        if (g.SegmentCount == 0) return null;
        // largest connected part of the railway (by length)
        var comp = new int[g.VertexCount];
        Array.Fill(comp, -1);
        var size = new List<double>();
        for (int v0 = 0; v0 < g.VertexCount; v0++)
        {
            if (comp[v0] >= 0 || g.VSegs[v0].Count == 0) continue;
            int c = size.Count;
            size.Add(0);
            var q = new Queue<int>();
            q.Enqueue(v0);
            comp[v0] = c;
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                foreach (int s in g.VSegs[v])
                {
                    size[c] += g.SegLen[s] / 2;
                    int w = g.SegA[s] == v ? g.SegB[s] : g.SegA[s];
                    if (comp[w] >= 0) continue;
                    comp[w] = c;
                    q.Enqueue(w);
                }
            }
        }
        int best = size.IndexOf(size.Max());
        var nodes = Enumerable.Range(0, g.VertexCount).Where(v => comp[v] == best).ToList();
        int a = nodes.MinBy(v => g.Vx[v] + g.Vy[v] * 0.3), b = nodes.MaxBy(v => g.Vx[v] + g.Vy[v] * 0.3);
        if (g.Snap(g.Vx[a], g.Vy[a], 2) is not RoadPos pa || g.Snap(g.Vx[b], g.Vy[b], 2) is not RoadPos pb) return null;
        var route = game.RouteFor(ModeId.SBahn, pa, pb);
        if (route == null) return null;
        var ids = new List<int>();
        double acc = 1e9;
        RoadPos? depotAt = null;
        for (int k = 1; k < route.Xs.Count; k++)
        {
            double d = Util.Hypot(route.Xs[k] - route.Xs[k - 1], route.Ys[k] - route.Ys[k - 1]);
            acc += d;
            if (acc < 1200) continue;
            if (game.SnapFor(ModeId.SBahn, route.Xs[k], route.Ys[k], 5) is RoadPos p && game.AddStop(p, ModeId.SBahn) is Stop st)
            {
                ids.Add(st.Id);
                acc = 0;
                if (ids.Count == 1 && k + 3 < route.Xs.Count) depotAt = game.SnapFor(ModeId.SBahn, route.Xs[k + 3], route.Ys[k + 3], 5);
            }
        }
        if (depotAt is RoadPos dp) game.AddDepot(dp, ModeId.SBahn);
        var line = ids.Count >= 2 ? game.CreateLine(ids, false, ModeId.SBahn) : null;
        if (line != null) game.AssignMissing(line);
        game.HireMissingDrivers();
        return line;
    }
}
