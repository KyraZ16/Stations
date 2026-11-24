import { MODES } from '../core/modes';
import { LINE_COLORS } from '../core/util';
import { MapData, PolyKind, RoadClass, BuildingKind } from '../map/mapdata';
import { RoadPos } from '../map/roadgraph';
import { Game } from '../sim/game';
import { ACCESS_RADIUS } from '../sim/router';
import { Run, Stop } from '../sim/types';
import { Camera } from './camera';

export type Selection =
  | { kind: 'stop' | 'line' | 'vehicle' | 'depot'; id: number }
  | null;

export interface RenderState {
  tool: string;
  hoverPos: RoadPos | null;
  hoverStop: Stop | null;
  draftStops: Stop[];
  draftPaths: { xs: number[]; ys: number[] }[];
  draftCircular: boolean;
  mouseWorld: { x: number; y: number } | null;
  selected: Selection;
  coverage: boolean;
}

const COLORS = {
  bg: '#e9e5dd',
  outside: '#d6d1c7',
  poly: {
    water: '#a9cbe0', park: '#cfe3bf', forest: '#b9d6a8', pitch: '#bfdcae', cemetery: '#c9d9bf',
    residential: '#e4dfd5', commercial: '#eadad6', industrial: '#ded8e2', railway: '#ddd6cc',
  } as Record<PolyKind, string>,
  building: { res: '#d3cbc0', com: '#d0c4c4', mixed: '#d1c9c0', minor: '#dad4cb' } as Record<BuildingKind, string>,
};

const ROAD_STYLE: Record<RoadClass, { w: number; minPx: number; fill: string; casing: string }> = {
  pedestrian: { w: 5, minPx: 0.6, fill: '#f4f1ec', casing: '#d8d2c8' },
  service: { w: 4.5, minPx: 0.6, fill: '#ffffff', casing: '#d3ccc1' },
  minor: { w: 8, minPx: 1.0, fill: '#ffffff', casing: '#cfc7ba' },
  tertiary: { w: 10, minPx: 1.4, fill: '#ffffff', casing: '#c4bbad' },
  secondary: { w: 12, minPx: 1.8, fill: '#fdf3d8', casing: '#cdb98f' },
  primary: { w: 14, minPx: 2.2, fill: '#fde8bd', casing: '#d2aa6c' },
  trunk: { w: 16, minPx: 2.6, fill: '#f9d9a6', casing: '#c79c5c' },
  motorway: { w: 18, minPx: 3, fill: '#f6c78f', casing: '#b98446' },
};
const ROAD_ORDER: RoadClass[] = ['pedestrian', 'service', 'minor', 'tertiary', 'secondary', 'primary', 'trunk', 'motorway'];

function polyline(p: Path2D, pts: number[]): void {
  p.moveTo(pts[0], pts[1]);
  for (let i = 2; i < pts.length; i += 2) p.lineTo(pts[i], pts[i + 1]);
}

export class Renderer {
  private ctx: CanvasRenderingContext2D;
  private dpr = 1;
  private polyPaths = new Map<PolyKind, Path2D>();
  private buildingPaths = new Map<BuildingKind, Path2D>();
  private roadPaths = new Map<RoadClass, Path2D>();
  private tunnelPath = new Path2D();
  private railPath = new Path2D();
  private tramPath = new Path2D();
  private riverPath = new Path2D();
  private canalPath = new Path2D();
  private runPaths = new WeakMap<Run, Path2D>();
  private coverageVersion = -1;
  private coveredPath = new Path2D();
  private uncoveredPath = new Path2D();

  constructor(private canvas: HTMLCanvasElement, readonly cam: Camera, private map: MapData) {
    this.ctx = canvas.getContext('2d')!;
    this.buildStatic();
  }

  private buildStatic(): void {
    const m = this.map;
    for (const poly of m.polys) {
      let p = this.polyPaths.get(poly.kind);
      if (!p) this.polyPaths.set(poly.kind, (p = new Path2D()));
      for (const r of poly.rings) {
        polyline(p, r);
        p.closePath();
      }
    }
    for (const b of m.buildings) {
      let p = this.buildingPaths.get(b.kind);
      if (!p) this.buildingPaths.set(b.kind, (p = new Path2D()));
      polyline(p, b.pts);
      p.closePath();
    }
    for (const r of m.roads) {
      if (r.tunnel) {
        polyline(this.tunnelPath, r.pts);
        continue;
      }
      let p = this.roadPaths.get(r.cls);
      if (!p) this.roadPaths.set(r.cls, (p = new Path2D()));
      polyline(p, r.pts);
    }
    for (const l of m.lines) {
      if (l.tunnel) continue;
      if (l.kind === 'river') polyline(this.riverPath, l.pts);
      else if (l.kind === 'canal') polyline(this.canalPath, l.pts);
      else if (l.kind === 'tram') polyline(this.tramPath, l.pts);
      else polyline(this.railPath, l.pts);
    }
  }

  resize(): void {
    this.dpr = window.devicePixelRatio || 1;
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight;
    this.canvas.width = Math.round(w * this.dpr);
    this.canvas.height = Math.round(h * this.dpr);
    this.cam.w = w;
    this.cam.h = h;
  }

  private worldTransform(): void {
    const c = this.cam, d = this.dpr;
    this.ctx.setTransform(d * c.scale, 0, 0, d * c.scale, d * (c.w / 2 - c.x * c.scale), d * (c.h / 2 - c.y * c.scale));
  }

  private screenTransform(): void {
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
  }

  private updateCoverage(game: Game): void {
    if (this.coverageVersion === game.networkVersion) return;
    this.coverageVersion = game.networkVersion;
    const served: Stop[] = [];
    for (const l of game.lines.values()) {
      if (!isFinite(game.headway(l))) continue;
      for (const id of l.stopIds) {
        const s = game.stops.get(id);
        if (s) served.push(s);
      }
    }
    this.coveredPath = new Path2D();
    this.uncoveredPath = new Path2D();
    const r2 = ACCESS_RADIUS * ACCESS_RADIUS;
    for (const b of this.map.buildings) {
      if (b.kind === 'minor') continue;
      let ok = false;
      for (const s of served) {
        const dx = s.pos.x - b.cx, dy = s.pos.y - b.cy;
        if (dx * dx + dy * dy < r2) {
          ok = true;
          break;
        }
      }
      const p = ok ? this.coveredPath : this.uncoveredPath;
      polyline(p, b.pts);
      p.closePath();
    }
  }

  draw(game: Game, st: RenderState): void {
    const ctx = this.ctx, cam = this.cam, s = cam.scale;
    this.screenTransform();
    ctx.fillStyle = COLORS.outside;
    ctx.fillRect(0, 0, cam.w, cam.h);

    this.worldTransform();
    const half = this.map.half;
    ctx.fillStyle = COLORS.bg;
    ctx.fillRect(-half, -half, half * 2, half * 2);

    // Land use and water
    for (const [kind, p] of this.polyPaths) {
      ctx.fillStyle = COLORS.poly[kind];
      ctx.fill(p, 'evenodd');
    }
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = COLORS.poly.water;
    ctx.lineWidth = Math.max(18, 2 / s);
    ctx.stroke(this.riverPath);
    ctx.lineWidth = Math.max(10, 1.5 / s);
    ctx.stroke(this.canalPath);

    // Buildings
    if (st.coverage) {
      this.updateCoverage(game);
      ctx.fillStyle = '#9fd39a';
      ctx.fill(this.coveredPath);
      ctx.fillStyle = '#e3a49a';
      ctx.fill(this.uncoveredPath);
    } else {
      for (const [kind, p] of this.buildingPaths) {
        ctx.fillStyle = COLORS.building[kind];
        ctx.fill(p);
      }
    }

    // Railways (context only for now)
    ctx.strokeStyle = '#a49c90';
    ctx.lineWidth = Math.max(2.5, 1 / s);
    ctx.setLineDash([]);
    ctx.stroke(this.railPath);
    ctx.lineWidth = Math.max(1.5, 0.8 / s);
    ctx.stroke(this.tramPath);

    // Roads: tunnels, casings, fills
    ctx.strokeStyle = 'rgba(150,140,125,0.35)';
    ctx.lineWidth = Math.max(5, 1 / s);
    ctx.setLineDash([6 / s, 4 / s]);
    ctx.stroke(this.tunnelPath);
    ctx.setLineDash([]);
    for (const cls of ROAD_ORDER) {
      const p = this.roadPaths.get(cls);
      if (!p) continue;
      const style = ROAD_STYLE[cls];
      ctx.strokeStyle = style.casing;
      ctx.lineWidth = Math.max(style.w, style.minPx / s) + Math.max(2, 1 / s);
      ctx.stroke(p);
    }
    for (const cls of ROAD_ORDER) {
      const p = this.roadPaths.get(cls);
      if (!p) continue;
      const style = ROAD_STYLE[cls];
      ctx.strokeStyle = style.fill;
      ctx.lineWidth = Math.max(style.w, style.minPx / s);
      ctx.stroke(p);
    }

    // Transit lines
    const sel = st.selected;
    for (const line of game.lines.values()) {
      const isSel = sel?.kind === 'line' && sel.id === line.id;
      const active = isFinite(game.headway(line));
      for (const run of line.runs) {
        let p = this.runPaths.get(run);
        if (!p) {
          p = new Path2D();
          p.moveTo(run.path.xs[0], run.path.ys[0]);
          for (let i = 1; i < run.path.xs.length; i++) p.lineTo(run.path.xs[i], run.path.ys[i]);
          this.runPaths.set(run, p);
        }
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = (isSel ? 10 : 7) / s;
        ctx.stroke(p);
        ctx.strokeStyle = line.color;
        ctx.globalAlpha = active ? 1 : 0.55;
        ctx.lineWidth = (isSel ? 6 : 4) / s;
        if (!active) ctx.setLineDash([8 / s, 6 / s]);
        ctx.stroke(p);
        ctx.setLineDash([]);
        ctx.globalAlpha = 1;
      }
    }

    // Draft line
    if (st.draftStops.length) {
      const color = LINE_COLORS[game.lines.size % LINE_COLORS.length];
      ctx.strokeStyle = color;
      ctx.lineWidth = 4 / s;
      ctx.setLineDash([10 / s, 6 / s]);
      for (const d of st.draftPaths) {
        ctx.beginPath();
        ctx.moveTo(d.xs[0], d.ys[0]);
        for (let i = 1; i < d.xs.length; i++) ctx.lineTo(d.xs[i], d.ys[i]);
        ctx.stroke();
      }
      const last = st.draftStops[st.draftStops.length - 1];
      const target = st.hoverStop?.pos ?? st.hoverPos ?? st.mouseWorld;
      if (target) {
        ctx.globalAlpha = 0.5;
        ctx.beginPath();
        ctx.moveTo(last.pos.x, last.pos.y);
        ctx.lineTo(target.x, target.y);
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
      ctx.setLineDash([]);
    }

    // Vehicles
    for (const v of game.vehicles.values()) {
      if (v.state === 'depot') continue;
      const def = MODES[v.mode].vehicle;
      const line = v.lineId !== null ? game.lines.get(v.lineId) : undefined;
      const len = Math.max(def.length, 15 / s), wid = Math.max(def.width, 7 / s);
      const isSel = sel?.kind === 'vehicle' && sel.id === v.id;
      ctx.save();
      ctx.translate(v.x, v.y);
      ctx.rotate(v.ang);
      // Dark outline so the bus stands out on its own line color
      ctx.beginPath();
      ctx.rect(-len / 2, -wid / 2, len, wid);
      ctx.fillStyle = line && (v.state !== 'deadhead' || v.deadheadTo === 'rejoin') ? line.color : '#7d8590';
      ctx.fill();
      ctx.strokeStyle = isSel ? '#f39200' : '#1f2328';
      ctx.lineWidth = (isSel ? 3 : 1.6) / s;
      ctx.stroke();
      // Load indicator: white fill grows with occupancy
      const load = Math.min(v.passengers.length / def.capacity, 1);
      const pad = 1.6 / s;
      ctx.fillStyle = 'rgba(255,255,255,0.35)';
      ctx.fillRect(-len / 2 + pad, -wid / 2 + pad, len - 2 * pad - 2.5 / s, wid - 2 * pad);
      if (load > 0) {
        ctx.fillStyle = '#ffffff';
        ctx.fillRect(-len / 2 + pad, -wid / 2 + pad, (len - 2 * pad - 2.5 / s) * load, wid - 2 * pad);
      }
      // Windscreen marks the driving direction
      ctx.fillStyle = '#1f2328';
      ctx.fillRect(len / 2 - 2.5 / s, -wid / 2, 2.5 / s, wid);
      ctx.restore();
    }

    // Screen-space overlays
    this.screenTransform();
    const stopR = Math.max(4, Math.min(8, s * 6));
    const linesAt = new Map<number, string>();
    for (const l of game.lines.values()) for (const id of l.stopIds) if (!linesAt.has(id)) linesAt.set(id, l.color);
    const draftIds = new Set(st.draftStops.map((d) => d.id));

    for (const d of game.depots.values()) {
      const x = cam.sx(d.pos.x), y = cam.sy(d.pos.y);
      const isSel = sel?.kind === 'depot' && sel.id === d.id;
      ctx.fillStyle = '#2d3640';
      ctx.strokeStyle = isSel ? '#f39200' : '#ffffff';
      ctx.lineWidth = 2;
      roundRect(ctx, x - 9, y - 9, 18, 18, 4);
      ctx.fill();
      ctx.stroke();
      ctx.fillStyle = '#fff';
      ctx.font = 'bold 11px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText('B', x, y + 0.5);
    }

    ctx.font = '11px system-ui, sans-serif';
    for (const stop of game.stops.values()) {
      const x = cam.sx(stop.pos.x), y = cam.sy(stop.pos.y);
      if (x < -20 || y < -20 || x > cam.w + 20 || y > cam.h + 20) continue;
      const isSel = sel?.kind === 'stop' && sel.id === stop.id;
      const hover = st.hoverStop?.id === stop.id;
      ctx.beginPath();
      ctx.arc(x, y, stopR + (hover || isSel ? 2 : 0), 0, Math.PI * 2);
      ctx.fillStyle = '#ffffff';
      ctx.fill();
      ctx.lineWidth = isSel ? 3.5 : 2.5;
      ctx.strokeStyle = draftIds.has(stop.id) ? '#111' : linesAt.get(stop.id) ?? '#555';
      ctx.stroke();
      const n = stop.waiting.length;
      if (n > 0 && s > 0.25) {
        const label = String(n);
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        const w = ctx.measureText(label).width + 8;
        ctx.fillStyle = n > 60 ? '#c62828' : n > 25 ? '#ef6c00' : '#37474f';
        roundRect(ctx, x + stopR + 3, y - 8, w, 16, 8);
        ctx.fill();
        ctx.fillStyle = '#fff';
        ctx.fillText(label, x + stopR + 7, y + 0.5);
      }
      if (s > 1.1 || hover || isSel) {
        ctx.textAlign = 'center';
        ctx.textBaseline = 'top';
        ctx.lineWidth = 3;
        ctx.strokeStyle = 'rgba(255,255,255,0.9)';
        ctx.strokeText(stop.name, x, y + stopR + 4);
        ctx.fillStyle = '#222';
        ctx.fillText(stop.name, x, y + stopR + 4);
      }
    }

    // Snap preview
    if (st.hoverPos && !st.hoverStop && (st.tool === 'stop' || st.tool === 'line' || st.tool === 'edit' || st.tool === 'depot')) {
      const x = cam.sx(st.hoverPos.x), y = cam.sy(st.hoverPos.y);
      ctx.beginPath();
      if (st.tool === 'depot') roundRect(ctx, x - 9, y - 9, 18, 18, 4);
      else ctx.arc(x, y, stopR, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(255,255,255,0.7)';
      ctx.fill();
      ctx.setLineDash([3, 3]);
      ctx.lineWidth = 2;
      ctx.strokeStyle = '#333';
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // Scale bar
    this.drawScaleBar();
  }

  private drawScaleBar(): void {
    const ctx = this.ctx, s = this.cam.scale;
    const target = 120 / s;
    const pow = Math.pow(10, Math.floor(Math.log10(target)));
    const nice = [1, 2, 5, 10].map((k) => k * pow).filter((v) => v <= target).pop() ?? pow;
    const px = nice * s;
    const x = 16, y = this.cam.h - 20;
    ctx.strokeStyle = '#333';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, y - 5);
    ctx.lineTo(x, y);
    ctx.lineTo(x + px, y);
    ctx.lineTo(x + px, y - 5);
    ctx.stroke();
    ctx.fillStyle = '#333';
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'bottom';
    ctx.fillText(nice >= 1000 ? `${nice / 1000} km` : `${nice} m`, x + 4, y - 4);
  }
}

function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}
