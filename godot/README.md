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
- `--time=2` spult auf die angegebene Uhrzeit vor (z. B. nachts, wenn alle Fahrzeuge im Hof stehen)
- `--depot` öffnet den ersten Betriebshof im 3D-Editor (`--depot=tram` den Tram-Betriebshof, `--depot=edit` mit Beispiel-Bauteilen)
- `--days=12` spult Tage vor (z. B. um Verschleiß zu sehen), `--select=depot|vehicle|line` öffnet ein Panel
- `--interior` öffnet den Betriebshof in der Innenansicht; `--camdist=30`, `--camyaw=0.6`, `--campitch=0.8` und `--camfocus=office|workshop|wash` stellen die Kamera ein
- `--debugui` listet Panel-Elemente mit auffällig großer Mindestbreite

## Stand

- [x] Kern portiert und getestet (gleiche Kartendaten und Ergebnisse wie im Browser)
- [x] Kartenansicht (2D): Startbildschirm mit Städten und Ortssuche, Karte mit Zoom/Verschieben, Linien, Gleise, Fahrzeuge, Haltestellen, Kopfleiste, Speichern (F5)
- [x] Werkzeuge und Panels: Bus/Tram, Haltestelle, Linie zeichnen und bearbeiten, Betriebshof, Gleise, Abreißen, Einzugsgebiet; Panels für Übersicht mit Hinweisen, Linie (Übersicht/Fahrplan/Strecke), Haltestelle, Fahrzeug, Betriebshof mit Personal
- [x] Depot-Builder (3D), erste Version: Grundstück mit Raster und Einfahrt, Fahrwege, Stellplätze/Abstellgleise, Sozialgebäude, Werkstatt, Waschanlage; Stellplätze und Gebäude zählen nur mit Anschluss an die Einfahrt; Stellplätze begrenzen Fahrzeuge, Sozialgebäude die Fahrer; geparkte Fahrzeuge sichtbar
- [x] Wartung und Verschleiß: Zustand sinkt mit den Kilometern (Pannen unter 50 %), Sauberkeit mit der Einsatzzeit (weniger Fahrgäste), Werkstatt repariert, Waschanlage wäscht, Ladestationen laden E-Busse; Disposition schickt nur einsatzbereite Fahrzeuge; Fahrzeugmodelle Solobus, Gelenkbus (langer Stellplatz), E-Bus
- [x] Leben im Betriebshof: Fahrzeuge fahren durch die Einfahrt über die Fahrwege zu ihrem Heim-Stellplatz, in die Werkstatt (2 Spuren), durch die Waschanlage und wieder hinaus; Abstand halten, Vorfahrt bei Gegenverkehr, Stau bei engen Layouts; Ladestationen laden den Bus auf dem Stellplatz daneben
- [x] Ladescreen mit Schritten, Fortschritt, Abbrechen und Erneut versuchen
- [x] Bessere 3D-Optik im Betriebshof: prozedurale Busse (Räder, Türen, Fensterband, Lichter, Zielanzeige; Gelenkbus mit Faltenbalg, E-Bus mit Akkus) und Trams, Sozialgebäude mit Fensterbändern, Stahlhallen, Ladesäulen; Tag/Nacht nach Spielzeit mit Laternen, leuchtenden Fenstern und Scheinwerfern; Straße, Gehwege, Bäume, strukturierte Oberflächen
- [x] Bessere Kartenansicht: Straßennamen entlang der Straßen (nach Bedeutung und Zoom, ohne Überlappung), Linien auf gemeinsamen Straßen nebeneinander wie im Netzplan, Fahrzeuge größer mit Liniennummer, Minikarte unten links (Klicken/Ziehen springt), Hinweise in der Übersicht umbrechen
- [x] Personal im Betriebshof: Fahrer, Mechaniker und Reinigungskräfte als Figuren; Sozialgebäude mit Umkleide, Pausenraum und Dispo zum Hineinsehen (Innenansicht mit I oder nah heranzoomen), Werkstatt mit Hebebühnen; Fahrer gehen zu Fuß zum Fahrzeug (Abfahrt erst, wenn sie eingestiegen sind), Mechaniker und Reinigungskräfte arbeiten in Früh-/Spät-/Nachtschicht – ohne sie bleiben Werkstattspuren und Waschanlagen zu; freie Reinigungskräfte putzen geparkte Fahrzeuge innen; Maus über eine Person zeigt Name und Tätigkeit
- [ ] Später: Service-Mitarbeiter und Kunden (Kundencenter)
- [ ] U-Bahn, Kampagne
