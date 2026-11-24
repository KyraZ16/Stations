/** Binary min-heap storing numeric values with numeric priorities. */
export class MinHeap {
  private keys: number[] = [];
  private vals: number[] = [];
  /** Priority of the value most recently returned by pop(). */
  lastKey = 0;

  get size(): number {
    return this.keys.length;
  }

  push(key: number, val: number): void {
    const k = this.keys;
    const v = this.vals;
    let i = k.length;
    k.push(key);
    v.push(val);
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (k[p] <= key) break;
      k[i] = k[p];
      v[i] = v[p];
      i = p;
    }
    k[i] = key;
    v[i] = val;
  }

  pop(): number {
    const k = this.keys;
    const v = this.vals;
    const topVal = v[0];
    this.lastKey = k[0];
    const lastK = k.pop()!;
    const lastV = v.pop()!;
    const n = k.length;
    if (n > 0) {
      let i = 0;
      while (true) {
        const l = 2 * i + 1;
        if (l >= n) break;
        const r = l + 1;
        const c = r < n && k[r] < k[l] ? r : l;
        if (k[c] >= lastK) break;
        k[i] = k[c];
        v[i] = v[c];
        i = c;
      }
      k[i] = lastK;
      v[i] = lastV;
    }
    return topVal;
  }
}

const moneyFmt = new Intl.NumberFormat('de-DE', {
  style: 'currency',
  currency: 'EUR',
  maximumFractionDigits: 0,
});
const numFmt = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 0 });

export const fmtMoney = (v: number): string => moneyFmt.format(v);
export const fmtNum = (v: number): string => numFmt.format(v);

export function fmtClock(t: number): string {
  const s = ((t % 86400) + 86400) % 86400;
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
}

export function fmtDuration(sec: number): string {
  if (!isFinite(sec)) return '–';
  if (sec < 60) return `${Math.round(sec)} s`;
  const m = Math.round(sec / 60);
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}

export function fmtDist(m: number): string {
  return m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(1).replace('.', ',')} km`;
}

export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : c === '"' ? '&quot;' : '&#39;',
  );
}

export const clamp = (v: number, lo: number, hi: number): number => (v < lo ? lo : v > hi ? hi : v);

export const LINE_COLORS = [
  '#e6332a', '#0a6ebd', '#00963f', '#f39200', '#8e44ad', '#00a3c4',
  '#c2185b', '#6d4c41', '#7cb342', '#283593', '#ef6c00', '#00897b',
];

/** Distance from point (px,py) to segment (ax,ay)-(bx,by); also returns the projection parameter t. */
export function distToSegment(
  px: number, py: number, ax: number, ay: number, bx: number, by: number,
): { d: number; t: number } {
  const dx = bx - ax;
  const dy = by - ay;
  const l2 = dx * dx + dy * dy;
  let t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
  t = clamp(t, 0, 1);
  const qx = ax + t * dx;
  const qy = ay + t * dy;
  return { d: Math.hypot(px - qx, py - qy), t };
}
