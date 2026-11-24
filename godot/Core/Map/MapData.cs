namespace Stations.Core.Map;

public enum RoadClass { Motorway, Trunk, Primary, Secondary, Tertiary, Minor, Service, Pedestrian }

public enum BuildingKind { Res, Com, Mixed, Minor }

public enum PolyKind { Water, Park, Forest, Pitch, Cemetery, Residential, Commercial, Industrial, Railway }

public enum MapLineKind { River, Canal, Rail, Tram, Subway, LightRail }

public sealed class RoadWay
{
    public RoadClass Cls;
    public string Name = "";
    public double[] Pts = Array.Empty<double>(); // flat x,y
    public bool Tunnel;
}

public sealed class Building
{
    public double[] Pts = Array.Empty<double>();
    public double Cx, Cy;
    public BuildingKind Kind;
    public double Residents, Jobs;
    public int Cell = -1;
}

public sealed class AreaPoly
{
    public PolyKind Kind;
    public List<double[]> Rings = new();
}

public sealed class MapLine
{
    public MapLineKind Kind;
    public double[] Pts = Array.Empty<double>();
    public bool Tunnel;
}

/// <summary>Demand zone: buildings aggregated on a grid.</summary>
public sealed class Cell
{
    public double X, Y, Residents, Jobs;
}

public sealed class MapData
{
    public const double CellSize = 200;

    public required Area Area;
    public required Projection Proj;
    public double Half => Area.Radius;
    public List<RoadWay> Roads = new();
    public List<Building> Buildings = new();
    public List<AreaPoly> Polys = new();
    public List<MapLine> Lines = new();
    public List<Cell> Cells = new();
    public RoadGraph Graph = new();
    public double TotalResidents, TotalJobs;

    private static readonly Dictionary<string, RoadClass> RoadClasses = new()
    {
        ["motorway"] = RoadClass.Motorway, ["motorway_link"] = RoadClass.Motorway,
        ["trunk"] = RoadClass.Trunk, ["trunk_link"] = RoadClass.Trunk,
        ["primary"] = RoadClass.Primary, ["primary_link"] = RoadClass.Primary,
        ["secondary"] = RoadClass.Secondary, ["secondary_link"] = RoadClass.Secondary,
        ["tertiary"] = RoadClass.Tertiary, ["tertiary_link"] = RoadClass.Tertiary,
        ["unclassified"] = RoadClass.Minor, ["residential"] = RoadClass.Minor, ["road"] = RoadClass.Minor,
        ["living_street"] = RoadClass.Minor, ["busway"] = RoadClass.Minor, ["bus_guideway"] = RoadClass.Minor,
        ["service"] = RoadClass.Service, ["pedestrian"] = RoadClass.Pedestrian,
    };

    private static readonly Dictionary<string, double> DefaultSpeedKmh = new()
    {
        ["motorway"] = 100, ["motorway_link"] = 60, ["trunk"] = 70, ["trunk_link"] = 50, ["primary"] = 50,
        ["primary_link"] = 40, ["secondary"] = 50, ["secondary_link"] = 40, ["tertiary"] = 45, ["tertiary_link"] = 35,
        ["unclassified"] = 40, ["residential"] = 30, ["road"] = 30, ["living_street"] = 10, ["service"] = 20,
        ["busway"] = 50, ["bus_guideway"] = 60, ["pedestrian"] = 10,
    };

    /// <summary>Share of the speed limit actually reached in city traffic.</summary>
    public const double TrafficFactor = 0.75;

    private static readonly HashSet<string> ResTypes = new()
    {
        "house", "detached", "semidetached_house", "terrace", "apartments", "residential", "dormitory",
        "bungalow", "farm", "semi", "cabin",
    };
    private static readonly HashSet<string> ComTypes = new()
    {
        "commercial", "office", "retail", "industrial", "warehouse", "supermarket", "school", "university",
        "college", "hospital", "public", "civic", "government", "hotel", "kindergarten", "train_station",
        "transportation", "manufacture", "sports_hall", "sports_centre", "stadium", "museum", "mall", "factory",
    };
    private static readonly HashSet<string> MinorTypes = new()
    {
        "garage", "garages", "shed", "roof", "carport", "hut", "ruins", "container", "kiosk", "service",
        "construction", "greenhouse", "bunker", "toilets", "parking", "transformer_tower", "bridge",
    };

    private static string? Tag(Dictionary<string, string> t, string k) => t.TryGetValue(k, out var v) ? v : null;
    private static bool IsTrue(string? v) => v is "yes" or "true" or "1";

    private static double ParseSpeed(Dictionary<string, string> tags, string hw)
    {
        string? ms = Tag(tags, "maxspeed");
        double kmh = DefaultSpeedKmh.TryGetValue(hw, out var d) ? d : 30;
        if (ms != null)
        {
            double n = Util.ParseLeadingNumber(ms);
            if (!double.IsNaN(n)) kmh = ms.Contains("mph") ? n * 1.609 : n;
            else if (ms.EndsWith(":urban")) kmh = 50;
            else if (ms == "walk") kmh = 7;
        }
        return Math.Max(5, kmh) / 3.6;
    }

    private static bool Routable(Dictionary<string, string> tags, string hw)
    {
        string? bus = Tag(tags, "bus"), psv = Tag(tags, "psv");
        if (IsTrue(bus) || IsTrue(psv) || bus == "designated" || psv == "designated") return true;
        if (hw == "pedestrian") return false;
        if (hw == "service" && Tag(tags, "service") is "parking_aisle" or "driveway" or "drive-through" or "emergency_access")
            return false;
        if (Tag(tags, "access") is "private" or "no" || Tag(tags, "motor_vehicle") is "private" or "no") return false;
        if (Tag(tags, "area") == "yes") return false;
        return true;
    }

    public static double RingArea(double[] pts)
    {
        double a = 0;
        int n = pts.Length;
        for (int i = 0; i < n; i += 2)
        {
            int j = (i + 2) % n;
            a += pts[i] * pts[j + 1] - pts[j] * pts[i + 1];
        }
        return Math.Abs(a) / 2;
    }

    private static double[] ReversePts(double[] p)
    {
        var r = new double[p.Length];
        for (int i = p.Length - 2, k = 0; i >= 0; i -= 2, k += 2) { r[k] = p[i]; r[k + 1] = p[i + 1]; }
        return r;
    }

    /// <summary>Join way member geometries of a multipolygon relation into closed rings.</summary>
    private static List<double[]> StitchRings(List<double[]> parts)
    {
        var rings = new List<double[]>();
        var open = parts.Where(p => p.Length >= 4).ToList();
        static bool Same(double[] p, int i, double[] q, int j) =>
            Math.Abs(p[i] - q[j]) < 0.05 && Math.Abs(p[i + 1] - q[j + 1]) < 0.05;
        while (open.Count > 0)
        {
            var cur = open[^1];
            open.RemoveAt(open.Count - 1);
            bool changed = true;
            while (!Same(cur, 0, cur, cur.Length - 2) && changed)
            {
                changed = false;
                for (int k = 0; k < open.Count; k++)
                {
                    var p = open[k];
                    int end = cur.Length - 2;
                    if (Same(cur, end, p, 0)) cur = cur.Concat(p.Skip(2)).ToArray();
                    else if (Same(cur, end, p, p.Length - 2)) cur = cur.Concat(ReversePts(p).Skip(2)).ToArray();
                    else if (Same(cur, 0, p, p.Length - 2)) cur = p.Concat(cur.Skip(2)).ToArray();
                    else if (Same(cur, 0, p, 0)) cur = ReversePts(p).Concat(cur.Skip(2)).ToArray();
                    else continue;
                    open.RemoveAt(k);
                    changed = true;
                    break;
                }
            }
            rings.Add(cur);
        }
        return rings;
    }

    private static PolyKind? GetPolyKind(Dictionary<string, string> t)
    {
        if (Tag(t, "natural") == "water" || Tag(t, "waterway") == "riverbank") return PolyKind.Water;
        string? leisure = Tag(t, "leisure");
        if (leisure is "park" or "garden" or "playground") return PolyKind.Park;
        if (leisure == "pitch") return PolyKind.Pitch;
        return Tag(t, "landuse") switch
        {
            "grass" or "meadow" or "recreation_ground" => PolyKind.Park,
            "forest" => PolyKind.Forest,
            "cemetery" => PolyKind.Cemetery,
            "residential" => PolyKind.Residential,
            "commercial" or "retail" => PolyKind.Commercial,
            "industrial" => PolyKind.Industrial,
            "railway" => PolyKind.Railway,
            _ => null,
        };
    }

    public static MapData Build(Area area, OsmData osm)
    {
        var proj = new Projection(area.Lat, area.Lon);
        var m = new MapData { Area = area, Proj = proj };
        var nodes = new Dictionary<long, (double X, double Y)>(osm.Nodes.Count);
        foreach (var n in osm.Nodes) nodes[n.Id] = (proj.X(n.Lon), proj.Y(n.Lat));

        double[] GeomPts(List<LatLon>? geom)
        {
            if (geom == null) return Array.Empty<double>();
            var pts = new double[geom.Count * 2];
            for (int i = 0; i < geom.Count; i++) { pts[2 * i] = proj.X(geom[i].Lon); pts[2 * i + 1] = proj.Y(geom[i].Lat); }
            return pts;
        }

        foreach (var w in osm.Ways)
        {
            var t = w.Tags;
            if (t.ContainsKey("highway") && w.Nodes != null) m.AddRoad(w, t, nodes);
            else if (t.ContainsKey("building")) m.AddBuilding(GeomPts(w.Geometry), t);
            else if (t.TryGetValue("railway", out var rw))
            {
                var pts = GeomPts(w.Geometry);
                var kind = rw switch
                {
                    "tram" => MapLineKind.Tram, "subway" => MapLineKind.Subway,
                    "light_rail" => MapLineKind.LightRail, _ => MapLineKind.Rail,
                };
                if (pts.Length >= 4)
                {
                    m.Lines.Add(new MapLine
                    {
                        Kind = kind, Pts = pts,
                        Tunnel = Tag(t, "tunnel") == "yes" || Tag(t, "location") == "underground" || (Tag(t, "layer")?.StartsWith('-') ?? false),
                    });
                }
            }
            else if (Tag(t, "waterway") is "river" or "canal")
            {
                var pts = GeomPts(w.Geometry);
                if (pts.Length >= 4)
                {
                    m.Lines.Add(new MapLine
                    {
                        Kind = Tag(t, "waterway") == "river" ? MapLineKind.River : MapLineKind.Canal, Pts = pts,
                        Tunnel = Tag(t, "tunnel") is "culvert" or "yes",
                    });
                }
            }
            else
            {
                var kind = GetPolyKind(t);
                var pts = GeomPts(w.Geometry);
                if (kind != null && pts.Length >= 6) m.Polys.Add(new AreaPoly { Kind = kind.Value, Rings = { pts } });
            }
        }

        foreach (var r in osm.Relations)
        {
            var kind = GetPolyKind(r.Tags);
            if (kind == null) continue;
            var outer = r.Members.Where(x => x.Type == "way" && x.Role != "inner").Select(x => GeomPts(x.Geometry)).ToList();
            var inner = r.Members.Where(x => x.Type == "way" && x.Role == "inner").Select(x => GeomPts(x.Geometry)).ToList();
            var rings = StitchRings(outer).Concat(StitchRings(inner)).Where(p => p.Length >= 6).ToList();
            if (rings.Count > 0) m.Polys.Add(new AreaPoly { Kind = kind.Value, Rings = rings });
        }

        m.BuildCells();
        // Draw large polygons first so small ones (ponds in parks) stay visible.
        m.Polys.Sort((p, q) => RingArea(q.Rings[0]).CompareTo(RingArea(p.Rings[0])));
        return m;
    }

    private void AddRoad(OsmWay w, Dictionary<string, string> t, Dictionary<long, (double X, double Y)> nodes)
    {
        string hw = t["highway"];
        if (!RoadClasses.TryGetValue(hw, out var cls)) return;
        var pts = new List<double>();
        var ids = new List<long>();
        foreach (long id in w.Nodes!)
        {
            if (!nodes.TryGetValue(id, out var p)) continue;
            pts.Add(p.X);
            pts.Add(p.Y);
            ids.Add(id);
        }
        if (ids.Count < 2) return;
        bool tunnel = Tag(t, "tunnel") is "yes" or "building_passage";
        int wayIdx = Roads.Count;
        Roads.Add(new RoadWay { Cls = cls, Name = Tag(t, "name") ?? Tag(t, "ref") ?? "", Pts = pts.ToArray(), Tunnel = tunnel });
        if (!Routable(t, hw)) return;

        bool fwd = true, bwd = true;
        string? ow = Tag(t, "oneway");
        if (IsTrue(ow) || Tag(t, "junction") is "roundabout" or "circular" || hw is "motorway" or "motorway_link") bwd = false;
        else if (ow == "-1") fwd = false;
        if (Tag(t, "oneway:bus") == "no" || Tag(t, "oneway:psv") == "no" || Tag(t, "busway") == "opposite_lane")
        {
            fwd = true;
            bwd = true;
        }
        double spd = ParseSpeed(t, hw) * TrafficFactor;
        int prev = Graph.Vertex(ids[0], pts[0], pts[1]);
        for (int i = 1; i < ids.Count; i++)
        {
            int v = Graph.Vertex(ids[i], pts[2 * i], pts[2 * i + 1]);
            Graph.AddSegment(prev, v, fwd, bwd, spd, wayIdx);
            prev = v;
        }
    }

    private void AddBuilding(double[] pts, Dictionary<string, string> t)
    {
        if (pts.Length < 6) return;
        double area = RingArea(pts);
        if (area < 15) return;
        double cx = 0, cy = 0;
        int n = pts.Length / 2;
        for (int i = 0; i < pts.Length; i += 2) { cx += pts[i]; cy += pts[i + 1]; }
        cx /= n;
        cy /= n;
        string type = t["building"];
        BuildingKind kind = MinorTypes.Contains(type) || area < 30 ? BuildingKind.Minor
            : ResTypes.Contains(type) ? BuildingKind.Res
            : ComTypes.Contains(type) || t.ContainsKey("shop") || t.ContainsKey("amenity") || t.ContainsKey("office") ? BuildingKind.Com
            : BuildingKind.Mixed;
        double defLevels = type == "apartments" ? 4 : kind == BuildingKind.Res ? 2 : 3;
        double lv = Util.ParseLeadingNumber(Tag(t, "building:levels"));
        double levels = double.IsNaN(lv) ? defLevels : Math.Clamp(lv, 1, 40);
        double floor = area * levels;
        double residents = 0, jobs = 0;
        switch (kind)
        {
            case BuildingKind.Res: residents = floor / 45; break;
            case BuildingKind.Com: jobs = floor / 35; break;
            case BuildingKind.Mixed: residents = floor / 80; jobs = floor / 110; break;
        }
        Buildings.Add(new Building { Pts = pts, Cx = cx, Cy = cy, Kind = kind, Residents = residents, Jobs = jobs });
    }

    private void BuildCells()
    {
        var cellMap = new Dictionary<(int, int), int>();
        var acc = new List<(double Sx, double Sy, double W)>();
        foreach (var b in Buildings)
        {
            if (b.Kind == BuildingKind.Minor) continue;
            var key = ((int)Math.Floor(b.Cx / CellSize), (int)Math.Floor(b.Cy / CellSize));
            if (!cellMap.TryGetValue(key, out int ci))
            {
                ci = Cells.Count;
                cellMap[key] = ci;
                Cells.Add(new Cell());
                acc.Add((0, 0, 0));
            }
            b.Cell = ci;
            double w = b.Residents + b.Jobs;
            Cells[ci].Residents += b.Residents;
            Cells[ci].Jobs += b.Jobs;
            var a = acc[ci];
            acc[ci] = (a.Sx + b.Cx * w, a.Sy + b.Cy * w, a.W + w);
            TotalResidents += b.Residents;
            TotalJobs += b.Jobs;
        }
        for (int i = 0; i < Cells.Count; i++)
        {
            Cells[i].X = acc[i].Sx / acc[i].W;
            Cells[i].Y = acc[i].Sy / acc[i].W;
        }
    }
}
