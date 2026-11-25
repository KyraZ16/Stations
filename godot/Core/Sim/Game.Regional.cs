using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>
/// Regional trains connect the city with the region beyond the map. Where a railway leaves the map there is an
/// exit ("Ausfahrt", a portal stop). Every exit served by a regional line brings commuters: in the morning
/// mostly into the city (to the jobs), in the evening back out (from the homes). They ride the regional train
/// and change to buses, trams, U- and S-Bahn in the city. A regional ticket costs more than a city ticket.
/// </summary>
public sealed partial class Game
{
    public const double RegionalFare = 8;
    /// <summary>Trips per day between the region and the city through one served exit.</summary>
    public const double RegionalTripsPerExit = 2500;
    private const double EdgeInset = 60; // m inside the map edge
    private double _regionalAcc;

    public IEnumerable<Stop> Portals => Stops.Values.Where(s => s.Portal);

    /// <summary>
    /// Create the exits once: where the railway crosses the map edge (parallel tracks close together form one exit).
    /// Returns how many exits there are.
    /// </summary>
    public int EnsurePortals()
    {
        if (Portals.Any()) return Portals.Count();
        var net = Rail;
        double h = Map.Half - EdgeInset;
        bool Inside((double X, double Y) p) => Math.Abs(p.X) < h && Math.Abs(p.Y) < h;
        var crossings = new List<(double X, double Y)>();
        // main lines only: yards and sidings crossing the edge are no way out into the region
        var pieces = Map.Lines.Where(l => l.Kind is MapLineKind.Rail or MapLineKind.LightRail && l.Service == null)
            .SelectMany(l => Enumerable.Range(0, l.Pts.Length / 2 - 1).Select(i => (A: (X: l.Pts[2 * i], Y: l.Pts[2 * i + 1]), B: (X: l.Pts[2 * i + 2], Y: l.Pts[2 * i + 3]))));
        foreach (var (pa, pb) in pieces)
        {
            if (Inside(pa) == Inside(pb)) continue;
            // point on the piece where it crosses the square edge
            double t0 = 0, t1 = 1;
            for (int i = 0; i < 30; i++)
            {
                double tm = (t0 + t1) / 2;
                var pm = (pa.X + (pb.X - pa.X) * tm, pa.Y + (pb.Y - pa.Y) * tm);
                if (Inside(pm) == Inside(pa)) t0 = tm; else t1 = tm;
            }
            double t = Inside(pa) ? t0 : t1;
            crossings.Add((pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t));
        }
        // one exit per bundle of tracks
        var groups = new List<List<(double X, double Y)>>();
        foreach (var c in crossings)
        {
            var g = groups.FirstOrDefault(gr => gr.Any(o => Util.Hypot(o.X - c.X, o.Y - c.Y) < 600));
            if (g != null) g.Add(c); else groups.Add(new() { c });
        }
        foreach (var g in groups)
        {
            double cx = g.Average(p => p.X), cy = g.Average(p => p.Y);
            var at = g.MinBy(p => Util.Hypot(p.X - cx, p.Y - cy));
            if (net.Snap(at.X, at.Y, 30) is not RoadPos pos) continue;
            int id = _nextId++;
            Stops[id] = new Stop
            {
                Id = id, Mode = ModeId.Train, Pos = pos, Portal = true,
                Name = UniqueStopName($"Ausfahrt {Compass(pos.X, pos.Y)}", ModeId.Train),
            };
        }
        NamePortals();
        if (Portals.Any()) OnChange();
        return Portals.Count();
    }

    /// <summary>
    /// Name the exits after the city or town the railway heads for (largest place roughly in that direction,
    /// beyond the map). Exits that still carry a direction name are renamed when city names are available.
    /// </summary>
    public void NamePortals()
    {
        var towns = Map.Places.Where(p => !p.IsDistrict && Util.Hypot(p.X, p.Y) > Map.Half * 1.5).ToList();
        if (towns.Count == 0) return;
        var used = new HashSet<string>(Portals.Where(p => p.Name.StartsWith("Richtung ")).Select(p => p.Name));
        bool changed = false;
        foreach (var portal in Portals.Where(p => p.Name.StartsWith("Ausfahrt ")).OrderBy(p => p.Id))
        {
            double bearing = Math.Atan2(portal.Pos.Y, portal.Pos.X);
            var best = towns
                .Select(t => (t, diff: Math.Abs(Math.IEEERemainder(Math.Atan2(t.Y, t.X) - bearing, 2 * Math.PI)), d: Util.Hypot(t.X, t.Y)))
                .Where(c => c.diff < 0.45 && !used.Contains("Richtung " + c.t.Name))
                .OrderByDescending(c => (c.t.Population > 0 ? c.t.Population : c.t.Kind == PlaceKind.City ? 100_000 : 10_000) / Math.Sqrt(c.d) * (1 - c.diff))
                .Select(c => c.t).FirstOrDefault();
            if (best == null) continue;
            portal.Name = "Richtung " + best.Name;
            used.Add(portal.Name);
            changed = true;
        }
        if (changed) OnChange();
    }

    /// <summary>Direction of a point seen from the map center (y grows southward).</summary>
    public static string Compass(double x, double y)
    {
        string[] names = { "Ost", "Südost", "Süd", "Südwest", "West", "Nordwest", "Nord", "Nordost" };
        double ang = Math.Atan2(y, x); // 0 = east, +90° = south
        int i = (int)Math.Round(ang / (Math.PI / 4));
        return names[(i % 8 + 8) % 8];
    }

    /// <summary>Commuters between the region (served exits) and the covered parts of the city.</summary>
    private void GenerateRegionalDemand(double dt)
    {
        var R = Router;
        if (R == null || R.CoveredRes <= 0 || R.CoveredJobs <= 0) return;
        var served = Portals.Where(p => R.Serves(p.Id)).ToList();
        if (served.Count == 0) return;
        _regionalAcc += served.Count * RegionalTripsPerExit / 86400 / 0.9 * DemandFactor(Time) * dt * (1 + AboBoost + PaymentBoost);
        while (_regionalAcc >= 1)
        {
            _regionalAcc -= 1;
            var portal = served[Rng.Next(served.Count)];
            bool intoCity = Rng.NextDouble() < OutboundShare(Time); // morning: from home outside to work inside
            int cell = R.SampleCell(byJobs: intoCity);
            var j = R.FindStop(portal.Id, cell, fromStop: intoCity);
            if (j == null || j.Time > 2.5 * 3600) continue;
            double comfort = j.Legs.Min(l => Lines.TryGetValue(l.Line, out var ln) ? ln.Comfort : 1);
            if (Rng.NextDouble() > 0.85 * (0.6 + 0.4 * comfort)) continue;
            if (!Stops.TryGetValue(j.FirstStop, out var stop)) continue;
            var legs = j.Legs.Select(l => new Leg
            {
                Line = l.Line, Run = l.Run, Board = l.Board, Alight = l.Alight, BoardStop = l.BoardStop,
                AlightStop = l.AlightStop, WalkBefore = l.WalkBefore, Ver = l.Ver,
            }).ToList();
            stop.Waiting.Add(new Passenger
            {
                Id = _passengerId++, Legs = legs, ReadyAt = Time + legs[0].WalkBefore, WaitingSince = Time, SpawnedAt = Time,
            });
            Today.Regional++;
        }
    }
}
