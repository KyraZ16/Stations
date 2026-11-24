import { Area, Projection } from '../core/geo';
import { OsmJson, OsmWay, OsmRelation } from './overpass';
import { RoadGraph } from './roadgraph';

export type RoadClass =
  | 'motorway' | 'trunk' | 'primary' | 'secondary' | 'tertiary' | 'minor' | 'service' | 'pedestrian';

export interface RoadWay {
  cls: RoadClass;
  name: string;
  pts: number[]; // flat x,y
  tunnel: boolean;
}

export type BuildingKind = 'res' | 'com' | 'mixed' | 'minor';

export interface Building {
  pts: number[];
  cx: number;
  cy: number;
  kind: BuildingKind;
  residents: number;
  jobs: number;
  cell: number;
}

export type PolyKind =
  | 'water' | 'park' | 'forest' | 'pitch' | 'cemetery' | 'residential' | 'commercial' | 'industrial' | 'railway';

export interface AreaPoly {
  kind: PolyKind;
  rings: number[][];
}

export interface MapLine {
  kind: 'river' | 'canal' | 'rail' | 'tram' | 'subway' | 'light_rail';
  pts: number[];
  tunnel: boolean;
}

/** Demand zone: buildings aggregated on a grid. */
export interface Cell {
  x: number;
  y: number;
  residents: number;
  jobs: number;
}

export const CELL_SIZE = 200;

export interface MapData {
  area: Area;
  proj: Projection;
  half: number; // half width of map in meters
  roads: RoadWay[];
  buildings: Building[];
  polys: AreaPoly[];
  lines: MapLine[];
  cells: Cell[];
  graph: RoadGraph;
  totals: { residents: number; jobs: number };
}

const ROAD_CLASS: Record<string, RoadClass> = {
  motorway: 'motorway', motorway_link: 'motorway',
  trunk: 'trunk', trunk_link: 'trunk',
  primary: 'primary', primary_link: 'primary',
  secondary: 'secondary', secondary_link: 'secondary',
  tertiary: 'tertiary', tertiary_link: 'tertiary',
  unclassified: 'minor', residential: 'minor', road: 'minor', living_street: 'minor', busway: 'minor',
  bus_guideway: 'minor',
  service: 'service', pedestrian: 'pedestrian',
};

const DEFAULT_SPEED_KMH: Record<string, number> = {
  motorway: 100, motorway_link: 60, trunk: 70, trunk_link: 50, primary: 50, primary_link: 40,
  secondary: 50, secondary_link: 40, tertiary: 45, tertiary_link: 35, unclassified: 40,
  residential: 30, road: 30, living_street: 10, service: 20, busway: 50, bus_guideway: 60, pedestrian: 10,
};

/** Share of the speed limit actually reached in city traffic. */
const TRAFFIC_FACTOR = 0.75;

const RES_TYPES = new Set([
  'house', 'detached', 'semidetached_house', 'terrace', 'apartments', 'residential', 'dormitory',
  'bungalow', 'farm', 'semi', 'cabin',
]);
const COM_TYPES = new Set([
  'commercial', 'office', 'retail', 'industrial', 'warehouse', 'supermarket', 'school', 'university',
  'college', 'hospital', 'public', 'civic', 'government', 'hotel', 'kindergarten', 'train_station',
  'transportation', 'manufacture', 'sports_hall', 'sports_centre', 'stadium', 'museum', 'mall', 'factory',
]);
const MINOR_TYPES = new Set([
  'garage', 'garages', 'shed', 'roof', 'carport', 'hut', 'ruins', 'container', 'kiosk', 'service',
  'construction', 'greenhouse', 'bunker', 'toilets', 'parking', 'transformer_tower', 'bridge',
]);

function isTrue(v: string | undefined): boolean {
  return v === 'yes' || v === 'true' || v === '1';
}

function parseSpeed(tags: Record<string, string>, hw: string): number {
  const ms = tags.maxspeed;
  let kmh = DEFAULT_SPEED_KMH[hw] ?? 30;
  if (ms) {
    const n = parseFloat(ms);
    if (!isNaN(n)) kmh = ms.includes('mph') ? n * 1.609 : n;
    else if (ms.endsWith(':urban')) kmh = 50;
    else if (ms === 'walk') kmh = 7;
  }
  return Math.max(5, kmh) / 3.6;
}

function routable(tags: Record<string, string>, hw: string): boolean {
  const busOk = isTrue(tags.bus) || isTrue(tags.psv) || tags.bus === 'designated' || tags.psv === 'designated';
  if (busOk) return true;
  if (hw === 'pedestrian') return false;
  if (hw === 'service' && ['parking_aisle', 'driveway', 'drive-through', 'emergency_access'].includes(tags.service)) {
    return false;
  }
  if (['private', 'no'].includes(tags.access) || ['private', 'no'].includes(tags.motor_vehicle)) return false;
  if (tags.area === 'yes') return false;
  return true;
}

function ringArea(pts: number[]): number {
  let a = 0;
  for (let i = 0, n = pts.length; i < n; i += 2) {
    const j = (i + 2) % n;
    a += pts[i] * pts[j + 1] - pts[j] * pts[i + 1];
  }
  return Math.abs(a) / 2;
}

/** Join way member geometries of a multipolygon relation into closed rings. */
function stitchRings(parts: number[][]): number[][] {
  const rings: number[][] = [];
  const open = parts.filter((p) => p.length >= 4).map((p) => p.slice());
  const same = (p: number[], i: number, q: number[], j: number) =>
    Math.abs(p[i] - q[j]) < 0.05 && Math.abs(p[i + 1] - q[j + 1]) < 0.05;
  while (open.length) {
    let cur = open.pop()!;
    let changed = true;
    while (!same(cur, 0, cur, cur.length - 2) && changed) {
      changed = false;
      for (let k = 0; k < open.length; k++) {
        const p = open[k];
        const end = cur.length - 2;
        if (same(cur, end, p, 0)) {
          cur = cur.concat(p.slice(2));
        } else if (same(cur, end, p, p.length - 2)) {
          cur = cur.concat(reversePts(p).slice(2));
        } else if (same(cur, 0, p, p.length - 2)) {
          cur = p.concat(cur.slice(2));
        } else if (same(cur, 0, p, 0)) {
          cur = reversePts(p).concat(cur.slice(2));
        } else continue;
        open.splice(k, 1);
        changed = true;
        break;
      }
    }
    rings.push(cur);
  }
  return rings;
}

function reversePts(p: number[]): number[] {
  const r: number[] = [];
  for (let i = p.length - 2; i >= 0; i -= 2) r.push(p[i], p[i + 1]);
  return r;
}

function polyKind(t: Record<string, string>): PolyKind | null {
  if (t.natural === 'water' || t.waterway === 'riverbank') return 'water';
  if (t.leisure === 'park' || t.leisure === 'garden' || t.leisure === 'playground') return 'park';
  if (t.leisure === 'pitch') return 'pitch';
  switch (t.landuse) {
    case 'grass': case 'meadow': case 'recreation_ground': return 'park';
    case 'forest': return 'forest';
    case 'cemetery': return 'cemetery';
    case 'residential': return 'residential';
    case 'commercial': case 'retail': return 'commercial';
    case 'industrial': return 'industrial';
    case 'railway': return 'railway';
  }
  return null;
}

export function buildMapData(area: Area, osm: OsmJson): MapData {
  const proj = new Projection(area.lat, area.lon);
  const nodes = new Map<number, [number, number]>();
  for (const el of osm.elements) {
    if (el.type === 'node') nodes.set(el.id, [proj.x(el.lon), proj.y(el.lat)]);
  }
  const geomPts = (geom: { lat: number; lon: number }[] | undefined): number[] => {
    const pts: number[] = [];
    if (!geom) return pts;
    for (const g of geom) {
      if (g) pts.push(proj.x(g.lon), proj.y(g.lat));
    }
    return pts;
  };

  const graph = new RoadGraph();
  const roads: RoadWay[] = [];
  const buildings: Building[] = [];
  const polys: AreaPoly[] = [];
  const lines: MapLine[] = [];

  for (const el of osm.elements) {
    if (el.type === 'way') {
      const t = el.tags ?? {};
      if (t.highway && el.nodes) addRoad(el, t);
      else if (t.building) addBuilding(el, t);
      else if (t.railway) {
        const pts = geomPts(el.geometry);
        if (pts.length >= 4) {
          lines.push({
            kind: t.railway as MapLine['kind'], pts,
            tunnel: t.tunnel === 'yes' || t.location === 'underground' || t.layer?.startsWith('-') === true,
          });
        }
      } else if (t.waterway === 'river' || t.waterway === 'canal') {
        const pts = geomPts(el.geometry);
        if (pts.length >= 4) lines.push({ kind: t.waterway, pts, tunnel: t.tunnel === 'culvert' || t.tunnel === 'yes' });
      } else {
        const kind = polyKind(t);
        const pts = geomPts(el.geometry);
        if (kind && pts.length >= 6) polys.push({ kind, rings: [pts] });
      }
    } else if (el.type === 'relation') {
      addRelation(el);
    }
  }

  function addRoad(w: OsmWay, t: Record<string, string>) {
    const hw = t.highway;
    const cls = ROAD_CLASS[hw];
    if (!cls) return;
    const pts: number[] = [];
    const ids: number[] = [];
    for (const id of w.nodes!) {
      const p = nodes.get(id);
      if (!p) continue;
      pts.push(p[0], p[1]);
      ids.push(id);
    }
    if (ids.length < 2) return;
    const tunnel = t.tunnel === 'yes' || t.tunnel === 'building_passage';
    const wayIdx = roads.length;
    roads.push({ cls, name: t.name ?? t.ref ?? '', pts, tunnel });
    if (!routable(t, hw)) return;

    let fwd = true, bwd = true;
    const ow = t.oneway;
    if (isTrue(ow) || t.junction === 'roundabout' || t.junction === 'circular' || hw === 'motorway' || hw === 'motorway_link') {
      bwd = false;
    } else if (ow === '-1') {
      fwd = false;
    }
    if (t['oneway:bus'] === 'no' || t['oneway:psv'] === 'no' || t.busway === 'opposite_lane') {
      fwd = true;
      bwd = true;
    }
    const spd = parseSpeed(t, hw) * TRAFFIC_FACTOR;
    let prev = graph.vertex(ids[0], pts[0], pts[1]);
    for (let i = 1; i < ids.length; i++) {
      const v = graph.vertex(ids[i], pts[2 * i], pts[2 * i + 1]);
      graph.addSegment(prev, v, fwd, bwd, spd, wayIdx);
      prev = v;
    }
  }

  function addBuilding(w: OsmWay, t: Record<string, string>) {
    const pts = geomPts(w.geometry);
    if (pts.length < 6) return;
    const area = ringArea(pts);
    if (area < 15) return;
    let cx = 0, cy = 0;
    const n = pts.length / 2;
    for (let i = 0; i < pts.length; i += 2) {
      cx += pts[i];
      cy += pts[i + 1];
    }
    cx /= n;
    cy /= n;
    const type = t.building;
    const kind: BuildingKind = MINOR_TYPES.has(type) || area < 30
      ? 'minor'
      : RES_TYPES.has(type) ? 'res' : COM_TYPES.has(type) || t.shop || t.amenity || t.office ? 'com' : 'mixed';
    const defLevels = type === 'apartments' ? 4 : kind === 'res' ? 2 : 3;
    const lv = parseFloat(t['building:levels'] ?? '');
    const levels = isNaN(lv) ? defLevels : Math.min(Math.max(lv, 1), 40);
    const floor = area * levels;
    let residents = 0, jobs = 0;
    if (kind === 'res') residents = floor / 45;
    else if (kind === 'com') jobs = floor / 35;
    else if (kind === 'mixed') {
      residents = floor / 80;
      jobs = floor / 110;
    }
    buildings.push({ pts, cx, cy, kind, residents, jobs, cell: -1 });
  }

  function addRelation(r: OsmRelation) {
    const kind = polyKind(r.tags ?? {});
    if (!kind) return;
    const outer = r.members.filter((m) => m.type === 'way' && m.role !== 'inner').map((m) => geomPts(m.geometry));
    const inner = r.members.filter((m) => m.type === 'way' && m.role === 'inner').map((m) => geomPts(m.geometry));
    const rings = [...stitchRings(outer), ...stitchRings(inner)].filter((p) => p.length >= 6);
    if (rings.length) polys.push({ kind, rings });
  }

  // Demand cells
  const cellMap = new Map<string, number>();
  const cells: Cell[] = [];
  const acc: { sx: number; sy: number; w: number }[] = [];
  let totalRes = 0, totalJobs = 0;
  for (const b of buildings) {
    if (b.kind === 'minor') continue;
    const key = `${Math.floor(b.cx / CELL_SIZE)},${Math.floor(b.cy / CELL_SIZE)}`;
    let ci = cellMap.get(key);
    if (ci === undefined) {
      ci = cells.length;
      cellMap.set(key, ci);
      cells.push({ x: 0, y: 0, residents: 0, jobs: 0 });
      acc.push({ sx: 0, sy: 0, w: 0 });
    }
    b.cell = ci;
    const w = b.residents + b.jobs;
    cells[ci].residents += b.residents;
    cells[ci].jobs += b.jobs;
    acc[ci].sx += b.cx * w;
    acc[ci].sy += b.cy * w;
    acc[ci].w += w;
    totalRes += b.residents;
    totalJobs += b.jobs;
  }
  cells.forEach((c, i) => {
    c.x = acc[i].sx / acc[i].w;
    c.y = acc[i].sy / acc[i].w;
  });

  // Draw large polygons first so small ones (ponds in parks) stay visible.
  polys.sort((p, q) => ringArea(q.rings[0]) - ringArea(p.rings[0]));

  return {
    area, proj, half: area.radius, roads, buildings, polys, lines, cells, graph,
    totals: { residents: totalRes, jobs: totalJobs },
  };
}
