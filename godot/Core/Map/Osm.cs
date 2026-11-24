using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Stations.Core.Map;

public readonly record struct LatLon(double Lat, double Lon);

public sealed class OsmNode
{
    public long Id;
    public double Lat, Lon;
}

public sealed class OsmWay
{
    public long Id;
    public Dictionary<string, string> Tags = new();
    public long[]? Nodes;
    public List<LatLon>? Geometry;
}

public sealed class OsmMember
{
    public string Type = "";
    public string Role = "";
    public List<LatLon>? Geometry;
}

public sealed class OsmRelation
{
    public long Id;
    public Dictionary<string, string> Tags = new();
    public List<OsmMember> Members = new();
}

/// <summary>Overpass JSON response, split by element type (order within each list is preserved).</summary>
public sealed class OsmData
{
    public List<OsmNode> Nodes = new();
    public List<OsmWay> Ways = new();
    public List<OsmRelation> Relations = new();

    public static OsmData Parse(string json)
    {
        var data = new OsmData();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            string type = el.GetProperty("type").GetString() ?? "";
            long id = el.GetProperty("id").GetInt64();
            switch (type)
            {
                case "node":
                    data.Nodes.Add(new OsmNode { Id = id, Lat = el.GetProperty("lat").GetDouble(), Lon = el.GetProperty("lon").GetDouble() });
                    break;
                case "way":
                {
                    var w = new OsmWay { Id = id, Tags = ReadTags(el) };
                    if (el.TryGetProperty("nodes", out var nodes))
                    {
                        w.Nodes = new long[nodes.GetArrayLength()];
                        int i = 0;
                        foreach (var n in nodes.EnumerateArray()) w.Nodes[i++] = n.GetInt64();
                    }
                    if (el.TryGetProperty("geometry", out var geom)) w.Geometry = ReadGeometry(geom);
                    data.Ways.Add(w);
                    break;
                }
                case "relation":
                {
                    var r = new OsmRelation { Id = id, Tags = ReadTags(el) };
                    if (el.TryGetProperty("members", out var members))
                    {
                        foreach (var m in members.EnumerateArray())
                        {
                            r.Members.Add(new OsmMember
                            {
                                Type = m.GetProperty("type").GetString() ?? "",
                                Role = m.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "",
                                Geometry = m.TryGetProperty("geometry", out var g) ? ReadGeometry(g) : null,
                            });
                        }
                    }
                    data.Relations.Add(r);
                    break;
                }
            }
        }
        return data;
    }

    private static Dictionary<string, string> ReadTags(JsonElement el)
    {
        var tags = new Dictionary<string, string>();
        if (el.TryGetProperty("tags", out var t))
            foreach (var p in t.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
        return tags;
    }

    private static List<LatLon> ReadGeometry(JsonElement geom)
    {
        var list = new List<LatLon>(geom.GetArrayLength());
        foreach (var p in geom.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue; // Overpass may emit null for missing nodes
            list.Add(new LatLon(p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()));
        }
        return list;
    }
}

/// <summary>Downloads OSM data via the Overpass API, with retries and a file cache.</summary>
public static class Overpass
{
    private static readonly string[] Endpoints =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
    };

    private const string RoadRe =
        "motorway|motorway_link|trunk|trunk_link|primary|primary_link|secondary|secondary_link|" +
        "tertiary|tertiary_link|unclassified|residential|living_street|service|busway|bus_guideway|pedestrian|road";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(150),
        };
        // Public OSM services ask automated clients to identify themselves.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Stations/0.1 (transit simulation game prototype)");
        return c;
    }

    public static string BuildQuery(Area area)
    {
        var (s, w, n, e) = Projection.BBox(area);
        string bbox = string.Join(",", new[] { s, w, n, e }.Select(v => v.ToString("F6", CultureInfo.InvariantCulture)));
        return $$"""
            [out:json][timeout:180][bbox:{{bbox}}];
            way["highway"~"^({{RoadRe}})$"]->.r;
            .r out body qt;
            .r >;
            out skel qt;
            (
              way["building"];
              way["railway"~"^(rail|light_rail|subway|tram)$"];
              way["natural"="water"];
              way["waterway"~"^(river|canal|riverbank)$"];
              way["landuse"~"^(grass|forest|meadow|recreation_ground|cemetery|residential|commercial|retail|industrial|railway)$"];
              way["leisure"~"^(park|garden|pitch|playground)$"];
              relation["natural"="water"];
              relation["waterway"="riverbank"];
              relation["leisure"="park"];
              relation["landuse"~"^(forest|grass|cemetery)$"];
            );
            out geom qt;
            """;
    }

    public static string CacheKey(Area a) => string.Create(CultureInfo.InvariantCulture, $"{a.Lat:F4}_{a.Lon:F4}_{a.Radius:F0}");

    /// <summary>Returns the raw Overpass JSON for an area, from <paramref name="cacheDir"/> if present.</summary>
    public static async Task<string> LoadAsync(Area area, string cacheDir, Action<string> status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        string file = Path.Combine(cacheDir, CacheKey(area) + ".json");
        if (File.Exists(file))
        {
            status("Kartendaten aus dem Cache geladen");
            return await File.ReadAllTextAsync(file, ct);
        }
        string query = BuildQuery(area);
        Exception? last = null;
        // Busy servers answer 429/502/504 and ask clients to retry with backoff.
        for (int round = 0; round < 3; round++)
        {
            foreach (string url in Endpoints)
            {
                string host = new Uri(url).Host;
                try
                {
                    status($"Lade OpenStreetMap-Daten von {host} … (Versuch {round + 1})");
                    using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
                    using var res = await Http.PostAsync(url, content, ct);
                    if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
                    status("Empfange Kartendaten …");
                    string json = await res.Content.ReadAsStringAsync(ct);
                    if (!json.Contains("\"elements\"")) throw new InvalidDataException("Keine Daten für dieses Gebiet");
                    await File.WriteAllTextAsync(file, json, ct);
                    return json;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    last = ex is TaskCanceledException ? new TimeoutException("Zeitüberschreitung") : ex;
                }
            }
            int wait = 5 << round;
            for (int sec = wait; sec > 0; sec--)
            {
                status($"Kartenserver ausgelastet – neuer Versuch in {sec} s …");
                await Task.Delay(1000, ct);
            }
        }
        throw new IOException($"OSM-Daten konnten nicht geladen werden ({last?.Message}). Bitte später erneut versuchen.");
    }

    /// <summary>Search a place by name via OSM Nominatim.</summary>
    public static async Task<List<(string Name, double Lat, double Lon)>> SearchPlaceAsync(string q, CancellationToken ct = default)
    {
        string url = $"https://nominatim.openstreetmap.org/search?format=json&limit=6&q={Uri.EscapeDataString(q)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.AcceptLanguage.ParseAdd("de");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var list = new List<(string, double, double)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            list.Add((r.GetProperty("display_name").GetString() ?? "",
                double.Parse(r.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(r.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture)));
        }
        return list;
    }
}
