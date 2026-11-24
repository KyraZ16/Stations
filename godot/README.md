# Stations – Godot-Version

Umzug des Browser-Prototyps (Projektwurzel, TypeScript) nach **Godot 4.7 (.NET) mit C#**.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `Core/` | Spiellogik ohne Godot-Abhängigkeit: OSM-Download und -Import, Straßengraph und Routing, Simulation (Linien, Fahrgäste, Fahrplan, Personal, Tram), Speichern und Laden. 1:1 aus `src/` portiert. |
| `Tests/` | Konsolentest: lädt Hamburg, baut Bus- und Tramlinien, simuliert einen Tag und prüft Speichern und Laden. |
| `Game/` | Das Godot-Projekt (Szenen, Darstellung, Bedienung). Bindet `Core` ein. |

## Benutzen

- **Im Editor öffnen:** Godot (.NET) starten → *Importieren* → `godot/Game/project.godot`.
- **Kern testen:** `dotnet run --project Tests/Stations.Tests.csproj -c Release`
  (der erste Lauf lädt OSM-Daten, danach kommen sie aus `Tests/cache/`)
- **Alles bauen:** `dotnet build Stations.slnx`

## Entwickler-Optionen

Nach `--` auf der Kommandozeile, z. B.
`Godot_v4.7.2-stable_mono_win64.exe --path godot/Game -- --autostart --demo`:

- `--autostart` startet direkt Hamburg (Sandbox)
- `--demo` baut ein Beispielnetz und spult auf 08:00 (im Spiel auch per **F9**)
- `--zoom=3` zoomt hinein, `--shot=bild.png` speichert einen Screenshot und beendet das Spiel
- `--tooltest=line|timetable|depot|vehicle|stop|track|draft` baut über die Werkzeuge ein kleines Netz und öffnet das genannte Panel
- `--debugui` listet Panel-Elemente mit auffällig großer Mindestbreite

## Stand

- [x] Kern portiert und getestet (gleiche Kartendaten und Ergebnisse wie im Browser)
- [x] Kartenansicht (2D): Startbildschirm mit Städten und Ortssuche, Karte mit Zoom/Verschieben, Linien, Gleise, Fahrzeuge, Haltestellen, Kopfleiste, Speichern (F5)
- [x] Werkzeuge und Panels: Bus/Tram, Haltestelle, Linie zeichnen und bearbeiten, Betriebshof, Gleise, Abreißen, Einzugsgebiet; Panels für Übersicht mit Hinweisen, Linie (Übersicht/Fahrplan/Strecke), Haltestelle, Fahrzeug, Betriebshof mit Personal
- [ ] Depot-Builder (3D)
