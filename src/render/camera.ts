import { clamp } from '../core/util';

/** 2D camera: world meters → screen CSS pixels. */
export class Camera {
  x = 0;
  y = 0;
  scale = 0.35; // px per meter
  w = 1;
  h = 1;
  minScale = 0.05;
  maxScale = 8;
  half = 2000;

  sx(x: number): number {
    return (x - this.x) * this.scale + this.w / 2;
  }

  sy(y: number): number {
    return (y - this.y) * this.scale + this.h / 2;
  }

  wx(sx: number): number {
    return (sx - this.w / 2) / this.scale + this.x;
  }

  wy(sy: number): number {
    return (sy - this.h / 2) / this.scale + this.y;
  }

  zoomAt(sx: number, sy: number, factor: number): void {
    const wx = this.wx(sx), wy = this.wy(sy);
    this.scale = clamp(this.scale * factor, this.minScale, this.maxScale);
    this.x = wx - (sx - this.w / 2) / this.scale;
    this.y = wy - (sy - this.h / 2) / this.scale;
    this.clampPos();
  }

  pan(dx: number, dy: number): void {
    this.x -= dx / this.scale;
    this.y -= dy / this.scale;
    this.clampPos();
  }

  clampPos(): void {
    this.x = clamp(this.x, -this.half, this.half);
    this.y = clamp(this.y, -this.half, this.half);
  }

  fit(half: number): void {
    this.half = half;
    this.x = 0;
    this.y = 0;
    this.scale = Math.min(this.w, this.h) / (half * 2.1);
    this.minScale = this.scale * 0.6;
  }
}
