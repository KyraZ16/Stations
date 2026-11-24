# Stations

ÖPNV-Simulation im Browser auf echten OpenStreetMap-Straßen. Plane Buslinien, setze Fahrzeuge ein
und bring Fahrgäste ans Ziel. Geplant sind drei Netzebenen: **Lokal** (Bus, Tram),
**Stadtweit** (U-Bahn, S-Bahn) und **Regional** (Zug).

Aktueller Stand: spielbarer Bus-Prototyp im Sandbox-Modus.

## Starten

```
npm install
npm run dev
```

Dann http://localhost:5173 öffnen, eine Stadt wählen oder einen Ort suchen.

## Steuerung

| Taste | Funktion |
|---|---|
| V | Auswählen |
| H | Haltestelle setzen |
| L | Linie zeichnen (Enter = fertig, Rechtsklick = letzte entfernen) |
| B | Betriebshof bauen |
| X | Abreißen |
| C | Einzugsgebiet anzeigen |
| Leertaste / 1–3 | Pause / Geschwindigkeit |

Linien bearbeiten: Linie anklicken → „Strecke bearbeiten“.

Details zu Spielmechanik und Architektur: [docs/DESIGN.md](docs/DESIGN.md)

Kartendaten © OpenStreetMap-Mitwirkende (ODbL).
