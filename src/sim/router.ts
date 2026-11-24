import { MinHeap } from '../core/util';
import { Cell } from '../map/mapdata';
import { Leg, Stop, TransitLine } from './types';

export const WALK_SPEED = 1.3; // m/s
export const WALK_DETOUR = 1.25;
export const ACCESS_RADIUS = 500; // m, max walk to a stop
const TRANSFER_RADIUS = 250;
const BOARD_PENALTY = 90; // s, perceived cost of each boarding
const MAX_WAIT = 900;

export interface Journey {
  legs: Leg[];
  time: number; // total perceived time incl. walking at both ends
  firstStop: number;
  firstWalk: number;
}

interface Edge {
  to: number;
  cost: number;
  kind: 0 | 1 | 2 | 3; // 0 board, 1 ride, 2 alight, 3 walk
}

/**
 * Passenger routing over the served transit network.
 * Nodes: one per stop, plus one "platform" node per (line, run, stop index).
 */
export class Router {
  private stops: Stop[] = [];
  private stopNode = new Map<number, number>();
  private plat: { line: number; run: number; idx: number; stop: number; ver: number }[] = [];
  private adj: Edge[][] = [];
  private cache = new Map<number, Journey | null>();

  /** Served stops reachable on foot from each cell. */
  cellAccess: { node: number; walk: number }[][] = [];
  covered: number[] = [];
  cumRes: number[] = [];
  cumJobs: number[] = [];
  coveredRes = 0;
  coveredJobs = 0;

  constructor(
    cells: Cell[],
    stops: Stop[],
    lines: TransitLine[],
    headway: (l: TransitLine) => number,
  ) {
    const served = new Set<number>();
    const active = lines.filter((l) => isFinite(headway(l)) && l.runs.length);
    for (const l of active) for (const id of l.stopIds) served.add(id);
    this.stops = stops.filter((s) => served.has(s.id));
    this.stops.forEach((s, i) => this.stopNode.set(s.id, i));
    const S = this.stops.length;
    for (let i = 0; i < S; i++) this.adj.push([]);

    for (const l of active) {
      const wait = Math.min(headway(l) / 2, MAX_WAIT) + BOARD_PENALTY;
      l.runs.forEach((run, r) => {
        const base = S + this.plat.length;
        const n = run.stopIds.length;
        for (let i = 0; i < n; i++) {
          const stop = run.stopIds[i];
          this.plat.push({ line: l.id, run: r, idx: i, stop, ver: l.version });
          const edges: Edge[] = [];
          if (i < n - 1) edges.push({ to: base + i + 1, cost: run.times[i + 1] - run.times[i], kind: 1 });
          if (i > 0) edges.push({ to: this.stopNode.get(stop)!, cost: 0, kind: 2 });
          this.adj.push(edges);
          if (i < n - 1) this.adj[this.stopNode.get(stop)!].push({ to: base + i, cost: wait, kind: 0 });
        }
      });
    }

    // Walking transfers between nearby stops
    for (let i = 0; i < S; i++) {
      for (let j = 0; j < S; j++) {
        if (i === j) continue;
        const a = this.stops[i].pos, b = this.stops[j].pos;
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        if (d < TRANSFER_RADIUS) {
          this.adj[i].push({ to: j, cost: (d * WALK_DETOUR) / WALK_SPEED + 60, kind: 3 });
        }
      }
    }

    // Cell coverage
    for (let c = 0; c < cells.length; c++) {
      const cell = cells[c];
      const acc: { node: number; walk: number }[] = [];
      for (let i = 0; i < S; i++) {
        const p = this.stops[i].pos;
        const d = Math.hypot(p.x - cell.x, p.y - cell.y);
        if (d < ACCESS_RADIUS) acc.push({ node: i, walk: (d * WALK_DETOUR) / WALK_SPEED });
      }
      this.cellAccess.push(acc);
      if (acc.length) {
        this.covered.push(c);
        this.coveredRes += cell.residents;
        this.coveredJobs += cell.jobs;
        this.cumRes.push(this.coveredRes);
        this.cumJobs.push(this.coveredJobs);
      }
    }
  }

  get stopCount(): number {
    return this.stops.length;
  }

  /** Pick a covered cell, weighted by residents or jobs. */
  sampleCell(byJobs: boolean): number {
    const cum = byJobs ? this.cumJobs : this.cumRes;
    const total = byJobs ? this.coveredJobs : this.coveredRes;
    const r = Math.random() * total;
    let lo = 0, hi = cum.length - 1;
    while (lo < hi) {
      const m = (lo + hi) >> 1;
      if (cum[m] < r) lo = m + 1;
      else hi = m;
    }
    return this.covered[lo];
  }

  find(from: number, to: number): Journey | null {
    const key = from * 100003 + to;
    const hit = this.cache.get(key);
    if (hit !== undefined) return hit;
    const j = this.search(from, to);
    if (this.cache.size > 200_000) this.cache.clear();
    this.cache.set(key, j);
    return j;
  }

  private search(from: number, to: number): Journey | null {
    const src = this.cellAccess[from], dst = this.cellAccess[to];
    if (!src.length || !dst.length) return null;
    const N = this.adj.length;
    const dist = new Float64Array(N).fill(Infinity);
    const prev = new Int32Array(N).fill(-1);
    const prevKind = new Int8Array(N).fill(-1);
    const heap = new MinHeap();
    for (const a of src) {
      if (a.walk < dist[a.node]) {
        dist[a.node] = a.walk;
        heap.push(a.walk, a.node);
      }
    }
    const target = new Map<number, number>();
    for (const a of dst) target.set(a.node, a.walk);

    let best = Infinity, bestNode = -1;
    while (heap.size) {
      const v = heap.pop();
      const d = heap.lastKey;
      if (d > dist[v]) continue;
      if (d >= best) break;
      const tw = target.get(v);
      if (tw !== undefined && prevKind[v] === 2 && d + tw < best) {
        best = d + tw;
        bestNode = v;
      }
      for (const e of this.adj[v]) {
        const nd = d + e.cost;
        if (nd < dist[e.to]) {
          dist[e.to] = nd;
          prev[e.to] = v;
          prevKind[e.to] = e.kind;
          heap.push(nd, e.to);
        }
      }
    }
    if (bestNode < 0) return null;

    // Walk back to reconstruct the node chain
    const chain: number[] = [];
    for (let v = bestNode; v >= 0; v = prev[v]) chain.push(v);
    chain.reverse();
    const S = this.stops.length;
    const legs: Leg[] = [];
    let walk = 0;
    let cur: Partial<Leg> | null = null;
    for (let k = 1; k < chain.length; k++) {
      const v = chain[k], u = chain[k - 1];
      const kind = prevKind[v];
      if (kind === 0) {
        const p = this.plat[v - S];
        cur = { line: p.line, run: p.run, board: p.idx, boardStop: p.stop, walkBefore: walk, ver: p.ver };
        walk = 0;
      } else if (kind === 2 && cur) {
        const p = this.plat[u - S];
        cur.alight = p.idx;
        cur.alightStop = p.stop;
        legs.push(cur as Leg);
        cur = null;
      } else if (kind === 3) {
        walk += dist[v] - dist[u];
      }
    }
    if (!legs.length) return null;
    const first = src.find((a) => a.node === chain[0])!;
    return { legs, time: best, firstStop: legs[0].boardStop, firstWalk: first.walk };
  }
}
