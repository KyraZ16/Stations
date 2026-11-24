using System.Diagnostics;
using Stations.Core;
using Stations.Core.Map;
using Stations.Core.Sim;

// Smoke test for the engine-independent core: real OSM data, bus + tram network, one simulated day,
// save/load round trip. Exit code 1 if a check fails.

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "OK" : "FEHLER")}] {what}");
    if (!ok) failures++;
}

var area = new Area("Hamburg Innenstadt", 53.5530, 9.9930, 2500);
string cacheDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cache");
var sw = Stopwatch.StartNew();

Console.WriteLine("== Karte laden");
string json = await Overpass.LoadAsync(area, cacheDir, msg => Console.WriteLine("  " + msg));
var osm = OsmData.Parse(json);
var map = MapData.Build(area, osm);
Console.WriteLine($"  {sw.ElapsedMilliseconds} ms · Straßen {map.Roads.Count} · Segmente {map.Graph.SegmentCount} · " +
                  $"Gebäude {map.Buildings.Count} · Zellen {map.Cells.Count} · Flächen {map.Polys.Count}");
Console.WriteLine($"  Einwohner {map.TotalResidents:F0} · Arbeitsplätze {map.TotalJobs:F0}");
Check(map.Graph.SegmentCount > 1000, "Straßengraph aufgebaut");
Check(map.TotalResidents > 10_000 && map.TotalJobs > 10_000, "Einwohner und Arbeitsplätze erkannt");

Console.WriteLine("== Busnetz bauen");
var game = new Game(map, sandbox: true, seed: 42);
var errors = new List<string>();
game.Toast += (msg, isError) => { if (isError) errors.Add(msg); };

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

var depot = game.AddDepot(map.Graph.Snap(-200, -900, 200)!.Value)!;
var l1 = game.CreateLine(MakeStops(ModeId.Bus, (-1600, -600), (-1100, -600), (-600, -600), (-100, -600), (400, -600), (900, -600), (1400, -600)), false)!;
var l2 = game.CreateLine(MakeStops(ModeId.Bus, (300, -1500), (300, -1000), (300, -500), (300, 0), (300, 500)), false)!;
var l3 = game.CreateLine(MakeStops(ModeId.Bus, (600, 600), (1100, 600), (1100, 1000), (600, 1000)), true)!;
foreach (var l in new[] { l1, l2, l3 }) game.AssignMissing(l);
int hired = game.HireMissingDrivers();
Console.WriteLine($"  Linien: {string.Join(", ", game.Lines.Values.Select(l => $"{l.Name} ({l.StopIds.Count} Halte, Umlauf {Util.Duration(l.CycleTime)}, Pool {game.VehiclesOf(l).Count})"))}");
Console.WriteLine($"  Fahrzeuge {game.Vehicles.Count} · Fahrer eingestellt {hired}");
Check(game.Lines.Count == 3 && errors.Count == 0, "drei Buslinien ohne Fehler angelegt");

Console.WriteLine("== Tramnetz bauen");
RoadPos Snap(double x, double y) => map.Graph.Snap(x, y, 200)!.Value;
var trackPts = new[] { (-900.0, -300.0), (-300, -350), (300, -300), (700, 100) };
for (int i = 0; i + 1 < trackPts.Length; i++)
    game.BuildTrack(Snap(trackPts[i].Item1, trackPts[i].Item2), Snap(trackPts[i + 1].Item1, trackPts[i + 1].Item2));
var tramDepot = game.AddDepot(game.SnapFor(ModeId.Tram, -880, -300, 60)!.Value, ModeId.Tram);
var tram = game.CreateLine(MakeStops(ModeId.Tram, (-800, -310), (-450, -340), (-100, -330), (300, -300), (650, 80)), false, ModeId.Tram);
if (tram != null) game.AssignMissing(tram);
game.HireMissingDrivers();
Console.WriteLine($"  Gleise {Util.Dist(game.TrackLength)} · Tramlinie {(tram == null ? "-" : $"{tram.StopIds.Count} Halte")}");
Check(game.Tracks.Count > 0 && tramDepot != null && tram != null, "Gleise, Tram-Betriebshof und Tramlinie gebaut");

Console.WriteLine("== Einen Tag simulieren (05:00 bis 05:00)");
sw.Restart();
int offTrack = 0, maxActive = 0;
double maxTramSpeed = 0;
for (int t = 0; t < 86400; t += 60)
{
    game.Advance(60);
    foreach (var v in game.Vehicles.Values)
    {
        if (v.Mode != ModeId.Tram || v.State == VehicleState.Depot) continue;
        maxTramSpeed = Math.Max(maxTramSpeed, v.V);
        if (game.SnapFor(ModeId.Tram, v.X, v.Y, 5) == null) offTrack++;
    }
    maxActive = Math.Max(maxActive, game.Vehicles.Values.Count(v => v.State != VehicleState.Depot));
}
var day1 = game.History.LastOrDefault();
Console.WriteLine($"  {sw.ElapsedMilliseconds} ms Rechenzeit für 24 h Spielzeit");
if (day1 != null)
{
    Console.WriteLine($"  Tag {day1.Day}: Fahrgäste {day1.Trips} · Einstiege {day1.Boardings} · aufgegeben {day1.Lost} · " +
                      $"Ø Wartezeit {Util.Duration(day1.Boardings > 0 ? day1.WaitSum / day1.Boardings : double.NaN)}");
    Console.WriteLine($"  Einnahmen {Util.Money(day1.Revenue)} · Kosten {Util.Money(day1.Costs)}");
}
Console.WriteLine($"  max. gleichzeitig unterwegs {maxActive} · Tram max. {maxTramSpeed * 3.6:F0} km/h · " +
                  $"Fahrer max. Schicht {game.Drivers.Values.Max(d => d.Worked) / 3600:F2} h");
Check(day1 != null && day1.Trips > 500, "Fahrgäste befördert");
Check(tram != null && tram.PassengersToday >= 0 && offTrack == 0, "Trams bleiben auf den Gleisen");
Check(game.Drivers.Values.All(d => d.Worked <= Game.ShiftMax + 60), "keine Schicht über 8 h");
Check(game.Lines.Values.All(l => !l.DriverShortage), "kein Fahrermangel");

Console.WriteLine("== Linie im Betrieb bearbeiten");
game.Advance(3 * 3600); // into the morning peak
var editStops = l1.StopIds.ToList();
editStops.RemoveAt(1);
bool edited = game.SetLineStops(l1, editStops);
game.Advance(1800);
Check(edited && game.ActiveVehicles(l1) > 0, "Linie geändert, Fahrzeuge fahren weiter");

Console.WriteLine("== Speichern und Laden");
string saveJson = game.Serialize().ToJson();
var restored = Game.Restore(map, SaveData.FromJson(saveJson));
Console.WriteLine($"  Spielstand {saveJson.Length / 1024} KB");
Check(restored.Lines.Count == game.Lines.Count && restored.Stops.Count == game.Stops.Count &&
      restored.Vehicles.Count == game.Vehicles.Count && restored.Drivers.Count == game.Drivers.Count &&
      restored.Tracks.SetEquals(game.Tracks), "alles wiederhergestellt");
restored.Advance(1800);
Check(restored.Vehicles.Values.Any(v => v.State != VehicleState.Depot), "geladenes Spiel läuft weiter");

Console.WriteLine(failures == 0 ? "\nAlle Prüfungen bestanden." : $"\n{failures} Prüfung(en) fehlgeschlagen.");
return failures == 0 ? 0 : 1;
