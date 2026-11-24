import { RouteResult } from '../map/roadgraph';

/** A polyline with cumulative distances and per-segment speed limits. */
export class Path {
  xs: number[] = [];
  ys: number[] = [];
  cum: number[] = [];
  spd: number[] = [];

  get length(): number {
    return this.cum.length ? this.cum[this.cum.length - 1] : 0;
  }

  static from(r: RouteResult): Path {
    const p = new Path();
    p.append(r);
    return p;
  }

  append(r: RouteResult): void {
    for (let i = 0; i < r.xs.length; i++) {
      if (i === 0 && this.xs.length) continue; // shared join point
      const x = r.xs[i], y = r.ys[i];
      const n = this.xs.length;
      if (n) {
        this.cum.push(this.cum[n - 1] + Math.hypot(x - this.xs[n - 1], y - this.ys[n - 1]));
        this.spd.push(r.spd[i - 1] ?? r.spd[0]);
      } else {
        this.cum.push(0);
      }
      this.xs.push(x);
      this.ys.push(y);
    }
  }

  /** Index i of the segment containing distance s (cum[i] <= s <= cum[i+1]). */
  segAt(s: number, hint = 0): number {
    const c = this.cum;
    const last = c.length - 2;
    if (last < 0) return 0;
    let i = Math.min(Math.max(hint, 0), last);
    if (c[i] <= s) {
      while (i < last && c[i + 1] < s) i++;
      return i;
    }
    let lo = 0, hi = i;
    while (lo < hi) {
      const m = (lo + hi + 1) >> 1;
      if (c[m] <= s) lo = m;
      else hi = m - 1;
    }
    return lo;
  }

  pointAt(s: number, seg: number): { x: number; y: number; ang: number } {
    const n = this.xs.length;
    if (n === 1) return { x: this.xs[0], y: this.ys[0], ang: 0 };
    const i = Math.min(seg, n - 2);
    const l = this.cum[i + 1] - this.cum[i];
    const t = l > 0 ? Math.min(Math.max((s - this.cum[i]) / l, 0), 1) : 0;
    const dx = this.xs[i + 1] - this.xs[i], dy = this.ys[i + 1] - this.ys[i];
    return { x: this.xs[i] + t * dx, y: this.ys[i] + t * dy, ang: Math.atan2(dy, dx) };
  }

  /** Estimated driving time between two distances for a vehicle with top speed vmax. */
  travelTime(s0: number, s1: number, vmax: number): number {
    let t = 0;
    let i = this.segAt(s0);
    while (i < this.spd.length && this.cum[i] < s1) {
      const a = Math.max(this.cum[i], s0), b = Math.min(this.cum[i + 1], s1);
      if (b > a) t += (b - a) / Math.min(this.spd[i], vmax);
      i++;
    }
    return t * 1.1; // acceleration and braking
  }
}
