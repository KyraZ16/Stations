using Stations.Core.Map;

namespace Stations.Core.Sim;

public sealed class Journey
{
    public required List<Leg> Legs;
    public double Time; // total perceived time incl. walking at both ends
    public int FirstStop;
    public double FirstWalk;
}

/// <summary>
/// Passenger routing over the served transit network.
/// Nodes: one per stop, plus one "platform" node per (line, run, stop index).
/// </summary>
public sealed class Router
{
    public const double WalkSpeed = 1.3; // m/s
    public const double WalkDetour = 1.25;
    public const double AccessRadius = 500; // m, max walk to a stop
    private const double TransferRadius = 250;
    public const double MetroAccessRadius = 800; // m, U-Bahn stations
    private const double MetroTransferRadius = 350;
    private const double BoardPenalty = 90; // s, perceived cost of each boarding
    private const double MaxWait = 900;

    private enum EdgeKind : byte { Board, Ride, Alight, Walk }

    private readonly record struct Edge(int To, double Cost, EdgeKind Kind);

    private readonly record struct Platform(int Line, int Run, int Idx, int Stop, int Ver);

    private readonly List<Stop> _stops;
    private readonly Dictionary<int, int> _stopNode = new();
    private readonly List<Platform> _plat = new();
    private readonly List<List<Edge>> _adj = new();
    private readonly Dictionary<long, Journey?> _cache = new();
    private readonly Random _rng;

    /// <summary>Served stops reachable on foot from each cell.</summary>
    public readonly List<List<(int Node, double Walk)>> CellAccess = new();
    public readonly List<int> Covered = new();
    private readonly List<double> _cumRes = new();
    private readonly List<double> _cumJobs = new();
    public double CoveredRes { get; private set; }
    public double CoveredJobs { get; private set; }

    public Router(List<Cell> cells, IEnumerable<Stop> stops, IEnumerable<TransitLine> lines, Func<TransitLine, double> headway, Random rng)
    {
        _rng = rng;
        var active = lines.Where(l => double.IsFinite(headway(l)) && l.Runs.Count > 0).ToList();
        var served = new HashSet<int>(active.SelectMany(l => l.StopIds));
        _stops = stops.Where(s => served.Contains(s.Id)).ToList();
        for (int i = 0; i < _stops.Count; i++) _stopNode[_stops[i].Id] = i;
        int S = _stops.Count;
        for (int i = 0; i < S; i++) _adj.Add(new List<Edge>());

        foreach (var l in active)
        {
            double wait = Math.Min(headway(l) / 2, MaxWait) + BoardPenalty;
            for (int r = 0; r < l.Runs.Count; r++)
            {
                var run = l.Runs[r];
                int baseIdx = S + _plat.Count;
                int n = run.StopIds.Count;
                for (int i = 0; i < n; i++)
                {
                    int stop = run.StopIds[i];
                    _plat.Add(new Platform(l.Id, r, i, stop, l.Version));
                    var edges = new List<Edge>();
                    if (i < n - 1) edges.Add(new Edge(baseIdx + i + 1, run.Times[i + 1] - run.Times[i], EdgeKind.Ride));
                    if (i > 0) edges.Add(new Edge(_stopNode[stop], 0, EdgeKind.Alight));
                    _adj.Add(edges);
                    if (i < n - 1) _adj[_stopNode[stop]].Add(new Edge(baseIdx + i, wait, EdgeKind.Board));
                }
            }
        }

        // Walking transfers between nearby stops
        for (int i = 0; i < S; i++)
        for (int j = 0; j < S; j++)
        {
            if (i == j) continue;
            var a = _stops[i].Pos;
            var b = _stops[j].Pos;
            double d = Util.Hypot(a.X - b.X, a.Y - b.Y);
            bool metro = _stops[i].Mode == ModeId.Metro || _stops[j].Mode == ModeId.Metro;
            if (d < (metro ? MetroTransferRadius : TransferRadius)) _adj[i].Add(new Edge(j, d * WalkDetour / WalkSpeed + 60, EdgeKind.Walk));
        }

        // Cell coverage
        for (int c = 0; c < cells.Count; c++)
        {
            var cell = cells[c];
            var acc = new List<(int, double)>();
            for (int i = 0; i < S; i++)
            {
                var p = _stops[i].Pos;
                double d = Util.Hypot(p.X - cell.X, p.Y - cell.Y);
                // U-Bahn stations draw passengers from further away; the way down to the platform costs a minute
                if (_stops[i].Mode == ModeId.Metro ? d < MetroAccessRadius : d < AccessRadius)
                    acc.Add((i, d * WalkDetour / WalkSpeed + (_stops[i].Mode == ModeId.Metro ? 60 : 0)));
            }
            CellAccess.Add(acc);
            if (acc.Count > 0)
            {
                Covered.Add(c);
                CoveredRes += cell.Residents;
                CoveredJobs += cell.Jobs;
                _cumRes.Add(CoveredRes);
                _cumJobs.Add(CoveredJobs);
            }
        }
    }

    public int StopCount => _stops.Count;

    /// <summary>Pick a covered cell, weighted by residents or jobs.</summary>
    public int SampleCell(bool byJobs)
    {
        var cum = byJobs ? _cumJobs : _cumRes;
        double total = byJobs ? CoveredJobs : CoveredRes;
        double r = _rng.NextDouble() * total;
        int lo = 0, hi = cum.Count - 1;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (cum[m] < r) lo = m + 1;
            else hi = m;
        }
        return Covered[lo];
    }

    public Journey? Find(int from, int to)
    {
        long key = (long)from * 100003 + to;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var j = Search(from, to);
        if (_cache.Count > 200_000) _cache.Clear();
        _cache[key] = j;
        return j;
    }

    private Journey? Search(int from, int to)
    {
        var src = CellAccess[from];
        var dst = CellAccess[to];
        if (src.Count == 0 || dst.Count == 0) return null;
        int N = _adj.Count;
        var dist = new double[N];
        Array.Fill(dist, double.PositiveInfinity);
        var prev = new int[N];
        Array.Fill(prev, -1);
        var prevKind = new sbyte[N];
        Array.Fill(prevKind, (sbyte)-1);
        var heap = new MinHeap();
        foreach (var (node, walk) in src)
        {
            if (walk < dist[node]) { dist[node] = walk; heap.Push(walk, node); }
        }
        var target = new Dictionary<int, double>();
        foreach (var (node, walk) in dst) target[node] = walk;

        double best = double.PositiveInfinity;
        int bestNode = -1;
        while (heap.Count > 0)
        {
            int v = heap.Pop();
            double d = heap.LastKey;
            if (d > dist[v]) continue;
            if (d >= best) break;
            if (target.TryGetValue(v, out double tw) && prevKind[v] == (sbyte)EdgeKind.Alight && d + tw < best)
            {
                best = d + tw;
                bestNode = v;
            }
            foreach (var e in _adj[v])
            {
                double nd = d + e.Cost;
                if (nd < dist[e.To]) { dist[e.To] = nd; prev[e.To] = v; prevKind[e.To] = (sbyte)e.Kind; heap.Push(nd, e.To); }
            }
        }
        if (bestNode < 0) return null;

        var chain = new List<int>();
        for (int v = bestNode; v >= 0; v = prev[v]) chain.Add(v);
        chain.Reverse();
        int S = _stops.Count;
        var legs = new List<Leg>();
        double walkAcc = 0;
        Leg? cur = null;
        for (int k = 1; k < chain.Count; k++)
        {
            int v = chain[k], u = chain[k - 1];
            switch ((EdgeKind)prevKind[v])
            {
                case EdgeKind.Board:
                {
                    var p = _plat[v - S];
                    cur = new Leg { Line = p.Line, Run = p.Run, Board = p.Idx, BoardStop = p.Stop, WalkBefore = walkAcc, Ver = p.Ver };
                    walkAcc = 0;
                    break;
                }
                case EdgeKind.Alight when cur != null:
                {
                    var p = _plat[u - S];
                    cur.Alight = p.Idx;
                    cur.AlightStop = p.Stop;
                    legs.Add(cur);
                    cur = null;
                    break;
                }
                case EdgeKind.Walk:
                    walkAcc += dist[v] - dist[u];
                    break;
            }
        }
        if (legs.Count == 0) return null;
        double firstWalk = src.First(a => a.Node == chain[0]).Walk;
        return new Journey { Legs = legs, Time = best, FirstStop = legs[0].BoardStop, FirstWalk = firstWalk };
    }
}
