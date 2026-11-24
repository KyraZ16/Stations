import { Area } from '../core/geo';
import { MODES, ModeId } from '../core/modes';
import { LINE_COLORS } from '../core/util';
import { MapData } from '../map/mapdata';
import { RoadPos } from '../map/roadgraph';
import { Path } from './path';
import { Router } from './router';
import { randomName } from './staff';
import {
  Timetable, defaultTimetable, peakVehicles, targetHeadway, vehicleHours, vehiclesFor,
} from './timetable';
import {
  DayStats, Depot, Driver, Passenger, Run, Stop, TransitLine, Vehicle, newDayStats,
} from './types';

export const SPEEDS = [0, 5, 20, 60]; // game seconds per real second
export const FARE = 2.8;
const MIN_LAYOVER = 60;
const DWELL_ESTIMATE = 15;
const MAX_WAIT = 40 * 60;
const TRIPS_PER_RESIDENT = 6;
const ACCEL = 1.2;
const BRAKE = 1.0;

/** Max driving time per driver and day. */
export const SHIFT_MAX = 8 * 3600;
/** A driver only starts a new trip with at least this much shift time left. */
const SHIFT_MIN_LEFT = 45 * 60;
export const DRIVER_WAGE_DAY = 190;
export const DRIVER_HIRE_COST = 1_500;
/** Vehicles leave the depot this long before the timetable needs them. */
const DISPATCH_LEAD = 15 * 60;
const DISPATCH_INTERVAL = 15;

export interface SaveData {
  v: 1 | 2;
  area: Area;
  time: number;
  money: number;
  sandbox: boolean;
  nextId: number;
  vehicleCounter: number;
  stops: { id: number; name: string; mode: ModeId; x: number; y: number }[];
  depots: { id: number; name: string; mode: ModeId; x: number; y: number }[];
  lines: {
    id: number; name: string; color: string; mode: ModeId; stopIds: number[]; circular: boolean;
    timetable?: Timetable;
  }[];
  vehicles: { id: number; name: string; mode: ModeId; depotId: number; lineId: number | null }[];
  drivers?: { id: number; name: string; depotId: number }[];
  history: DayStats[];
}

/** Time-of-day demand factor (average over a day ≈ 0.9). */
function demandFactor(t: number): number {
  const h = (t % 86400) / 3600;
  if (h < 5) return 0.08;
  if (h < 6) return 0.4;
  if (h < 7) return 1.2;
  if (h < 9) return 2.0;
  if (h < 15) return 0.9;
  if (h < 18) return 1.7;
  if (h < 20) return 1.0;
  if (h < 22) return 0.5;
  return 0.25;
}

/** Share of trips going home → work at time t. */
function outboundShare(t: number): number {
  const h = (t % 86400) / 3600;
  if (h < 11) return 0.8;
  if (h < 14) return 0.5;
  return 0.2;
}

export class Game {
  time = 5 * 3600;
  speed = 1;
  money: number;
  sandbox: boolean;

  stops = new Map<number, Stop>();
  depots = new Map<number, Depot>();
  lines = new Map<number, TransitLine>();
  vehicles = new Map<number, Vehicle>();
  drivers = new Map<number, Driver>();

  today: DayStats;
  history: DayStats[] = [];

  router: Router | null = null;
  /** Incremented whenever the served network changes (for overlays). */
  networkVersion = 0;
  private routerDirty = true;
  private demandAcc = 0;
  private expireTimer = 0;
  private dispatchTimer = 0;
  private headwaySig = '';
  private nextId = 1;
  private vehicleCounter = 0;
  private passengerId = 1;

  onToast: (msg: string, kind?: 'info' | 'error') => void = () => {};
  /** Structural change (something added/removed) – UI should re-render lists. */
  onChange: () => void = () => {};

  constructor(readonly map: MapData, opts: { sandbox: boolean; money?: number }) {
    this.sandbox = opts.sandbox;
    this.money = opts.money ?? 5_000_000;
    this.today = newDayStats(this.day);
  }

  get day(): number {
    return Math.floor(this.time / 86400) + 1;
  }

  // ---------------------------------------------------------------- economy

  canAfford(cost: number): boolean {
    return this.sandbox || this.money >= cost;
  }

  private pay(cost: number): boolean {
    if (!this.canAfford(cost)) {
      this.onToast('Nicht genug Geld', 'error');
      return false;
    }
    this.money -= cost;
    return true;
  }

  // ---------------------------------------------------------------- building

  stopAt(x: number, y: number, radius: number): Stop | null {
    let best: Stop | null = null;
    let bd = radius;
    for (const s of this.stops.values()) {
      const d = Math.hypot(s.pos.x - x, s.pos.y - y);
      if (d < bd) {
        bd = d;
        best = s;
      }
    }
    return best;
  }

  private streetName(pos: RoadPos): string {
    return this.map.roads[this.map.graph.segWay[pos.seg]]?.name ?? '';
  }

  private uniqueStopName(base: string): string {
    const names = new Set([...this.stops.values()].map((s) => s.name));
    if (!names.has(base)) return base;
    for (let i = 2; ; i++) if (!names.has(`${base} ${i}`)) return `${base} ${i}`;
  }

  addStop(pos: RoadPos, mode: ModeId = 'bus'): Stop | null {
    if (this.stopAt(pos.x, pos.y, 30)) {
      this.onToast('Zu nah an einer anderen Haltestelle', 'error');
      return null;
    }
    if (!this.pay(MODES[mode].stopCost)) return null;
    const id = this.nextId++;
    const street = this.streetName(pos);
    const stop: Stop = {
      id, mode, pos, waiting: [], boardedToday: 0,
      name: this.uniqueStopName(street || `Haltestelle ${id}`),
    };
    this.stops.set(id, stop);
    this.onChange();
    return stop;
  }

  removeStop(id: number): void {
    const stop = this.stops.get(id);
    if (!stop) return;
    this.today.lost += stop.waiting.length;
    this.stops.delete(id);
    for (const line of [...this.lines.values()]) {
      if (!line.stopIds.includes(id)) continue;
      line.stopIds = line.stopIds.filter((s) => s !== id);
      if (line.stopIds.length < 2) {
        this.deleteLine(line.id);
        this.onToast(`Linie ${line.name} wurde aufgelöst (zu wenige Haltestellen)`);
      } else {
        const err = this.rebuildLine(line);
        if (err) {
          this.deleteLine(line.id);
          this.onToast(`Linie ${line.name} aufgelöst: ${err}`, 'error');
        }
      }
    }
    this.routerDirty = true;
    this.onChange();
  }

  addDepot(pos: RoadPos, mode: ModeId = 'bus'): Depot | null {
    if (!this.pay(MODES[mode].depotCost)) return null;
    const id = this.nextId++;
    const street = this.streetName(pos);
    const depot: Depot = { id, mode, pos, name: street ? `Betriebshof ${street}` : `Betriebshof ${id}` };
    this.depots.set(id, depot);
    this.onChange();
    return depot;
  }

  removeDepot(id: number): void {
    const count = [...this.vehicles.values()].filter((v) => v.depotId === id).length;
    if (count) {
      this.onToast('Der Betriebshof hat noch Fahrzeuge. Verkaufe sie zuerst.', 'error');
      return;
    }
    for (const d of [...this.drivers.values()]) if (d.depotId === id) this.drivers.delete(d.id);
    this.depots.delete(id);
    this.onChange();
  }

  // ---------------------------------------------------------------- staff

  driversOf(depotId: number): Driver[] {
    return [...this.drivers.values()].filter((d) => d.depotId === depotId);
  }

  hireDrivers(depotId: number, n: number): void {
    if (!this.depots.has(depotId)) return;
    if (!this.pay(DRIVER_HIRE_COST * n)) return;
    for (let i = 0; i < n; i++) {
      const id = this.nextId++;
      this.drivers.set(id, { id, name: randomName(), depotId, worked: 0, vehicleId: null });
    }
    this.onChange();
  }

  /** Dismiss one off-duty driver (prefers those who already worked today). */
  fireDriver(depotId: number): void {
    const off = this.driversOf(depotId).filter((d) => d.vehicleId === null).sort((a, b) => b.worked - a.worked);
    if (!off.length) {
      this.onToast('Alle Fahrer sind gerade im Dienst', 'error');
      return;
    }
    this.drivers.delete(off[0].id);
    this.onChange();
  }

  private availableDriver(depotId: number): Driver | null {
    for (const d of this.drivers.values()) {
      if (d.depotId === depotId && d.vehicleId === null && d.worked < SHIFT_MAX - SHIFT_MIN_LEFT) return d;
    }
    return null;
  }

  private takeDriver(v: Vehicle): boolean {
    if (v.driverId !== null) return true;
    const d = this.availableDriver(v.depotId);
    if (!d) return false;
    d.vehicleId = v.id;
    v.driverId = d.id;
    return true;
  }

  private releaseDriver(v: Vehicle): void {
    const d = v.driverId !== null ? this.drivers.get(v.driverId) : undefined;
    if (d) d.vehicleId = null;
    v.driverId = null;
  }

  /**
   * Estimated drivers a depot needs per day: vehicle hours of the timetables, split by where
   * the line's vehicles are based, in 8 h shifts plus ~30 % reserve (shifts rarely end exactly at 8 h).
   */
  driverDemand(depotId: number): number {
    let hours = 0;
    for (const l of this.lines.values()) {
      const pool = this.vehiclesOf(l);
      if (!pool.length) continue;
      const share = pool.filter((v) => v.depotId === depotId).length / pool.length;
      hours += vehicleHours(l.timetable, l.cycleTime) * share;
    }
    return Math.ceil((hours / (SHIFT_MAX / 3600)) * 1.3);
  }

  /** Hire drivers so every depot covers its estimated demand. Returns how many were hired. */
  hireMissingDrivers(depotId?: number): number {
    let hired = 0;
    for (const d of this.depots.values()) {
      if (depotId !== undefined && d.id !== depotId) continue;
      const missing = this.driverDemand(d.id) - this.driversOf(d.id).length;
      if (missing > 0 && this.canAfford(missing * DRIVER_HIRE_COST)) {
        this.hireDrivers(d.id, missing);
        hired += missing;
      }
    }
    return hired;
  }

  // ---------------------------------------------------------------- lines

  /** Route a line's runs along the streets. Returns an error message on failure. */
  private buildRuns(stopIds: number[], circular: boolean, mode: ModeId): Run[] | string {
    const seqs = circular
      ? [[...stopIds, stopIds[0]]]
      : [stopIds, [...stopIds].reverse()];
    const vmax = MODES[mode].vehicle.maxSpeed;
    const runs: Run[] = [];
    for (const seq of seqs) {
      const path = new Path();
      const offsets = [0];
      const times = [0];
      for (let i = 0; i + 1 < seq.length; i++) {
        const a = this.stops.get(seq[i])!, b = this.stops.get(seq[i + 1])!;
        const r = this.map.graph.route(a.pos, b.pos);
        if (!r) return `Keine befahrbare Verbindung von „${a.name}“ nach „${b.name}“`;
        const s0 = path.length;
        path.append(r);
        offsets.push(path.length);
        times.push(times[i] + path.travelTime(s0, path.length, vmax) + DWELL_ESTIMATE);
      }
      runs.push({ path, stopIds: seq, offsets, times, lastDeparture: -Infinity });
    }
    return runs;
  }

  /** Try routing a draft line (for previews). */
  previewRoute(a: Stop, b: Stop) {
    return this.map.graph.route(a.pos, b.pos);
  }

  createLine(stopIds: number[], circular: boolean, mode: ModeId = 'bus'): TransitLine | null {
    if (stopIds.length < 2) {
      this.onToast('Eine Linie braucht mindestens zwei Haltestellen', 'error');
      return null;
    }
    const runs = this.buildRuns(stopIds, circular, mode);
    if (typeof runs === 'string') {
      this.onToast(runs, 'error');
      return null;
    }
    const used = new Set([...this.lines.values()].map((l) => l.name));
    let n = 1;
    while (used.has(String(n))) n++;
    const id = this.nextId++;
    const line: TransitLine = {
      id, name: String(n), mode, stopIds: [...stopIds], circular, runs, cycleTime: 0,
      color: LINE_COLORS[(this.lines.size) % LINE_COLORS.length],
      passengersToday: 0, revenueToday: 0, version: 0, timetable: defaultTimetable(), driverShortage: false,
    };
    line.cycleTime = cycleTime(runs);
    this.lines.set(id, line);
    this.routerDirty = true;
    this.onChange();
    return line;
  }

  /**
   * Re-route a line after its stops changed. Buses in service keep running: each one
   * drives to its next stop on the new route, and riding passengers are remapped.
   */
  rebuildLine(line: TransitLine): string | null {
    const runs = this.buildRuns(line.stopIds, line.circular, line.mode);
    if (typeof runs === 'string') return runs;
    const oldRuns = line.runs;
    line.runs = runs;
    line.cycleTime = cycleTime(runs);
    line.version++;

    // Waiting passengers planned with the old stop indices: let them re-plan (they are regenerated).
    for (const s of this.stops.values()) {
      const before = s.waiting.length;
      s.waiting = s.waiting.filter((p) => !p.legs.some((l, i) => i >= p.leg && l.line === line.id));
      this.today.lost += before - s.waiting.length;
    }

    for (const v of this.vehicles.values()) {
      if (v.lineId !== line.id || v.state === 'depot') continue;
      if (v.state === 'deadhead' && v.deadheadTo === 'depot') continue;
      if (v.state === 'deadhead' && v.deadheadTo === 'line') {
        this.dispatch(v, 'line');
        continue;
      }
      const oldRun = oldRuns[v.runIdx];
      const r = Math.min(v.runIdx, runs.length - 1);
      const run = runs[r];
      const curStop = oldRun?.stopIds[v.stopIdx];
      let k = curStop !== undefined ? run.stopIds.indexOf(curStop) : -1;
      if (k < 0) {
        // Stop no longer on the line: continue at the nearest stop of the new route.
        let bd = Infinity;
        run.stopIds.forEach((id, i) => {
          const p = this.stops.get(id)!.pos;
          const d = Math.hypot(p.x - v.x, p.y - v.y);
          if (d < bd && i < run.stopIds.length - 1) {
            bd = d;
            k = i;
          }
        });
      }
      // Remap riding passengers to the new stop indices
      v.passengers = v.passengers.filter((p) => {
        const leg = p.legs[p.leg];
        const ai = run.stopIds.indexOf(leg.alightStop, k);
        const laterUse = p.legs.some((l, i) => i > p.leg && l.line === line.id);
        if (ai < 0 || laterUse) {
          this.today.lost++;
          return false;
        }
        leg.alight = ai;
        leg.run = r;
        leg.ver = line.version;
        return true;
      });
      if (v.state === 'layover' && k === 0) {
        const arrived = v.arrivedAt;
        this.enterLayover(v, line, r);
        v.arrivedAt = arrived;
      } else {
        this.rejoin(v, line, r, k);
      }
    }
    this.routerDirty = true;
    this.onChange();
    return null;
  }

  /** Replace a line's stop sequence. Keeps the old one if routing fails. */
  setLineStops(line: TransitLine, stopIds: number[]): boolean {
    for (let i = 1; i < stopIds.length; i++) {
      if (stopIds[i] === stopIds[i - 1]) {
        this.onToast('Dieselbe Haltestelle kann nicht zweimal hintereinander angefahren werden', 'error');
        return false;
      }
    }
    if (stopIds.length < 2) {
      this.onToast('Eine Linie braucht mindestens zwei Haltestellen', 'error');
      return false;
    }
    const old = line.stopIds;
    line.stopIds = stopIds;
    const err = this.rebuildLine(line);
    if (err) {
      line.stopIds = old;
      this.onToast(err, 'error');
      return false;
    }
    return true;
  }

  /** Send a vehicle in service to stop index k of run r on the (changed) line. */
  private rejoin(v: Vehicle, line: TransitLine, r: number, k: number): void {
    const stop = this.stops.get(line.runs[r].stopIds[k])!;
    const from = this.map.graph.snap(v.x, v.y, 80);
    const route = from && this.map.graph.route(from, stop.pos);
    if (!route) {
      this.today.lost += v.passengers.length;
      v.passengers = [];
      this.dispatch(v, 'line');
      return;
    }
    v.path = Path.from(route);
    v.s = 0;
    v.seg = 0;
    v.runIdx = r;
    v.stopIdx = k;
    v.state = 'deadhead';
    v.deadheadTo = 'rejoin';
  }

  setCircular(line: TransitLine, circular: boolean): void {
    const old = line.circular;
    line.circular = circular;
    const err = this.rebuildLine(line);
    if (err) {
      line.circular = old;
      this.onToast(err, 'error');
    }
  }

  deleteLine(id: number): void {
    const line = this.lines.get(id);
    if (!line) return;
    for (const v of this.vehicles.values()) {
      if (v.lineId === id) {
        v.lineId = null;
        this.dispatch(v, 'depot');
      }
    }
    this.purgePassengers(id);
    this.lines.delete(id);
    this.routerDirty = true;
    this.onChange();
  }

  private purgePassengers(lineId: number): void {
    const uses = (p: Passenger) => p.legs.some((l, i) => i >= p.leg && l.line === lineId);
    for (const s of this.stops.values()) {
      const before = s.waiting.length;
      s.waiting = s.waiting.filter((p) => !uses(p));
      this.today.lost += before - s.waiting.length;
    }
    for (const v of this.vehicles.values()) {
      if (v.lineId === lineId) {
        this.today.lost += v.passengers.length;
        v.passengers = [];
      }
    }
  }

  /** Vehicles of the line that are out of the depot and not heading back. */
  activeVehicles(line: TransitLine): number {
    let n = 0;
    for (const v of this.vehicles.values()) {
      if (v.lineId === line.id && v.state !== 'depot' && !(v.state === 'deadhead' && v.deadheadTo === 'depot')) n++;
    }
    return n;
  }

  /** Vehicles the timetable needs now (looking ahead so buses leave the depot in time). */
  requiredVehicles(line: TransitLine): number {
    const now = vehiclesFor(line.cycleTime, targetHeadway(line.timetable, this.time));
    const soon = vehiclesFor(line.cycleTime, targetHeadway(line.timetable, this.time + DISPATCH_LEAD));
    return Math.max(now, soon);
  }

  peakVehicles(line: TransitLine): number {
    return peakVehicles(line.timetable, line.cycleTime);
  }

  /** Effective headway right now: the timetable's, or longer if vehicles are missing. */
  headway(line: TransitLine): number {
    const target = targetHeadway(line.timetable, this.time);
    const n = this.activeVehicles(line);
    if (!isFinite(target) || n === 0) return Infinity;
    return Math.max(target, line.cycleTime / n);
  }

  setTimetable(line: TransitLine, tt: Timetable): void {
    line.timetable = tt;
    this.routerDirty = true;
    this.onChange();
  }

  vehiclesOf(line: TransitLine): Vehicle[] {
    return [...this.vehicles.values()].filter((v) => v.lineId === line.id);
  }

  linesAtStop(stopId: number): TransitLine[] {
    return [...this.lines.values()].filter((l) => l.stopIds.includes(stopId));
  }

  // ---------------------------------------------------------------- vehicles

  buyVehicle(depotId: number): Vehicle | null {
    const depot = this.depots.get(depotId);
    if (!depot) return null;
    const def = MODES[depot.mode].vehicle;
    if (!this.pay(def.price)) return null;
    const id = this.nextId++;
    this.vehicleCounter++;
    const v: Vehicle = {
      id, name: `${def.name} ${this.vehicleCounter}`, mode: depot.mode, depotId, lineId: null, driverId: null,
      state: 'depot', deadheadTo: 'depot', path: null, s: 0, seg: 0, v: 0, runIdx: 0, stopIdx: 0,
      dwellUntil: 0, arrivedAt: 0, passengers: [], x: depot.pos.x, y: depot.pos.y, ang: 0, kmTotal: 0,
    };
    this.vehicles.set(id, v);
    this.onChange();
    return v;
  }

  sellVehicle(id: number): void {
    const v = this.vehicles.get(id);
    if (!v) return;
    if (v.state !== 'depot') {
      this.onToast('Fahrzeuge können nur im Betriebshof verkauft werden', 'error');
      return;
    }
    this.money += MODES[v.mode].vehicle.price * 0.5;
    this.vehicles.delete(id);
    this.onChange();
  }

  /**
   * Put a vehicle into a line's pool (or take it out with null). Vehicles in the depot wait
   * for the dispatcher; vehicles on the road switch over directly.
   */
  assignVehicle(id: number, lineId: number | null): void {
    const v = this.vehicles.get(id);
    if (!v || v.lineId === lineId) return;
    if (v.lineId !== null) {
      this.today.lost += v.passengers.length;
      v.passengers = [];
    }
    v.lineId = lineId;
    if (v.state !== 'depot') this.dispatch(v, lineId === null ? 'depot' : 'line');
    this.dispatchTimer = DISPATCH_INTERVAL; // run the dispatcher on the next step
    this.routerDirty = true;
    this.onChange();
  }

  /** Assign (or buy) vehicles until the line's pool covers the timetable's peak. */
  assignMissing(line: TransitLine): void {
    let missing = this.peakVehicles(line) - this.vehiclesOf(line).length;
    while (missing-- > 0) {
      const before = this.vehiclesOf(line).length;
      this.addVehicleToLine(line);
      if (this.vehiclesOf(line).length === before) break;
    }
  }

  /** Add a vehicle to a line: reuse an idle one or buy a new one at the closest depot. */
  addVehicleToLine(line: TransitLine): void {
    const first = this.stops.get(line.stopIds[0])!;
    const dist = (d: Depot) => Math.hypot(d.pos.x - first.pos.x, d.pos.y - first.pos.y);
    const idle = [...this.vehicles.values()]
      .filter((v) => v.lineId === null && v.mode === line.mode)
      .sort((a, b) => dist(this.depots.get(a.depotId)!) - dist(this.depots.get(b.depotId)!));
    if (idle.length) {
      this.assignVehicle(idle[0].id, line.id);
      return;
    }
    const depots = [...this.depots.values()].filter((d) => d.mode === line.mode).sort((a, b) => dist(a) - dist(b));
    if (!depots.length) {
      this.onToast('Baue zuerst einen Betriebshof', 'error');
      return;
    }
    const v = this.buyVehicle(depots[0].id);
    if (v) this.assignVehicle(v.id, line.id);
  }

  removeVehicleFromLine(line: TransitLine): void {
    const vs = this.vehiclesOf(line);
    if (!vs.length) return;
    // Prefer a vehicle in the depot, then the emptiest one
    const rank = (v: Vehicle) => (v.state === 'depot' ? -1 : v.state === 'deadhead' && v.deadheadTo === 'depot' ? 0 : 1);
    vs.sort((a, b) => rank(a) - rank(b) || a.passengers.length - b.passengers.length);
    this.assignVehicle(vs[0].id, null);
  }

  /**
   * Start vehicles from the depot when the timetable needs more of them.
   * Surplus vehicles return to the depot from the terminus (see stepVehicle).
   */
  private runDispatcher(): void {
    for (const line of this.lines.values()) {
      line.driverShortage = false;
      let active = this.activeVehicles(line);
      const req = this.requiredVehicles(line);
      if (active >= req) continue;
      const first = this.stops.get(line.runs[0].stopIds[0])!.pos;
      const waiting = this.vehiclesOf(line)
        .filter((v) => v.state === 'depot')
        .sort((a, b) => {
          const da = this.depots.get(a.depotId)!.pos, db = this.depots.get(b.depotId)!.pos;
          return Math.hypot(da.x - first.x, da.y - first.y) - Math.hypot(db.x - first.x, db.y - first.y);
        });
      for (const v of waiting) {
        if (active >= req) break;
        if (!this.takeDriver(v)) {
          line.driverShortage = true;
          continue;
        }
        this.dispatch(v, 'line');
        if (v.state !== 'depot') active++;
      }
    }
    const sig = [...this.lines.values()].map((l) => Math.round(this.headway(l))).join(',');
    if (sig !== this.headwaySig) {
      this.headwaySig = sig;
      this.routerDirty = true;
    }
  }

  private dispatch(v: Vehicle, to: 'line' | 'depot'): void {
    if (v.state === 'depot' && to === 'depot') return;
    if (v.state === 'depot' && !this.takeDriver(v)) return;
    const depot = this.depots.get(v.depotId)!;
    const graph = this.map.graph;
    const from = v.state === 'depot' ? depot.pos : graph.snap(v.x, v.y, 80) ?? depot.pos;
    let dest: RoadPos = depot.pos;
    if (to === 'line') {
      const line = this.lines.get(v.lineId!);
      if (!line) return;
      dest = this.stops.get(line.runs[0].stopIds[0])!.pos;
    }
    const r = graph.route(from, dest);
    if (!r) {
      if (to === 'depot') {
        this.parkInDepot(v);
      } else {
        this.onToast(`${v.name} findet keinen Weg vom Betriebshof zur Linie`, 'error');
        v.lineId = null;
        this.parkInDepot(v);
      }
      return;
    }
    if (v.state === 'depot') {
      v.x = depot.pos.x;
      v.y = depot.pos.y;
    }
    v.path = Path.from(r);
    v.s = 0;
    v.seg = 0;
    v.state = 'deadhead';
    v.deadheadTo = to;
  }

  private parkInDepot(v: Vehicle): void {
    const depot = this.depots.get(v.depotId)!;
    v.state = 'depot';
    v.path = null;
    v.v = 0;
    v.x = depot.pos.x;
    v.y = depot.pos.y;
    this.today.lost += v.passengers.length;
    v.passengers = [];
    this.releaseDriver(v);
  }

  // ---------------------------------------------------------------- simulation

  update(realDt: number): void {
    let dt = Math.min(realDt, 0.25) * SPEEDS[this.speed];
    while (dt > 0) {
      const step = Math.min(dt, 1);
      this.step(step);
      dt -= step;
    }
  }

  private step(dt: number): void {
    const dayBefore = this.day;
    const shiftDayBefore = Math.floor((this.time - 3 * 3600) / 86400);
    this.time += dt;
    if (this.day !== dayBefore) this.newDay();
    // Shift day starts at 03:00: everyone gets a fresh 8 h
    if (Math.floor((this.time - 3 * 3600) / 86400) !== shiftDayBefore) {
      for (const d of this.drivers.values()) d.worked = 0;
    }

    const wage = (this.drivers.size * DRIVER_WAGE_DAY * dt) / 86400;
    this.money -= wage;
    this.today.costs += wage;

    this.dispatchTimer += dt;
    if (this.dispatchTimer >= DISPATCH_INTERVAL) {
      this.dispatchTimer = 0;
      this.runDispatcher();
    }

    if (this.routerDirty) {
      this.router = new Router(this.map.cells, [...this.stops.values()], [...this.lines.values()], (l) =>
        this.headway(l),
      );
      this.routerDirty = false;
      this.networkVersion++;
    }

    this.generateDemand(dt);
    for (const v of this.vehicles.values()) this.stepVehicle(v, dt);

    this.expireTimer += dt;
    if (this.expireTimer > 60) {
      this.expireTimer = 0;
      for (const s of this.stops.values()) {
        const before = s.waiting.length;
        s.waiting = s.waiting.filter((p) => this.time - p.waitingSince < MAX_WAIT);
        this.today.lost += before - s.waiting.length;
      }
    }
  }

  private newDay(): void {
    this.history.push(this.today);
    if (this.history.length > 60) this.history.shift();
    this.today = newDayStats(this.day);
    for (const l of this.lines.values()) {
      l.passengersToday = 0;
      l.revenueToday = 0;
    }
    for (const s of this.stops.values()) s.boardedToday = 0;
  }

  private generateDemand(dt: number): void {
    const R = this.router;
    if (!R || R.coveredRes <= 0 || R.coveredJobs <= 0) return;
    const totals = this.map.totals;
    this.demandAcc += ((R.coveredRes * TRIPS_PER_RESIDENT) / 86400) * demandFactor(this.time) * dt;
    const destShare = R.coveredJobs / Math.max(totals.jobs, 1);
    const cells = this.map.cells;
    while (this.demandAcc >= 1) {
      this.demandAcc -= 1;
      if (Math.random() > destShare) continue;
      const out = Math.random() < outboundShare(this.time);
      const o = R.sampleCell(!out);
      const d = R.sampleCell(out);
      if (o === d) continue;
      const dist = Math.hypot(cells[o].x - cells[d].x, cells[o].y - cells[d].y);
      if (dist < 300) continue;
      const j = R.find(o, d);
      if (!j) continue;
      const walkT = (dist * 1.3) / 1.3;
      const carT = (dist * 1.4) / 9 + 240;
      const alt = Math.min(walkT, carT);
      const p = 0.75 / (1 + Math.exp((j.time - alt) / 400));
      if (Math.random() > p) continue;
      const stop = this.stops.get(j.firstStop);
      if (!stop) continue;
      stop.waiting.push({
        id: this.passengerId++, legs: j.legs, leg: 0,
        readyAt: this.time + j.legs[0].walkBefore, waitingSince: this.time, spawnedAt: this.time,
      });
    }
  }

  private stepVehicle(v: Vehicle, dt: number): void {
    if (v.state === 'depot') return;
    const def = MODES[v.mode].vehicle;
    const cost = (def.costPerHour * dt) / 3600;
    this.money -= cost;
    this.today.costs += cost;
    if (v.driverId !== null) {
      const d = this.drivers.get(v.driverId);
      if (d) d.worked += dt;
    }

    if (v.state === 'deadhead') {
      if (this.drive(v, v.path!.length, dt)) {
        if (v.deadheadTo === 'depot') {
          this.parkInDepot(v);
        } else if (v.deadheadTo === 'rejoin') {
          const line = v.lineId !== null ? this.lines.get(v.lineId) : undefined;
          const run = line?.runs[v.runIdx];
          if (line && run && v.stopIdx < run.stopIds.length) {
            v.path = run.path;
            v.s = run.offsets[v.stopIdx];
            v.seg = run.path.segAt(v.s);
            this.serveStop(v, line, run);
          } else {
            this.dispatch(v, line ? 'line' : 'depot');
          }
        } else {
          const line = v.lineId !== null ? this.lines.get(v.lineId) : undefined;
          if (line) this.enterLayover(v, line, 0);
          else this.dispatch(v, 'depot');
        }
      }
      return;
    }

    const line = v.lineId !== null ? this.lines.get(v.lineId) : undefined;
    if (!line) {
      this.dispatch(v, 'depot');
      return;
    }
    const run = line.runs[v.runIdx];

    switch (v.state) {
      case 'layover': {
        // Driver relief at the terminus when the next trip would exceed the shift
        const drv = v.driverId !== null ? this.drivers.get(v.driverId) : undefined;
        const tripTime = line.cycleTime / line.runs.length;
        if (drv && drv.worked + tripTime > SHIFT_MAX) {
          const fresh = this.availableDriver(v.depotId);
          if (fresh) {
            drv.vehicleId = null;
            fresh.vehicleId = v.id;
            v.driverId = fresh.id;
          } else {
            line.driverShortage = true;
            this.dispatch(v, 'depot');
            break;
          }
        }
        // More vehicles out than the timetable needs: this one goes home
        if (this.activeVehicles(line) > this.requiredVehicles(line)) {
          this.dispatch(v, 'depot');
          break;
        }
        const hw = targetHeadway(line.timetable, this.time);
        if (
          isFinite(hw) && this.time - v.arrivedAt >= MIN_LAYOVER &&
          this.time - run.lastDeparture >= hw * 0.95
        ) {
          run.lastDeparture = this.time;
          v.stopIdx = 0;
          this.serveStop(v, line, run);
        }
        break;
      }
      case 'moving':
        if (this.drive(v, run.offsets[v.stopIdx], dt)) this.serveStop(v, line, run);
        break;
      case 'dwell':
        if (this.time >= v.dwellUntil) {
          if (v.stopIdx >= run.stopIds.length - 1) {
            this.enterLayover(v, line, (v.runIdx + 1) % line.runs.length);
          } else {
            v.stopIdx++;
            v.state = 'moving';
          }
        }
        break;
    }
  }

  private enterLayover(v: Vehicle, line: TransitLine, runIdx: number): void {
    v.runIdx = runIdx;
    v.path = line.runs[runIdx].path;
    v.s = 0;
    v.seg = 0;
    v.v = 0;
    v.stopIdx = 0;
    v.state = 'layover';
    v.arrivedAt = this.time;
    const p = v.path.pointAt(0, 0);
    v.x = p.x;
    v.y = p.y;
  }

  /** Move along the current path towards distance `target`. Returns true when it is reached. */
  private drive(v: Vehicle, target: number, dt: number): boolean {
    const path = v.path!;
    const def = MODES[v.mode].vehicle;
    const remaining = target - v.s;
    if (remaining <= 0.01) {
      v.v = 0;
      return true;
    }
    v.seg = path.segAt(v.s, v.seg);
    const limit = Math.min(path.spd[v.seg] ?? def.maxSpeed, def.maxSpeed);
    const vt = Math.min(limit, Math.sqrt(2 * BRAKE * remaining) + 0.3);
    v.v = v.v < vt ? Math.min(vt, v.v + ACCEL * dt) : vt;
    let ds = v.v * dt;
    let arrived = false;
    if (ds >= remaining) {
      ds = remaining;
      arrived = true;
    }
    v.s += ds;
    v.kmTotal += ds / 1000;
    const c = (def.costPerKm * ds) / 1000;
    this.money -= c;
    this.today.costs += c;
    v.seg = path.segAt(v.s, v.seg);
    const p = path.pointAt(v.s, v.seg);
    v.x = p.x;
    v.y = p.y;
    if (ds > 0.001) v.ang = p.ang;
    if (arrived) v.v = 0;
    return arrived;
  }

  private serveStop(v: Vehicle, line: TransitLine, run: Run): void {
    const k = v.stopIdx;
    const stop = this.stops.get(run.stopIds[k]);
    let moves = 0;
    if (stop) {
      // Alighting
      const keep: Passenger[] = [];
      for (const p of v.passengers) {
        if (p.legs[p.leg].alight !== k) {
          keep.push(p);
          continue;
        }
        moves++;
        p.leg++;
        if (p.leg >= p.legs.length) {
          this.today.trips++;
          continue;
        }
        const next = p.legs[p.leg];
        const nextStop = this.stops.get(next.boardStop);
        if (!nextStop || this.lines.get(next.line)?.version !== next.ver) {
          this.today.lost++;
          continue;
        }
        p.readyAt = this.time + next.walkBefore;
        p.waitingSince = this.time;
        nextStop.waiting.push(p);
      }
      v.passengers = keep;

      // Boarding
      const cap = MODES[v.mode].vehicle.capacity;
      if (k < run.stopIds.length - 1) {
        const remaining: Passenger[] = [];
        for (const p of stop.waiting) {
          const leg = p.legs[p.leg];
          if (
            v.passengers.length < cap && leg.line === line.id && leg.run === v.runIdx &&
            leg.board === k && p.readyAt <= this.time
          ) {
            moves++;
            v.passengers.push(p);
            stop.boardedToday++;
            this.today.boardings++;
            this.today.waitSum += this.time - p.waitingSince;
            line.passengersToday++;
            if (p.leg === 0) {
              this.money += FARE;
              this.today.revenue += FARE;
              line.revenueToday += FARE;
            }
          } else {
            remaining.push(p);
          }
        }
        stop.waiting = remaining;
      }
    }
    v.state = 'dwell';
    v.v = 0;
    v.dwellUntil = this.time + (k === 0 ? 12 : 7) + moves * 1.2;
  }

  // ---------------------------------------------------------------- stats

  get waitingCount(): number {
    let n = 0;
    for (const s of this.stops.values()) n += s.waiting.length;
    return n;
  }

  get ridingCount(): number {
    let n = 0;
    for (const v of this.vehicles.values()) n += v.passengers.length;
    return n;
  }

  // ---------------------------------------------------------------- save / load

  serialize(): SaveData {
    return {
      v: 2,
      area: this.map.area,
      time: this.time,
      money: this.money,
      sandbox: this.sandbox,
      nextId: this.nextId,
      vehicleCounter: this.vehicleCounter,
      stops: [...this.stops.values()].map((s) => ({ id: s.id, name: s.name, mode: s.mode, x: s.pos.x, y: s.pos.y })),
      depots: [...this.depots.values()].map((d) => ({ id: d.id, name: d.name, mode: d.mode, x: d.pos.x, y: d.pos.y })),
      lines: [...this.lines.values()].map((l) => ({
        id: l.id, name: l.name, color: l.color, mode: l.mode, stopIds: l.stopIds, circular: l.circular,
        timetable: l.timetable,
      })),
      vehicles: [...this.vehicles.values()].map((v) => ({
        id: v.id, name: v.name, mode: v.mode, depotId: v.depotId, lineId: v.lineId,
      })),
      drivers: [...this.drivers.values()].map((d) => ({ id: d.id, name: d.name, depotId: d.depotId })),
      history: this.history,
    };
  }

  static restore(map: MapData, data: SaveData): Game {
    const g = new Game(map, { sandbox: data.sandbox, money: data.money });
    g.time = data.time;
    g.nextId = data.nextId;
    g.vehicleCounter = data.vehicleCounter;
    g.history = data.history ?? [];
    g.today = newDayStats(g.day);
    const graph = map.graph;
    for (const s of data.stops) {
      const pos = graph.resnap(s.x, s.y);
      if (pos) g.stops.set(s.id, { id: s.id, name: s.name, mode: s.mode, pos, waiting: [], boardedToday: 0 });
    }
    for (const d of data.depots) {
      const pos = graph.resnap(d.x, d.y);
      if (pos) g.depots.set(d.id, { id: d.id, name: d.name, mode: d.mode, pos });
    }
    for (const l of data.lines) {
      const stopIds = l.stopIds.filter((id) => g.stops.has(id));
      if (stopIds.length < 2) continue;
      const runs = g.buildRuns(stopIds, l.circular, l.mode);
      if (typeof runs === 'string') continue;
      g.lines.set(l.id, {
        ...l, stopIds, runs, cycleTime: cycleTime(runs), passengersToday: 0, revenueToday: 0, version: 0,
        timetable: l.timetable ?? defaultTimetable(), driverShortage: false,
      });
    }
    for (const vd of data.vehicles) {
      const depot = g.depots.get(vd.depotId);
      if (!depot) continue;
      const v: Vehicle = {
        id: vd.id, name: vd.name, mode: vd.mode, depotId: vd.depotId, driverId: null,
        lineId: vd.lineId !== null && g.lines.has(vd.lineId) ? vd.lineId : null,
        state: 'depot', deadheadTo: 'depot', path: null, s: 0, seg: 0, v: 0, runIdx: 0, stopIdx: 0,
        dwellUntil: 0, arrivedAt: 0, passengers: [], x: depot.pos.x, y: depot.pos.y, ang: 0, kmTotal: 0,
      };
      g.vehicles.set(v.id, v);
    }
    if (data.drivers) {
      for (const d of data.drivers) {
        if (g.depots.has(d.depotId)) g.drivers.set(d.id, { ...d, worked: 0, vehicleId: null });
      }
    } else {
      // Saves from before staff existed: give every depot enough drivers for its buses
      for (const depot of g.depots.values()) {
        const n = [...g.vehicles.values()].filter((v) => v.depotId === depot.id).length;
        for (let i = 0; i < Math.ceil(n * 1.6); i++) {
          const id = g.nextId++;
          g.drivers.set(id, { id, name: randomName(), depotId: depot.id, worked: 0, vehicleId: null });
        }
      }
    }
    // Vehicles start in the depot; the dispatcher sends them out per timetable.
    g.dispatchTimer = DISPATCH_INTERVAL;
    return g;
  }
}

function cycleTime(runs: Run[]): number {
  return runs.reduce((t, r) => t + r.times[r.times.length - 1] + MIN_LAYOVER, 0);
}
