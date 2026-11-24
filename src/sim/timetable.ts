/**
 * A line's timetable: service hours plus a target headway per daily period.
 * Hours are decimal (5.5 = 05:30); `end` may exceed 24 for service past midnight.
 */
export interface Timetable {
  start: number;
  end: number;
  /** Headway in seconds per period; 0 = no service in that period. */
  headways: number[];
}

export const PERIODS: { name: string; to: number }[] = [
  { name: 'Frühverkehr', to: 6 },
  { name: 'HVZ morgens', to: 9 },
  { name: 'Tagesverkehr', to: 15 },
  { name: 'HVZ nachmittags', to: 19 },
  { name: 'Abendverkehr', to: Infinity },
];

/** Selectable headways in seconds. */
export const HEADWAY_OPTIONS = [300, 360, 450, 600, 720, 900, 1200, 1800, 3600];

export const defaultTimetable = (): Timetable => ({
  start: 5,
  end: 24,
  headways: [1200, 600, 900, 600, 1200],
});

/** [from, to) in hours for period i, or null if the period is empty. */
export function periodBounds(tt: Timetable, i: number): [number, number] | null {
  const from = i === 0 ? tt.start : Math.max(tt.start, PERIODS[i - 1].to);
  const to = i === PERIODS.length - 1 ? tt.end : Math.min(tt.end, PERIODS[i].to);
  return to > from ? [from, to] : null;
}

/** Index of the period active at game time t (seconds), or -1 outside service hours. */
export function periodAt(tt: Timetable, t: number): number {
  let h = (((t % 86400) + 86400) % 86400) / 3600;
  if (h < tt.start && h + 24 < tt.end) h += 24;
  if (h < tt.start || h >= tt.end) return -1;
  for (let i = 0; i < PERIODS.length; i++) {
    const b = periodBounds(tt, i);
    if (b && h >= b[0] && h < b[1]) return i;
  }
  return -1;
}

export function targetHeadway(tt: Timetable, t: number): number {
  const i = periodAt(tt, t);
  if (i < 0) return Infinity;
  const hw = tt.headways[i];
  return hw > 0 ? hw : Infinity;
}

export function vehiclesFor(cycleTime: number, headway: number): number {
  return isFinite(headway) ? Math.ceil(cycleTime / headway - 0.05) : 0;
}

/** Most vehicles needed at any time of the day. */
export function peakVehicles(tt: Timetable, cycleTime: number): number {
  let n = 0;
  PERIODS.forEach((_, i) => {
    if (periodBounds(tt, i) && tt.headways[i] > 0) n = Math.max(n, vehiclesFor(cycleTime, tt.headways[i]));
  });
  return n;
}

/** Vehicle hours per day (for estimating driver needs). */
export function vehicleHours(tt: Timetable, cycleTime: number): number {
  let sum = 0;
  PERIODS.forEach((_, i) => {
    const b = periodBounds(tt, i);
    if (b && tt.headways[i] > 0) sum += (b[1] - b[0]) * vehiclesFor(cycleTime, tt.headways[i]);
  });
  return sum;
}

export function fmtHour(h: number): string {
  const hh = Math.floor(h) % 24;
  const mm = Math.round((h % 1) * 60);
  return `${String(hh).padStart(2, '0')}:${String(mm).padStart(2, '0')}`;
}
