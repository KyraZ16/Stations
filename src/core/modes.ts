/**
 * The three network levels of the game. Each level has its own transport modes.
 * Only the bus is playable in the prototype; the others are planned.
 */
export type Tier = 'local' | 'city' | 'regional';
export type ModeId = 'bus' | 'tram' | 'metro' | 'sbahn' | 'train';

export interface ModeDef {
  id: ModeId;
  name: string;
  tier: Tier;
  icon: string;
  available: boolean;
  /** What the mode runs on. Buses share the OSM street network. */
  infra: 'road' | 'tramtrack' | 'metro' | 'rail';
  vehicle: {
    name: string;
    capacity: number;
    maxSpeed: number; // m/s
    price: number;
    costPerHour: number; // vehicle costs while out of the depot (drivers are paid separately)
    costPerKm: number;
    length: number; // m, for drawing
    width: number;
  };
  stopCost: number;
  depotCost: number;
}

export const TIERS: { id: Tier; name: string; desc: string }[] = [
  { id: 'local', name: 'Lokal', desc: 'Bus & Tram' },
  { id: 'city', name: 'Stadtweit', desc: 'U-Bahn & S-Bahn' },
  { id: 'regional', name: 'Regional', desc: 'Regionalzüge' },
];

export const MODES: Record<ModeId, ModeDef> = {
  bus: {
    id: 'bus', name: 'Bus', tier: 'local', icon: '🚌', available: true, infra: 'road',
    vehicle: {
      name: 'Solobus', capacity: 80, maxSpeed: 50 / 3.6, price: 250_000,
      costPerHour: 12, costPerKm: 1.1, length: 12, width: 2.55,
    },
    stopCost: 5_000,
    depotCost: 400_000,
  },
  tram: {
    id: 'tram', name: 'Tram', tier: 'local', icon: '🚋', available: false, infra: 'tramtrack',
    vehicle: {
      name: 'Niederflurtram', capacity: 180, maxSpeed: 60 / 3.6, price: 2_500_000,
      costPerHour: 60, costPerKm: 2, length: 30, width: 2.4,
    },
    stopCost: 40_000,
    depotCost: 3_000_000,
  },
  metro: {
    id: 'metro', name: 'U-Bahn', tier: 'city', icon: 'Ⓤ', available: false, infra: 'metro',
    vehicle: {
      name: 'U-Bahn-Zug', capacity: 700, maxSpeed: 80 / 3.6, price: 8_000_000,
      costPerHour: 120, costPerKm: 6, length: 100, width: 2.6,
    },
    stopCost: 30_000_000,
    depotCost: 20_000_000,
  },
  sbahn: {
    id: 'sbahn', name: 'S-Bahn', tier: 'city', icon: 'Ⓢ', available: false, infra: 'rail',
    vehicle: {
      name: 'S-Bahn-Zug', capacity: 900, maxSpeed: 120 / 3.6, price: 10_000_000,
      costPerHour: 150, costPerKm: 8, length: 140, width: 3,
    },
    stopCost: 8_000_000,
    depotCost: 25_000_000,
  },
  train: {
    id: 'train', name: 'Regionalzug', tier: 'regional', icon: '🚆', available: false, infra: 'rail',
    vehicle: {
      name: 'Triebwagen', capacity: 500, maxSpeed: 160 / 3.6, price: 12_000_000,
      costPerHour: 180, costPerKm: 10, length: 120, width: 3,
    },
    stopCost: 5_000_000,
    depotCost: 30_000_000,
  },
};
