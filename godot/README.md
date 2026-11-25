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
- `--sbahn` baut zusätzlich eine Beispiel-S-Bahn (S1 entlang der längsten Bahnstrecke); `--depot=sbahn` öffnet ihren Betriebshof
- `--metro` baut zusätzlich eine Beispiel-U-Bahn (U1) und wechselt auf „Stadtweit“; `--depot=metro` öffnet ihren Betriebshof
- `--demo` baut ein Beispielnetz und spult auf 08:00 (im Spiel auch per **F9**)
- `--zoom=3` zoomt hinein, `--shot=bild.png` speichert einen Screenshot und beendet das Spiel
- `--tooltest=line|timetable|depot|vehicle|stop|track|draft` baut über die Werkzeuge ein kleines Netz und öffnet das genannte Panel
- `--time=2` spult auf die angegebene Uhrzeit vor (z. B. nachts, wenn alle Fahrzeuge im Hof stehen)
- `--depot` öffnet den ersten Betriebshof im 3D-Editor (`--depot=tram` den Tram-Betriebshof, `--depot=edit` mit Beispiel-Bauteilen)
- `--days=12` spult Tage vor (z. B. um Verschleiß zu sehen), `--select=depot|vehicle|line|research` öffnet ein Panel
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
- [x] Gebäude frei aufziehen: Sozialgebäude, Werkstatt und Waschanlage als Rechteck beliebiger Größe (Klick = Standardgröße); Preis nach Fläche; Sozialgebäude 3 Mitarbeiterplätze je Feld, Pausenraum, Spinde und Dispo wachsen mit; Werkstatt und Waschanlage je 2 Felder Breite eine Spur (mit Hebebühnen bzw. Bürsten und eigenem Personal)
- [x] Kundencenter (frei aufziehbar, 06–22 Uhr): Kunden kommen von der Straße, warten auf Stühlen oder stehen, werden an Schaltern von Servicekräften (Früh-/Spätschicht) bedient und kaufen Abos (49 € sofort, für etwa einen Monat bis zu +25 % Fahrgäste); ohne freien Schalter gehen sie nach 20 Minuten wieder
- [x] Forschung: Punkte aus dem Betrieb (2 je 100 Einstiege, 0,5 je Kunde im Kundencenter) starten Projekte, die dann Forscher-Stunden brauchen; Forscher sitzen im frei aufziehbaren Planungsbüro (Früh-/Spätschicht), mehr Forscher = schneller fertig. Bezahlstufen 1–4 (Bargeld beim Fahrer → Karte am Automaten → kontaktlos am Automaten + Automat im Fahrzeug → kontaktlos im Fahrzeug) beschleunigen den Einstieg und bringen bis zu +10 % Fahrgäste; außerdem Werkstatt-Diagnose, Reinigungsplan, Abo-Marketing. Gelenkbus (mit Gelenkbus-Stellplatz), Ladestation und E-Bus werden erst erforscht (gesperrt mit 🔒; alte Spielstände behalten, was sie schon nutzen). Knopf „Forschung“ in der Kopfleiste
- [x] U-Bahn (Stadtweit): Tunnel frei durch die Stadt bauen (G, gerade Stücke von Punkt zu Punkt, Andocken an bestehende Tunnel, 30.000 €/m), U-Bahnhöfe (H, 800 m Einzugsgebiet, mindestens 250 m Abstand) und U-Bahn-Betriebshof (B) auf Tunneln, Linien U1, U2 …; Züge (40 m, 330 Plätze, 80 km/h) mit schnellem Einstieg an allen Türen; Umstieg zu Bus und Tram zu Fuß; 3D-Hof mit 42-m-Abstellgleisen
- [x] S-Bahn (Stadtweit): fährt auf den echten Bahnstrecken der Karte (OSM rail/light_rail ohne Abstell- und Rangiergleise; im S-Bahn-Modus grün hervorgehoben), eigene Gleise frei dazubauen (G, 12.000 €/m, nur diese lassen sich abreißen), S-Bahnhöfe (1000 m Einzugsgebiet, 400 m Abstand), Linien S1, S2 …, Züge 66 m / 500 Plätze / 100 km/h, Betriebshof mit 69-m-Abstellgleisen
- [ ] Regionalzüge, Kampagne
