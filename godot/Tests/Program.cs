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

Console.WriteLine("== Betriebshof-Layout");
var yard = DepotLayout.Starter(ModeId.Bus);
Check(yard.VehicleCapacity(ModeId.Bus) == 8 && yard.DriverCapacity() == DepotLayout.DriversPerOffice, $"Start-Layout: {yard.VehicleCapacity(ModeId.Bus)} Stellplätze, {yard.DriverCapacity()} Fahrerplätze");
var lonely = new PlacedModule { Type = ModuleType.BusSpot, X = 1, Y = 9 };
yard.Modules.Add(lonely);
Check(yard.VehicleCapacity(ModeId.Bus) == 8, "Stellplatz ohne Anschluss an den Fahrweg zählt nicht");
yard.Modules.Remove(lonely);
int leftSpot = yard.Modules.Where(m => m.Type == ModuleType.BusSpot).Min(m => m.X);
var crossRoad = yard.Modules.First(m => m.Type == ModuleType.Road && m.Y == 7 && m.X == leftSpot + 1);
yard.Modules.Remove(crossRoad);
Check(yard.VehicleCapacity(ModeId.Bus) < 8, $"unterbrochener Fahrweg trennt Stellplätze ab ({yard.VehicleCapacity(ModeId.Bus)} übrig)");
Check(yard.CanPlace(ModuleType.Office, yard.Width / 2 - 1, yard.Height - 3, 0) != null, "Einfahrt bleibt frei");
var tramYard = DepotLayout.Starter(ModeId.Tram);
Check(tramYard.VehicleCapacity(ModeId.Tram) == 4, $"Tram-Start-Layout: {tramYard.VehicleCapacity(ModeId.Tram)} Abstellgleise");
var big = DepotLayout.Starter(ModeId.Bus, minVehicles: 14, minDrivers: 40);
Check(big.VehicleCapacity(ModeId.Bus) >= 14 && big.DriverCapacity() >= 40, "altes Spielstand-Layout passt sich an Fahrzeuge und Fahrer an");

var yardGame = new Game(map, sandbox: true, seed: 7);
var yardDepot = yardGame.AddDepot(map.Graph.Snap(-200, -900, 300)!.Value)!;
int bought = 0;
while (yardGame.BuyVehicle(yardDepot.Id) != null && bought < 20) bought++;
Check(bought == 8, $"Kauf endet bei vollem Hof ({bought} Busse)");
Check(yardGame.PlaceModule(yardDepot, ModuleType.BusSpot, 1, 9, 1) == null, "zusätzlichen Stellplatz gebaut");
Check(yardGame.RemoveModuleAt(yardDepot, crossRoad.X, crossRoad.Y) != null, "Fahrweg mit belegten Stellplätzen lässt sich nicht abreißen");
var yardRestored = Game.Restore(map, SaveData.FromJson(yardGame.Serialize().ToJson()));
Check(yardRestored.Depots[yardDepot.Id].Layout.Modules.Count == yardDepot.Layout.Modules.Count, "Layout wird gespeichert und geladen");

Console.WriteLine("== Verschleiß und Wartung (je 14 Tage)");
(double AvgCond, int Breakdowns, double Repairs) RunFortnight(bool workshops)
{
    var gm = new Game(map, sandbox: true, seed: 11);
    DemoNetwork.Build(gm);
    if (workshops)
    {
        foreach (var d in gm.Depots.Values)
        {
            int y = d.Mode == ModeId.Tram ? 14 : 8;
            string? err = gm.PlaceModule(d, ModuleType.Workshop, 2, y, 0);
            if (err != null) Console.WriteLine($"  Werkstatt nicht gebaut: {err}");
            gm.HireWorkers(d.Id, StaffRole.Mechanic, gm.WorkerDemand(d.Id, StaffRole.Mechanic));
        }
    }
    double costsBefore = 0;
    for (int day = 0; day < 14; day++) gm.Advance(86400);
    costsBefore = gm.History.Sum(h => h.Costs);
    return (gm.Vehicles.Values.Average(v => v.Condition), gm.Vehicles.Values.Sum(v => v.Breakdowns), costsBefore);
}
var noWs = RunFortnight(false);
var withWs = RunFortnight(true);
Console.WriteLine($"  ohne Werkstatt: Ø Zustand {noWs.AvgCond:P0}, {noWs.Breakdowns} Pannen");
Console.WriteLine($"  mit Werkstatt:  Ø Zustand {withWs.AvgCond:P0}, {withWs.Breakdowns} Pannen");
Check(noWs.AvgCond < 0.6, "ohne Werkstatt verschleißen die Fahrzeuge");
Check(withWs.AvgCond > noWs.AvgCond + 0.2, "Werkstatt hält die Flotte in Schuss");
Check(withWs.Breakdowns < noWs.Breakdowns, "mit Werkstatt weniger Pannen");

var eg = new Game(map, sandbox: true, seed: 5);
var eDepot = eg.AddDepot(map.Graph.Snap(-200, -900, 300)!.Value)!;
var eLine = eg.CreateLine(new List<int>
{
    eg.AddStop(map.Graph.Snap(-1600, -600, 200)!.Value)!.Id,
    eg.AddStop(map.Graph.Snap(400, -600, 200)!.Value)!.Id,
}, false)!;
eLine.PreferredModel = "ebus";
eg.AssignMissing(eLine);
eg.HireMissingDrivers();
Check(eg.VehiclesOf(eLine).All(v => v.Def.Electric), "Linie kauft ihr Wunschmodell (E-Bus)");
eg.Advance(86400);
Check(eg.Vehicles.Values.Any(v => v.Charge < 0.6) && eLine.MaintenanceShortage, "E-Busse ohne Ladestation fallen nach und nach aus");
Console.WriteLine("  " + (eg.PlaceModule(eDepot, ModuleType.Charger, eg.VehiclesOf(eLine)[0].SpotX, 1, 0) ?? "Ladestation 1 gebaut"));
Console.WriteLine("  " + (eg.PlaceModule(eDepot, ModuleType.Charger, eg.VehiclesOf(eLine)[1].SpotX, 1, 0) ?? "Ladestation 2 gebaut"));
eg.Advance(6 * 3600);
var inYard = eg.Vehicles.Values.Where(v => v.State == VehicleState.Depot).ToList();
Check(inYard.Count > 0 && inYard.All(v => v.Charge > 0.9), "Ladestationen laden die E-Busse im Hof");
Check(eg.BuyVehicle(eDepot.Id, "gelenkbus") == null, "Gelenkbus braucht einen langen Stellplatz");
eg.PlaceModule(eDepot, ModuleType.LongBusSpot, 3, 0, 0);
Check(eg.BuyVehicle(eDepot.Id, "gelenkbus") != null, "mit Gelenkbus-Stellplatz klappt der Kauf");
var eRestored = Game.Restore(map, SaveData.FromJson(eg.Serialize().ToJson()));
Check(eRestored.Vehicles.Values.Select(v => (v.ModelId, Math.Round(v.Charge, 3))).SequenceEqual(eg.Vehicles.Values.Select(v => (v.ModelId, Math.Round(v.Charge, 3)))),
    "Modell und Akkustand werden gespeichert");

Console.WriteLine("== Leben im Betriebshof");
var yg = new Game(map, sandbox: true, seed: 3);
var yDepot = yg.AddDepot(map.Graph.Snap(-200, -900, 300)!.Value)!;
var yLine = yg.CreateLine(new List<int>
{
    yg.AddStop(map.Graph.Snap(-1600, -600, 200)!.Value)!.Id,
    yg.AddStop(map.Graph.Snap(400, -600, 200)!.Value)!.Id,
}, false)!;
yLine.Timetable.Headways = new double[] { 300, 300, 300, 300, 300 }; // dense: many buses leave at once
for (int i = 0; i < 8; i++) yg.BuyVehicle(yDepot.Id);
foreach (var v in yg.Vehicles.Values) yg.AssignVehicle(v.Id, yLine.Id);
yg.HireDrivers(yDepot.Id, 25);
var spotsUsed = yg.Vehicles.Values.Select(v => (v.SpotX, v.SpotY)).Distinct().Count();
Check(spotsUsed == 8 && yg.Vehicles.Values.All(v => v.SpotX >= 0), "jedes Fahrzeug hat einen eigenen Stellplatz");
bool sawYard = false;
double lastLeft = 0, maxWait = 0;
for (int s = 0; s < 600; s++)
{
    yg.Advance(1);
    foreach (var v in yg.Vehicles.Values)
    {
        if (v.State == VehicleState.Yard && v.Task == YardTask.Exit) { sawYard = true; maxWait = Math.Max(maxWait, v.YardWait); }
    }
    if (yg.Vehicles.Values.Count(v => !Game.InYard(v)) >= yg.RequiredVehicles(yLine) && lastLeft == 0) lastLeft = s;
}
Check(sawYard, "Fahrzeuge fahren sichtbar aus dem Hof");
Check(lastLeft > 0, $"alle {yg.RequiredVehicles(yLine)} benötigten Busse haben den Hof verlassen (nach {lastLeft:F0} s)");
Check(maxWait > 0, $"an der Einfahrt staut es sich (bis {maxWait:F0} s Wartezeit)");

var worn = yg.Vehicles.Values.First();
worn.Condition = 0.2; // will go for service at the terminus
yg.PlaceModule(yDepot, ModuleType.Workshop, 2, 8, 0);
bool sawWorkshop = false, backHome = false;
// Without mechanics the workshop stays closed
yg.Advance(2 * 3600);
Check(!(worn.State == VehicleState.Depot && worn.Bay == YardBay.Workshop) && worn.Condition < 0.3, "ohne Mechaniker bleibt die Werkstatt zu");
Check(yg.PlaceModule(yDepot, ModuleType.Office, 16, 8, 0) == null, "zweites Sozialgebäude gebaut (Platz für Mechaniker)");
yg.HireWorkers(yDepot.Id, StaffRole.Mechanic, 6);
Check(yg.WorkersOf(yDepot.Id, StaffRole.Mechanic).Select(w => w.Shift).Distinct().Count() == 3, "Mechaniker auf Früh-, Spät- und Nachtschicht verteilt");
for (int s = 0; s < 8 * 3600; s += 10)
{
    yg.Advance(10);
    if (worn.State == VehicleState.Depot && worn.Bay == YardBay.Workshop) sawWorkshop = true;
    if (sawWorkshop && worn.Condition > 0.99 && worn.Bay == YardBay.Spot) { backHome = true; break; }
}
Check(sawWorkshop, "abgenutzter Bus fährt in die Werkstatt");
Check(backHome, "nach der Reparatur zurück auf den Stellplatz");

Console.WriteLine("== Gebäude frei aufziehen");
var bg = new Game(map, sandbox: true, seed: 4);
var bDepot = bg.AddDepot(map.Graph.Snap(-200, -900, 300)!.Value)!;
int capBefore = bDepot.Layout.DriverCapacity();
Check(bg.PlaceModule(bDepot, ModuleType.Office, 7, 8, 0, 1, 3) != null, "zu kleines Sozialgebäude (1×3) wird abgelehnt");
Check(bg.PlaceModule(bDepot, ModuleType.Office, 7, 8, 0, 4, 4) == null, "Sozialgebäude 4×4 aufgezogen");
Check(bDepot.Layout.DriverCapacity() - capBefore == 4 * 4 * DepotLayout.StaffPerCell, $"4×4 bringt {4 * 4 * DepotLayout.StaffPerCell} Mitarbeiterplätze");
var bigOffice = bDepot.Layout.ModuleAt(7, 8)!;
var bigPlan = DepotLayout.OfficePlanFor(bigOffice, bDepot.Layout.OfficeDoor(bigOffice).Dir);
Check(bigPlan.Seats.Count > 8, $"größerer Pausenraum ({bigPlan.Seats.Count} Sitzplätze statt 8)");
Check(bg.PlaceModule(bDepot, ModuleType.Workshop, 1, 8, 0, 5, 5) != null, "zu kurze Werkstatt (5×5) wird abgelehnt");
Check(bg.PlaceModule(bDepot, ModuleType.Workshop, 1, 8, 0, 6, 6) == null && DepotLayout.Lanes(bDepot.Layout.ModuleAt(1, 8)!) == 3, "Werkstatt 6×6 hat 3 Spuren");
Check(bg.WorkerDemand(bDepot.Id, StaffRole.Mechanic) == 9, "3 Spuren brauchen 9 Mechaniker (3 Schichten)");
var bRestored = Game.Restore(map, SaveData.FromJson(bg.Serialize().ToJson()));
Check(bRestored.Depots[bDepot.Id].Layout.DriverCapacity() == bDepot.Layout.DriverCapacity() &&
      DepotLayout.Lanes(bRestored.Depots[bDepot.Id].Layout.ModuleAt(1, 8)!) == 3, "gezeichnete Größen werden gespeichert");

Console.WriteLine("== Personal auf dem Gelände");
var mechOnSite = yg.People.Values.Where(p => p.Role == StaffRole.Mechanic).ToList();
var onDuty = yg.WorkersOf(yDepot.Id, StaffRole.Mechanic).Count(w => Game.OnDuty(w.Shift, yg.Time));
Check(mechOnSite.Count <= onDuty + 2 && mechOnSite.Count > 0, $"nur die Schicht im Dienst ist da ({mechOnSite.Count} Mechaniker auf dem Gelände)");
Check(yg.People.Values.Any(p => p.Role == StaffRole.Driver && p.Office != null), "Fahrer warten im Sozialgebäude");

// A dispatched bus waits for its driver to walk over
var pg = new Game(map, sandbox: true, seed: 8);
var pDepot = pg.AddDepot(map.Graph.Snap(-200, -900, 300)!.Value)!;
var pLine = pg.CreateLine(new List<int>
{
    pg.AddStop(map.Graph.Snap(-1600, -600, 200)!.Value)!.Id,
    pg.AddStop(map.Graph.Snap(400, -600, 200)!.Value)!.Id,
}, false)!;
pg.AssignMissing(pLine);
pg.HireMissingDrivers();
pg.Advance(2); // people take their places
bool sawBoarding = false, sawWalker = false;
double firstOut = -1;
for (int s = 0; s < 900 && firstOut < 0; s++)
{
    pg.Advance(1);
    sawBoarding |= pg.Vehicles.Values.Any(v => v.Task == YardTask.WaitDriver);
    sawWalker |= pg.People.Values.Any(p => p.Goal == PersonGoal.Vehicle && p.Act == PersonAct.Walking);
    if (pg.Vehicles.Values.Any(v => v.State == VehicleState.Yard && v.Task == YardTask.Exit)) firstOut = s;
}
Check(sawBoarding && sawWalker, "Bus wartet, bis der Fahrer hingelaufen ist");
Check(firstOut > 5, $"Abfahrt nach dem Fußweg ({firstOut:F0} s)");

// Spare cleaners clean parked vehicles from the inside
var dirty = pg.Vehicles.Values.First(v => v.State == VehicleState.Depot);
dirty.Cleanliness = 0.5;
pg.HireWorkers(pDepot.Id, StaffRole.Cleaner, 3);
double cleanBefore = dirty.Cleanliness;
for (int s = 0; s < 24 * 3600 && dirty.Cleanliness < 0.9; s += 30) pg.Advance(30);
Check(dirty.Cleanliness > cleanBefore + 0.3, $"Reinigungskraft reinigt geparkten Bus innen ({cleanBefore:P0} → {dirty.Cleanliness:P0})");
var pRestored = Game.Restore(map, SaveData.FromJson(pg.Serialize().ToJson()));
Check(pRestored.Workers.Count == pg.Workers.Count && pRestored.Workers.Values.All(w => pg.Workers[w.Id].Shift == w.Shift && pg.Workers[w.Id].Role == w.Role),
    "Mechaniker und Reinigungskräfte werden gespeichert");
pRestored.Advance(2);
Check(pRestored.People.Count > 0, "nach dem Laden ist das Personal wieder da");

Console.WriteLine(failures == 0 ? "\nAlle Prüfungen bestanden." : $"\n{failures} Prüfung(en) fehlgeschlagen.");
return failures == 0 ? 0 : 1;
