import { Area, areaBBox } from '../core/geo';

export interface OsmNode { type: 'node'; id: number; lat: number; lon: number; tags?: Record<string, string> }
export interface OsmWay {
  type: 'way';
  id: number;
  nodes?: number[];
  tags?: Record<string, string>;
  geometry?: { lat: number; lon: number }[];
}
export interface OsmRelation {
  type: 'relation';
  id: number;
  tags?: Record<string, string>;
  members: { type: string; ref: number; role: string; geometry?: { lat: number; lon: number }[] }[];
}
export type OsmElement = OsmNode | OsmWay | OsmRelation;
export interface OsmJson { elements: OsmElement[] }

const ENDPOINTS = [
  'https://overpass-api.de/api/interpreter',
  'https://overpass.private.coffee/api/interpreter',
  'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
];

const ROAD_RE =
  'motorway|motorway_link|trunk|trunk_link|primary|primary_link|secondary|secondary_link|' +
  'tertiary|tertiary_link|unclassified|residential|living_street|service|busway|bus_guideway|pedestrian|road';

function buildQuery(area: Area): string {
  const [s, w, n, e] = areaBBox(area).map((v) => v.toFixed(6));
  return `[out:json][timeout:180][bbox:${s},${w},${n},${e}];
way["highway"~"^(${ROAD_RE})$"]->.r;
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
out geom qt;`;
}

// ---------- IndexedDB cache ----------

const DB_NAME = 'stations-osm';
const STORE = 'areas';

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function cacheGet(key: string): Promise<OsmJson | undefined> {
  try {
    const db = await openDb();
    return await new Promise((resolve) => {
      const req = db.transaction(STORE).objectStore(STORE).get(key);
      req.onsuccess = () => resolve(req.result as OsmJson | undefined);
      req.onerror = () => resolve(undefined);
    });
  } catch {
    return undefined;
  }
}

async function cachePut(key: string, data: OsmJson): Promise<void> {
  try {
    const db = await openDb();
    await new Promise<void>((resolve) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(data, key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
    });
  } catch {
    /* caching is optional */
  }
}

export const areaKey = (a: Area): string => `${a.lat.toFixed(4)},${a.lon.toFixed(4)},${a.radius}`;

export async function loadOsm(area: Area, status: (msg: string) => void): Promise<OsmJson> {
  const key = areaKey(area);
  const cached = await cacheGet(key);
  if (cached) {
    status('Kartendaten aus dem Cache geladen');
    return cached;
  }
  const query = buildQuery(area);
  let lastErr: unknown;
  // Public Overpass servers answer 429/502/504 when busy and ask clients to retry with backoff.
  for (let round = 0; round < 3; round++) {
    for (const url of ENDPOINTS) {
      const host = new URL(url).host;
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), 120_000);
      try {
        status(`Lade OpenStreetMap-Daten von ${host} … (Versuch ${round + 1}, kann etwas dauern)`);
        const res = await fetch(url, {
          method: 'POST',
          body: new URLSearchParams({ data: query }),
          signal: ctrl.signal,
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        status('Empfange Kartendaten …');
        const json = (await res.json()) as OsmJson;
        if (!json.elements?.length) throw new Error('Keine Daten für dieses Gebiet');
        await cachePut(key, json);
        return json;
      } catch (err) {
        lastErr = ctrl.signal.aborted ? new Error('Zeitüberschreitung') : err;
        console.warn('Overpass request failed', url, lastErr);
      } finally {
        clearTimeout(timer);
      }
    }
    const wait = 5 * 2 ** round;
    for (let s = wait; s > 0; s--) {
      status(`Kartenserver ausgelastet – neuer Versuch in ${s} s …`);
      await new Promise((r) => setTimeout(r, 1000));
    }
  }
  throw new Error(`OSM-Daten konnten nicht geladen werden (${String(lastErr)}). Bitte später erneut versuchen.`);
}

/** Search a place by name (OSM Nominatim). */
export async function searchPlace(q: string): Promise<{ name: string; lat: number; lon: number }[]> {
  const url = `https://nominatim.openstreetmap.org/search?format=json&limit=6&q=${encodeURIComponent(q)}`;
  const res = await fetch(url, { headers: { 'Accept-Language': 'de' } });
  if (!res.ok) throw new Error(`Suche fehlgeschlagen (HTTP ${res.status})`);
  const data = (await res.json()) as { display_name: string; lat: string; lon: string }[];
  return data.map((d) => ({ name: d.display_name, lat: parseFloat(d.lat), lon: parseFloat(d.lon) }));
}
