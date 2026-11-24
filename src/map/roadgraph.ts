import { MinHeap, distToSegment } from '../core/util';

/** A position on the road network: a point on segment `seg` at fraction `t` from segA to segB. */
export interface RoadPos {
  seg: number;
  t: number;
  x: number;
  y: number;
}

/** A routed polyline. spd[i] is the speed (m/s) on the piece from point i to i+1. */
export interface RouteResult {
  xs: number[];
  ys: number[];
  spd: number[];
  time: number;
}

const GRID = 60;
const MAX_SPEED = 30; // m/s, admissible A* heuristic

/**
 * Directed street graph built from OSM ways. Every OSM node is a vertex.
 * Segments are the undirected pieces between two consecutive nodes; each has
 * a forward and/or reverse directed edge depending on one-way rules.
 */
export class RoadGraph {
  vx: number[] = [];
  vy: number[] = [];
  private vIndex = new Map<number, number>();

  eFrom: number[] = [];
  eTo: number[] = [];
  eCost: number[] = []; // seconds
  eSpd: number[] = [];
  out: number[][] = [];

  segA: number[] = [];
  segB: number[] = [];
  segF: number[] = [];
  segR: number[] = [];
  segLen: number[] = [];
  segSpd: number[] = [];
  segWay: number[] = [];
  /** Segments touching each vertex (for direction-free routing, e.g. tram tracks). */
  vSegs: number[][] = [];
  private grid = new Map<number, number[]>();

  vertex(osmId: number, x: number, y: number): number {
    let i = this.vIndex.get(osmId);
    if (i === undefined) {
      i = this.vx.length;
      this.vx.push(x);
      this.vy.push(y);
      this.out.push([]);
      this.vSegs.push([]);
      this.vIndex.set(osmId, i);
    }
    return i;
  }

  private addEdge(a: number, b: number, len: number, spd: number): number {
    const e = this.eFrom.length;
    this.eFrom.push(a);
    this.eTo.push(b);
    this.eCost.push(len / spd);
    this.eSpd.push(spd);
    this.out[a].push(e);
    return e;
  }

  addSegment(a: number, b: number, fwd: boolean, bwd: boolean, spd: number, way: number): void {
    const ax = this.vx[a], ay = this.vy[a], bx = this.vx[b], by = this.vy[b];
    const len = Math.hypot(bx - ax, by - ay);
    if (len < 0.01 || (!fwd && !bwd)) return;
    const s = this.segA.length;
    this.segA.push(a);
    this.segB.push(b);
    this.segF.push(fwd ? this.addEdge(a, b, len, spd) : -1);
    this.segR.push(bwd ? this.addEdge(b, a, len, spd) : -1);
    this.segLen.push(len);
    this.segSpd.push(spd);
    this.segWay.push(way);
    this.vSegs[a].push(s);
    this.vSegs[b].push(s);
    const x0 = Math.floor(Math.min(ax, bx) / GRID), x1 = Math.floor(Math.max(ax, bx) / GRID);
    const y0 = Math.floor(Math.min(ay, by) / GRID), y1 = Math.floor(Math.max(ay, by) / GRID);
    for (let cx = x0; cx <= x1; cx++) {
      for (let cy = y0; cy <= y1; cy++) {
        const k = cellKey(cx, cy);
        let list = this.grid.get(k);
        if (!list) this.grid.set(k, (list = []));
        list.push(s);
      }
    }
  }

  /** Nearest point on the drivable network within maxDist meters (optionally only allowed segments). */
  snap(x: number, y: number, maxDist = 40, allow?: (seg: number) => boolean): RoadPos | null {
    const r = Math.ceil(maxDist / GRID);
    const cx = Math.floor(x / GRID), cy = Math.floor(y / GRID);
    let best: RoadPos | null = null;
    let bestD = maxDist;
    for (let i = cx - r; i <= cx + r; i++) {
      for (let j = cy - r; j <= cy + r; j++) {
        const list = this.grid.get(cellKey(i, j));
        if (!list) continue;
        for (const s of list) {
          if (allow && !allow(s)) continue;
          const a = this.segA[s], b = this.segB[s];
          const { d, t } = distToSegment(x, y, this.vx[a], this.vy[a], this.vx[b], this.vy[b]);
          if (d < bestD) {
            bestD = d;
            best = {
              seg: s, t,
              x: this.vx[a] + t * (this.vx[b] - this.vx[a]),
              y: this.vy[a] + t * (this.vy[b] - this.vy[a]),
            };
          }
        }
      }
    }
    return best;
  }

  /** Re-derive a RoadPos (after loading a save) from coordinates. */
  resnap(x: number, y: number): RoadPos | null {
    return this.snap(x, y, 60);
  }

  /** Fastest drivable route between two road positions (A*). */
  route(a: RoadPos, b: RoadPos): RouteResult | null {
    const sa = a.seg, sb = b.seg;
    const spdA = this.segSpd[sa], lenA = this.segLen[sa];
    const spdB = this.segSpd[sb], lenB = this.segLen[sb];

    // Both points on the same segment and reachable directly.
    if (sa === sb) {
      const fwdOk = this.segF[sa] >= 0 && b.t >= a.t;
      const bwdOk = this.segR[sa] >= 0 && b.t <= a.t;
      if (fwdOk || bwdOk) {
        const len = Math.abs(b.t - a.t) * lenA;
        return { xs: [a.x, b.x], ys: [a.y, b.y], spd: [spdA], time: len / spdA };
      }
    }

    const n = this.vx.length;
    const g = new Float64Array(n).fill(Infinity);
    const prev = new Int32Array(n).fill(-1);
    const closed = new Uint8Array(n);
    const heap = new MinHeap();
    const bx = b.x, by = b.y;
    const h = (v: number) => Math.hypot(this.vx[v] - bx, this.vy[v] - by) / MAX_SPEED;

    const seed = (v: number, cost: number) => {
      if (cost < g[v]) {
        g[v] = cost;
        prev[v] = -2;
        heap.push(cost + h(v), v);
      }
    };
    if (this.segF[sa] >= 0) seed(this.segB[sa], ((1 - a.t) * lenA) / spdA);
    if (this.segR[sa] >= 0) seed(this.segA[sa], (a.t * lenA) / spdA);

    const endA = this.segF[sb] >= 0 ? this.segA[sb] : -1; // enter target segment from A going forward
    const extraA = (b.t * lenB) / spdB;
    const endB = this.segR[sb] >= 0 ? this.segB[sb] : -1;
    const extraB = ((1 - b.t) * lenB) / spdB;

    let best = Infinity;
    let bestEnd = -1;
    while (heap.size) {
      const v = heap.pop();
      if (heap.lastKey >= best) break;
      if (closed[v]) continue;
      closed[v] = 1;
      const gv = g[v];
      if (v === endA && gv + extraA < best) {
        best = gv + extraA;
        bestEnd = v;
      }
      if (v === endB && gv + extraB < best) {
        best = gv + extraB;
        bestEnd = v;
      }
      for (const e of this.out[v]) {
        const w = this.eTo[e];
        const ng = gv + this.eCost[e];
        if (ng < g[w]) {
          g[w] = ng;
          prev[w] = e;
          heap.push(ng + h(w), w);
        }
      }
    }
    if (bestEnd < 0) return null;

    const verts: number[] = [];
    const spds: number[] = [];
    let v = bestEnd;
    while (prev[v] !== -2) {
      const e = prev[v];
      verts.push(v);
      spds.push(this.eSpd[e]);
      v = this.eFrom[e];
    }
    verts.push(v);
    verts.reverse();
    spds.reverse();

    const xs = [a.x], ys = [a.y], spd = [spdA];
    for (const vv of verts) {
      xs.push(this.vx[vv]);
      ys.push(this.vy[vv]);
    }
    spd.push(...spds);
    xs.push(b.x);
    ys.push(b.y);
    spd.push(spdB);
    return { xs, ys, spd, time: best };
  }

  /**
   * Shortest route that may use segments in both directions (tram tracks, track planning).
   * Only segments passing `allow` are used; `speed` gives m/s per segment.
   * Also returns the segments the route runs over.
   */
  routeSegs(
    a: RoadPos, b: RoadPos, allow: (seg: number) => boolean, speed: (seg: number) => number,
  ): (RouteResult & { segs: number[] }) | null {
    const sa = a.seg, sb = b.seg;
    if (!allow(sa) || !allow(sb)) return null;
    if (sa === sb) {
      const len = Math.abs(b.t - a.t) * this.segLen[sa];
      return { xs: [a.x, b.x], ys: [a.y, b.y], spd: [speed(sa)], time: len / speed(sa), segs: [sa] };
    }
    const n = this.vx.length;
    const g = new Float64Array(n).fill(Infinity);
    const prevSeg = new Int32Array(n).fill(-1);
    const closed = new Uint8Array(n);
    const heap = new MinHeap();
    const h = (v: number) => Math.hypot(this.vx[v] - b.x, this.vy[v] - b.y) / MAX_SPEED;
    const seed = (v: number, cost: number) => {
      if (cost < g[v]) {
        g[v] = cost;
        prevSeg[v] = -2;
        heap.push(cost + h(v), v);
      }
    };
    const la = this.segLen[sa], lb = this.segLen[sb];
    seed(this.segB[sa], ((1 - a.t) * la) / speed(sa));
    seed(this.segA[sa], (a.t * la) / speed(sa));
    const endA = this.segA[sb], extraA = (b.t * lb) / speed(sb);
    const endB = this.segB[sb], extraB = ((1 - b.t) * lb) / speed(sb);

    let best = Infinity, bestEnd = -1;
    while (heap.size) {
      const v = heap.pop();
      if (heap.lastKey >= best) break;
      if (closed[v]) continue;
      closed[v] = 1;
      const gv = g[v];
      if (v === endA && gv + extraA < best) { best = gv + extraA; bestEnd = v; }
      if (v === endB && gv + extraB < best) { best = gv + extraB; bestEnd = v; }
      for (const s of this.vSegs[v]) {
        if (!allow(s)) continue;
        const w = this.segA[s] === v ? this.segB[s] : this.segA[s];
        const ng = gv + this.segLen[s] / speed(s);
        if (ng < g[w]) {
          g[w] = ng;
          prevSeg[w] = s;
          heap.push(ng + h(w), w);
        }
      }
    }
    if (bestEnd < 0) return null;

    const verts: number[] = [];
    const segs: number[] = [];
    let v = bestEnd;
    while (prevSeg[v] !== -2) {
      const s = prevSeg[v];
      verts.push(v);
      segs.push(s);
      v = this.segA[s] === v ? this.segB[s] : this.segA[s];
    }
    verts.push(v);
    verts.reverse();
    segs.reverse();
    const xs = [a.x], ys = [a.y], spd = [speed(sa)];
    for (const vv of verts) {
      xs.push(this.vx[vv]);
      ys.push(this.vy[vv]);
    }
    for (const s of segs) spd.push(speed(s));
    xs.push(b.x);
    ys.push(b.y);
    spd.push(speed(sb));
    return { xs, ys, spd, time: best, segs: [sa, ...segs, sb] };
  }
}

function cellKey(cx: number, cy: number): number {
  return (cx + 100000) * 200003 + (cy + 100000);
}
