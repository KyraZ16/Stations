import { ModeId } from '../core/modes';
import { RoadPos } from '../map/roadgraph';
import { Path } from './path';
import { Timetable } from './timetable';

export interface Stop {
  id: number;
  name: string;
  mode: ModeId;
  pos: RoadPos;
  waiting: Passenger[];
  boardedToday: number;
}

export interface Depot {
  id: number;
  name: string;
  mode: ModeId;
  pos: RoadPos;
}

/** One direction of a line: a path through an ordered list of stops. */
export interface Run {
  path: Path;
  stopIds: number[];
  offsets: number[]; // distance of each stop along the path
  times: number[]; // scheduled cumulative time at each stop (incl. dwell)
  lastDeparture: number;
}

export interface TransitLine {
  id: number;
  name: string;
  color: string;
  mode: ModeId;
  stopIds: number[];
  circular: boolean;
  runs: Run[];
  cycleTime: number;
  /** Incremented when the route changes; passenger plans from older versions are invalid. */
  version: number;
  timetable: Timetable;
  /** Set by the dispatcher when a vehicle could not leave for lack of a driver. */
  driverShortage: boolean;
  passengersToday: number;
  revenueToday: number;
}

export interface Driver {
  id: number;
  name: string;
  depotId: number;
  /** Seconds worked in the current shift day. */
  worked: number;
  vehicleId: number | null;
}

export type VehicleState = 'depot' | 'deadhead' | 'layover' | 'moving' | 'dwell';

export interface Vehicle {
  id: number;
  name: string;
  mode: ModeId;
  depotId: number;
  /** Line the vehicle is assigned to (its pool). It only runs when the timetable needs it. */
  lineId: number | null;
  driverId: number | null;
  state: VehicleState;
  /** For deadhead trips: where the trip ends. */
  deadheadTo: 'line' | 'depot' | 'rejoin';
  path: Path | null;
  s: number;
  seg: number;
  v: number;
  runIdx: number;
  stopIdx: number;
  dwellUntil: number;
  arrivedAt: number;
  passengers: Passenger[];
  x: number;
  y: number;
  ang: number;
  kmTotal: number;
}

export interface Leg {
  line: number;
  run: number;
  board: number; // stop index within run
  alight: number;
  boardStop: number;
  alightStop: number;
  walkBefore: number; // seconds walking to the boarding stop (transfer walks)
  ver: number; // line version the plan was made for
}

export interface Passenger {
  id: number;
  legs: Leg[];
  leg: number;
  readyAt: number;
  waitingSince: number;
  spawnedAt: number;
}

export interface DayStats {
  day: number;
  trips: number; // completed journeys
  boardings: number;
  lost: number; // gave up waiting
  revenue: number;
  costs: number;
  waitSum: number;
}

export const newDayStats = (day: number): DayStats => ({
  day, trips: 0, boardings: 0, lost: 0, revenue: 0, costs: 0, waitSum: 0,
});
