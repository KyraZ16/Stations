const EARTH_R = 6378137;
const DEG = Math.PI / 180;

/**
 * Local equirectangular projection around a center point.
 * World units are meters, x grows east, y grows south (screen orientation).
 */
export class Projection {
  readonly kx: number;
  readonly ky: number;

  constructor(readonly lat0: number, readonly lon0: number) {
    this.kx = Math.cos(lat0 * DEG) * EARTH_R * DEG;
    this.ky = EARTH_R * DEG;
  }

  x(lon: number): number {
    return (lon - this.lon0) * this.kx;
  }

  y(lat: number): number {
    return -(lat - this.lat0) * this.ky;
  }

  toLatLon(x: number, y: number): { lat: number; lon: number } {
    return { lat: this.lat0 - y / this.ky, lon: this.lon0 + x / this.kx };
  }
}

export interface Area {
  name: string;
  lat: number;
  lon: number;
  /** Half side length of the square map area in meters. */
  radius: number;
}

/** Returns [south, west, north, east]. */
export function areaBBox(a: Area): [number, number, number, number] {
  const dLat = a.radius / (EARTH_R * DEG);
  const dLon = a.radius / (Math.cos(a.lat * DEG) * EARTH_R * DEG);
  return [a.lat - dLat, a.lon - dLon, a.lat + dLat, a.lon + dLon];
}
