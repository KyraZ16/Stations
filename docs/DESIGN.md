# Stations – Design & Architektur

ÖPNV-Simulation im Browser auf echten OpenStreetMap-Daten. Spielprinzip angelehnt an Bus-Management-Spiele
(Linien, Fahrzeuge, Betriebshöfe), Netzplanung mit Fahrgastströmen wie bei U-Bahn-Planungsspielen.

## Vision: drei Netzebenen

| Ebene | Verkehrsmittel | Infrastruktur | Status |
|---|---|---|---|
| **Lokal** | Bus, Tram | Straßen (OSM), Tramgleise | Bus spielbar, Tram geplant |
| **Stadtweit** | U-Bahn, S-Bahn | Tunnel / eigene Trassen | geplant |
| **Regional** | Regionalzug | Bahnstrecken | geplant |

Alle Ebenen teilen sich ein Fahrgast-Routing: Fahrgäste planen Wege über alle Linien und steigen um
(Bus → U-Bahn → Zug). Der Verkehrsmittel-Katalog steht in `src/core/modes.ts`.

Später: 3D-Ansicht nur für Depot-/Stationsbau, Karte bleibt 2D.

## Stand Prototyp 0.1 (Sandbox)

- Startbildschirm: Stadt-Vorlagen oder Ortssuche (Nominatim), Kartengröße 3/5/8 km, Sandbox-Schalter
- OSM-Download über Overpass mit Wiederholungen bei ausgelasteten Servern, Cache in IndexedDB
- 2D-Karte (Canvas): Straßen nach Klasse, Gebäude, Wasser, Parks, Bahnstrecken
- Werkzeuge: Haltestelle (H), Linie zeichnen (L), Betriebshof (B), Abreißen (X), Einzugsgebiet (C)
- Linien folgen dem Straßennetz (A*, Einbahnstraßen beachtet, Busspur-Ausnahmen)
- Busse: Leerfahrt vom Betriebshof, Fahren mit Beschleunigen und Bremsen, Haltezeiten, Wendezeit,
  Taktabstand an der Endhaltestelle
- Fahrgäste: Nachfrage aus Gebäuden (Wohnen → Arbeit, tageszeitabhängig), Routenwahl mit Umstiegen,
  Vergleich mit Auto oder Fußweg, Aufgeben nach 40 min Wartezeit
- Wirtschaft: Fahrpreis, Kosten pro Stunde und km, Kauf und Verkauf von Fahrzeugen
- Speichern und Laden über localStorage, automatisch alle 2 min
- Fahrpläne: Betriebszeit und Takt je Zeitraum (Früh, HVZ, Tag, HVZ, Abend). Jede Linie hat einen
  Fahrzeugpool; die Disposition schickt nur so viele Busse los, wie der Takt braucht, überzählige
  fahren an der Endhaltestelle ein. Abfahrten an der Endhaltestelle im Takt.
- Personal: Fahrer je Betriebshof (Einstellung, Tageslohn), Schicht max. 8 h, Ablösung an der
  Endhaltestelle, Bedarfsschätzung aus den Fahrzeugstunden, Warnungen bei Bus- oder Fahrermangel
- Linien bearbeiten: Haltestellen einfügen (an der Stelle mit dem kleinsten Umweg), entfernen,
  umsortieren, Ringlinie umschalten. Busse im Einsatz fahren weiter zur nächsten Haltestelle der
  neuen Route, Fahrgäste an Bord werden umgeplant (`Game.rebuildLine`)

## Architektur

```
src/
  core/    geo.ts (Projektion), modes.ts (Ebenen/Verkehrsmittel), util.ts
  map/     overpass.ts (Download+Cache), mapdata.ts (OSM → Spielwelt), roadgraph.ts (Graph, Snap, A*)
  sim/     game.ts (Spielzustand, Tick, Bau-Aktionen, Disposition, Save), router.ts (Fahrgast-Routing),
           timetable.ts (Fahrplan-Zeiträume, Takt, Fahrzeugbedarf), staff.ts (Fahrernamen),
           path.ts (Polylinien), types.ts
  render/  camera.ts, renderer.ts (Canvas-2D)
  ui/      start.ts (Startbildschirm), app.ts (Eingabe, Werkzeuge, Panels)
```

- Weltkoordinaten in Metern (lokale Projektion um den Kartenmittelpunkt, y nach Süden).
- Straßengraph: jeder OSM-Knoten ist ein Knoten; Segmente haben Vor- und/oder Rückwärtskante.
- **Linie** = Liste von Haltestellen → *Runs* (Hin- und Rückrichtung, bei Ringlinie einer).
  Ein Run hat einen Pfad, Haltestellen-Offsets und Fahrzeiten.
- **Fahrgast-Router**: Graph aus Haltestellen-Knoten und Bahnsteig-Knoten (Linie, Run, Index).
  Kanten sind Einsteigen (halbe Taktzeit + Strafe), Fahren, Aussteigen und Fußweg-Umstieg (≤ 250 m).
  Ergebnisse werden pro Zellenpaar gecacht.
- **Nachfrage**: Gebäude → 200-m-Zellen (Einwohner und Arbeitsplätze). Nur Zellen mit bedienter
  Haltestelle in ≤ 500 m erzeugen Fahrten.

## Nächste Schritte (Ideen)

1. Haltestellen verschieben und zusammenlegen, Linien teilen und verbinden
2. Personal vertiefen: Dienstpläne, Zufriedenheit, Krankheit; Fahrplan-Zeiträume frei einteilen
3. Fahrzeugtypen (Gelenkbus, E-Bus), Wartung und Verschleiß, Betriebshof-Kapazität
4. Tram: Gleise auf Straßen bauen, eigener Graph
5. Stadtweit: U-Bahn-Tunnel frei zeichnen (Kosten nach Tiefe und Länge), Umsteigeknoten Bus ↔ U-Bahn
6. Kampagne: Startkapital, Ziele, Stadtwachstum
7. Depot- und Stations-Builder (3D-Ansicht)

## Entwicklung

```
npm install
npm run dev        # http://localhost:5173
npm run typecheck
```
