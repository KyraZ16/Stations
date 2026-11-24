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

## Stand

- [x] Kern portiert und getestet (gleiche Kartendaten und Ergebnisse wie im Browser)
- [ ] Kartenansicht (2D) mit Zoom und Verschieben
- [ ] Werkzeuge und Panels (Haltestelle, Linie, Betriebshof, Gleise, Fahrplan, Personal)
- [ ] Depot-Builder (3D)
