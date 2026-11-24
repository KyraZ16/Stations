import { MODES, ModeId, TIERS, Tier } from '../core/modes';
import {
  LINE_COLORS, distToSegment, escapeHtml, fmtClock, fmtDist, fmtDuration, fmtMoney, fmtNum,
} from '../core/util';
import { RouteResult } from '../map/roadgraph';
import { Camera } from '../render/camera';
import { RenderState, Renderer, Selection } from '../render/renderer';
import { DRIVER_HIRE_COST, DRIVER_WAGE_DAY, Game, SHIFT_MAX, SPEEDS } from '../sim/game';
import {
  HEADWAY_OPTIONS, PERIODS, fmtHour, periodAt, periodBounds, vehicleHours, vehiclesFor,
} from '../sim/timetable';
import { Driver, Stop, TransitLine, Vehicle } from '../sim/types';
import { SAVE_KEY } from './start';

const fmtShift = (s: number): string => `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, '0')} h`;

/** 'edit' is not in the toolbar: it is entered from the line panel. */
type Tool = 'select' | 'stop' | 'line' | 'depot' | 'bulldoze' | 'edit';

const TOOLS: { id: Exclude<Tool, 'edit'>; name: string; icon: string; key: string }[] = [
  { id: 'select', name: 'Auswählen', icon: '↖', key: 'V' },
  { id: 'stop', name: 'Haltestelle', icon: '◉', key: 'H' },
  { id: 'line', name: 'Linie zeichnen', icon: '〰', key: 'L' },
  { id: 'depot', name: 'Betriebshof', icon: '▣', key: 'B' },
  { id: 'bulldoze', name: 'Abreißen', icon: '✕', key: 'X' },
];

const HINTS: Record<Tool, string> = {
  select: 'Klicke auf Haltestellen, Linien, Busse oder Betriebshöfe · Ziehen zum Verschieben · Mausrad zum Zoomen',
  stop: 'Klicke auf eine Straße, um eine Haltestelle zu setzen',
  line: 'Klicke Haltestellen nacheinander an (oder auf eine Straße für eine neue) · <kbd>Enter</kbd> fertig · <kbd>Rechtsklick</kbd> letzte entfernen · erste Haltestelle erneut = Ringlinie',
  depot: 'Klicke auf eine Straße, um einen Betriebshof zu bauen',
  bulldoze: 'Klicke auf Haltestelle, Linie oder Betriebshof, um sie zu entfernen',
  edit: 'Klick auf Straße oder Haltestelle fügt sie passend ein · Klick auf eine Haltestelle der Linie entfernt sie · <kbd>Enter</kbd> fertig',
};

const VEHICLE_STATE: Record<Vehicle['state'], string> = {
  depot: 'Im Betriebshof',
  deadhead: 'Leerfahrt',
  layover: 'Wendezeit an Endhaltestelle',
  moving: 'Unterwegs',
  dwell: 'An Haltestelle',
};

export class App {
  private canvas: HTMLCanvasElement;
  private cam = new Camera();
  private renderer: Renderer;
  private topbar: HTMLElement;
  private toolbar: HTMLElement;
  private panel: HTMLElement;
  private hint: HTMLElement;
  private toasts: HTMLElement;

  private tool: Tool = 'select';
  private tier: Tier = 'local';
  private mode: ModeId = 'bus';
  private selected: Selection = null;
  private coverage = false;
  private draft: { stops: Stop[]; paths: RouteResult[]; circular: boolean } = { stops: [], paths: [], circular: false };
  private hoverPos: RenderState['hoverPos'] = null;
  private hoverStop: Stop | null = null;
  private mouseWorld: { x: number; y: number } | null = null;

  private drag: { x: number; y: number; moved: boolean; button: number } | null = null;
  private last = performance.now();
  private panelTimer = 0;
  private saveTimer = 0;
  private panelDirty = true;

  constructor(root: HTMLElement, private game: Game) {
    root.innerHTML = `
      <canvas id="map"></canvas>
      <div id="topbar"></div>
      <div id="toolbar"></div>
      <div id="panel"></div>
      <div id="hint"></div>
      <div id="toasts"></div>
      <div class="attribution">© OpenStreetMap-Mitwirkende</div>`;
    this.canvas = root.querySelector('canvas')!;
    this.topbar = root.querySelector('#topbar')!;
    this.toolbar = root.querySelector('#toolbar')!;
    this.panel = root.querySelector('#panel')!;
    this.hint = root.querySelector('#hint')!;
    this.toasts = root.querySelector('#toasts')!;
    this.renderer = new Renderer(this.canvas, this.cam, game.map);
    this.renderer.resize();
    this.cam.fit(game.map.half);

    game.onToast = (m, k) => this.toast(m, k);
    game.onChange = () => (this.panelDirty = true);

    this.bindInput();
    this.renderToolbar();
    this.renderTopbar();
    this.renderPanel();
    this.setTool('select');
    requestAnimationFrame(this.frame);
  }

  // ------------------------------------------------------------ main loop

  private frame = (now: number) => {
    const dt = (now - this.last) / 1000;
    this.last = now;
    this.game.update(dt);
    this.renderer.draw(this.game, {
      tool: this.tool,
      hoverPos: this.hoverPos,
      hoverStop: this.hoverStop,
      draftStops: this.draft.stops,
      draftPaths: this.draft.paths,
      draftCircular: this.draft.circular,
      mouseWorld: this.mouseWorld,
      selected: this.selected,
      coverage: this.coverage,
    });
    this.panelTimer += dt;
    if (this.panelTimer > 0.4 || this.panelDirty) {
      this.panelTimer = 0;
      this.renderTopbar();
      this.renderPanel();
    }
    this.saveTimer += dt;
    if (this.saveTimer > 120) {
      this.saveTimer = 0;
      this.save(true);
    }
    requestAnimationFrame(this.frame);
  };

  // ------------------------------------------------------------ input

  private bindInput(): void {
    const c = this.canvas;
    window.addEventListener('resize', () => this.renderer.resize());
    c.addEventListener('contextmenu', (e) => e.preventDefault());
    c.addEventListener('wheel', (e) => {
      e.preventDefault();
      this.cam.zoomAt(e.offsetX, e.offsetY, Math.exp(-e.deltaY * 0.0015));
    }, { passive: false });
    c.addEventListener('mousedown', (e) => {
      this.drag = { x: e.clientX, y: e.clientY, moved: false, button: e.button };
    });
    window.addEventListener('mousemove', (e) => {
      if (this.drag) {
        const dx = e.clientX - this.drag.x, dy = e.clientY - this.drag.y;
        if (this.drag.moved || Math.hypot(dx, dy) > 4) {
          this.drag.moved = true;
          this.cam.pan(dx, dy);
          this.drag.x = e.clientX;
          this.drag.y = e.clientY;
          c.classList.add('grabbing');
        }
      }
      if (e.target === c) this.updateHover(e.offsetX, e.offsetY);
      else this.mouseWorld = null;
    });
    window.addEventListener('mouseup', (e) => {
      const d = this.drag;
      this.drag = null;
      c.classList.remove('grabbing');
      if (!d || d.moved || e.target !== c) return;
      if (d.button === 0) this.click(e.offsetX, e.offsetY);
      else if (d.button === 2) this.rightClick();
    });
    window.addEventListener('keydown', (e) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') {
        if (e.key === 'Enter') (e.target as HTMLElement).blur();
        return;
      }
      const k = e.key.toLowerCase();
      const tool = TOOLS.find((t) => t.key.toLowerCase() === k);
      if (tool) this.setTool(tool.id);
      else if (k === 'escape') {
        if (this.draft.stops.length) this.cancelDraft();
        else if (this.tool !== 'select') this.setTool('select');
        else this.select(null);
      } else if (k === 'enter' && this.tool === 'line') this.finishDraft();
      else if (k === 'enter' && this.tool === 'edit') this.setTool('select');
      else if (k === ' ') {
        e.preventDefault();
        this.setSpeed(this.game.speed === 0 ? 1 : 0);
      } else if (k === '1' || k === '2' || k === '3') this.setSpeed(+k);
      else if (k === 'c') this.toggleCoverage();
      else if (k === 'delete' && this.selected) this.deleteSelected();
    });

    this.toolbar.addEventListener('click', (e) => this.onUiClick(e));
    this.topbar.addEventListener('click', (e) => this.onUiClick(e));
    this.panel.addEventListener('click', (e) => this.onUiClick(e));
    this.panel.addEventListener('change', (e) => this.onUiChange(e));
  }

  private worldAt(sx: number, sy: number) {
    return { x: this.cam.wx(sx), y: this.cam.wy(sy) };
  }

  private updateHover(sx: number, sy: number): void {
    const w = this.worldAt(sx, sy);
    this.mouseWorld = w;
    this.hoverStop = null;
    this.hoverPos = null;
    if (this.tool === 'select') return;
    this.hoverStop = this.game.stopAt(w.x, w.y, 12 / this.cam.scale);
    if (this.tool === 'stop' || this.tool === 'line' || this.tool === 'edit' || this.tool === 'depot') {
      this.hoverPos = this.game.map.graph.snap(w.x, w.y, Math.max(25, 20 / this.cam.scale));
    }
  }

  private click(sx: number, sy: number): void {
    const w = this.worldAt(sx, sy);
    const g = this.game;
    const r = 12 / this.cam.scale;
    switch (this.tool) {
      case 'select':
        this.select(this.pick(w.x, w.y));
        break;
      case 'stop': {
        if (g.stopAt(w.x, w.y, r)) {
          this.select(this.pick(w.x, w.y));
          return;
        }
        const pos = g.map.graph.snap(w.x, w.y, Math.max(25, 20 / this.cam.scale));
        if (!pos) return this.toast('Hier ist keine befahrbare Straße', 'error');
        const s = g.addStop(pos, this.mode);
        if (s) this.selected = { kind: 'stop', id: s.id };
        break;
      }
      case 'depot': {
        const pos = g.map.graph.snap(w.x, w.y, Math.max(25, 20 / this.cam.scale));
        if (!pos) return this.toast('Hier ist keine befahrbare Straße', 'error');
        const d = g.addDepot(pos, this.mode);
        if (d) {
          this.select({ kind: 'depot', id: d.id });
          this.setTool('select');
        }
        break;
      }
      case 'line':
        this.lineClick(w.x, w.y);
        break;
      case 'edit':
        this.editClick(w.x, w.y);
        break;
      case 'bulldoze': {
        const p = this.pick(w.x, w.y, false);
        if (p) {
          this.selected = p;
          this.deleteSelected();
        }
        break;
      }
    }
    this.panelDirty = true;
  }

  private rightClick(): void {
    if (this.tool === 'line' && this.draft.stops.length) {
      this.draft.stops.pop();
      this.draft.paths.pop();
      this.draft.circular = false;
      this.panelDirty = true;
    } else if (this.tool !== 'select') {
      this.setTool('select');
    }
  }

  private pick(x: number, y: number, vehicles = true): Selection {
    const g = this.game;
    const r = 12 / this.cam.scale;
    if (vehicles) {
      let best: Vehicle | null = null, bd = r;
      for (const v of g.vehicles.values()) {
        if (v.state === 'depot') continue;
        const d = Math.hypot(v.x - x, v.y - y);
        if (d < bd) {
          bd = d;
          best = v;
        }
      }
      if (best) return { kind: 'vehicle', id: best.id };
    }
    const stop = g.stopAt(x, y, r);
    if (stop) return { kind: 'stop', id: stop.id };
    for (const d of g.depots.values()) {
      if (Math.hypot(d.pos.x - x, d.pos.y - y) < r) return { kind: 'depot', id: d.id };
    }
    const lr = 7 / this.cam.scale;
    for (const l of g.lines.values()) {
      for (const run of l.runs) {
        const p = run.path;
        for (let i = 0; i + 1 < p.xs.length; i++) {
          if (distToSegment(x, y, p.xs[i], p.ys[i], p.xs[i + 1], p.ys[i + 1]).d < lr) {
            return { kind: 'line', id: l.id };
          }
        }
      }
    }
    return null;
  }

  // ------------------------------------------------------------ line drawing

  private lineClick(x: number, y: number): void {
    const g = this.game;
    const d = this.draft;
    let stop = g.stopAt(x, y, 12 / this.cam.scale);
    if (!stop) {
      const pos = g.map.graph.snap(x, y, Math.max(25, 20 / this.cam.scale));
      if (!pos) return this.toast('Hier ist keine befahrbare Straße', 'error');
      stop = g.addStop(pos, this.mode);
      if (!stop) return;
    }
    if (d.stops.length && d.stops[d.stops.length - 1].id === stop.id) return;
    if (d.stops.length >= 2 && d.stops[0].id === stop.id) {
      const r = g.previewRoute(d.stops[d.stops.length - 1], stop);
      if (!r) return this.toast('Keine befahrbare Verbindung zur ersten Haltestelle', 'error');
      d.circular = true;
      this.finishDraft();
      return;
    }
    if (d.stops.length) {
      const r = g.previewRoute(d.stops[d.stops.length - 1], stop);
      if (!r) return this.toast(`Keine befahrbare Verbindung nach „${stop.name}“`, 'error');
      d.paths.push(r);
    }
    d.stops.push(stop);
    this.panelDirty = true;
  }

  // ------------------------------------------------------------ line editing

  private editLineId: number | null = null;

  private startEdit(id: number): void {
    if (!this.game.lines.has(id)) return;
    this.setTool('edit');
    this.editLineId = id;
    this.selected = { kind: 'line', id };
    this.panelDirty = true;
  }

  private editClick(x: number, y: number): void {
    const g = this.game;
    const line = this.editLineId !== null ? g.lines.get(this.editLineId) : undefined;
    if (!line) return this.setTool('select');
    let stop = g.stopAt(x, y, 12 / this.cam.scale);
    if (stop && line.stopIds.includes(stop.id)) {
      if (line.stopIds.length <= 2) return this.toast('Eine Linie braucht mindestens zwei Haltestellen', 'error');
      const id = stop.id;
      g.setLineStops(line, line.stopIds.filter((s) => s !== id));
      return;
    }
    let created = false;
    if (!stop) {
      const pos = g.map.graph.snap(x, y, Math.max(25, 20 / this.cam.scale));
      if (!pos) return this.toast('Hier ist keine befahrbare Straße', 'error');
      stop = g.addStop(pos, line.mode);
      if (!stop) return;
      created = true;
    }
    const ok = g.setLineStops(line, this.insertBest(line.stopIds, line.circular, stop));
    if (!ok && created) g.removeStop(stop.id);
  }

  /** Insert a stop where it adds the smallest detour (straight-line distances). */
  private insertBest(ids: number[], circular: boolean, stop: Stop): number[] {
    const g = this.game;
    const p = (id: number) => g.stops.get(id)!.pos;
    const d = (a: { x: number; y: number }, b: { x: number; y: number }) => Math.hypot(a.x - b.x, a.y - b.y);
    const n = ids.length;
    let best = Infinity, at = n;
    for (let i = 0; i <= n; i++) {
      let cost: number;
      if (i > 0 && i < n) cost = d(p(ids[i - 1]), stop.pos) + d(stop.pos, p(ids[i])) - d(p(ids[i - 1]), p(ids[i]));
      else if (circular) cost = d(p(ids[n - 1]), stop.pos) + d(stop.pos, p(ids[0])) - d(p(ids[n - 1]), p(ids[0]));
      else cost = d(stop.pos, p(ids[i === 0 ? 0 : n - 1]));
      if (cost < best) {
        best = cost;
        at = i === 0 && circular ? n : i;
      }
    }
    const out = [...ids];
    out.splice(at, 0, stop.id);
    return out;
  }

  private finishDraft(): void {
    const d = this.draft;
    if (d.stops.length < 2) {
      this.toast('Eine Linie braucht mindestens zwei Haltestellen', 'error');
      return;
    }
    const line = this.game.createLine(d.stops.map((s) => s.id), d.circular, this.mode);
    if (!line) return;
    this.draft = { stops: [], paths: [], circular: false };
    this.setTool('select');
    this.select({ kind: 'line', id: line.id });
    if (this.game.depots.size) {
      // Equip the line for its default timetable: buses for the peak, drivers for the shifts.
      this.game.assignMissing(line);
      const hired = this.game.hireMissingDrivers();
      const n = this.game.vehiclesOf(line).length;
      this.toast(`Linie ${line.name} erstellt – ${n} ${n === 1 ? 'Bus' : 'Busse'} zugewiesen` +
        (hired ? `, ${hired} Fahrer eingestellt` : ''));
    } else {
      this.toast(`Linie ${line.name} erstellt – baue einen Betriebshof, um Busse einzusetzen`);
    }
  }

  private cancelDraft(): void {
    this.draft = { stops: [], paths: [], circular: false };
    this.panelDirty = true;
  }

  // ------------------------------------------------------------ actions

  private setTool(t: Tool): void {
    if (this.tool === 'line' && t !== 'line' && this.draft.stops.length) this.cancelDraft();
    if (t !== 'edit') this.editLineId = null;
    this.tool = t;
    this.canvas.classList.toggle('crosshair', t !== 'select');
    this.hint.innerHTML = HINTS[t];
    this.hoverPos = null;
    this.hoverStop = null;
    this.renderToolbar();
    this.panelDirty = true;
  }

  private setSpeed(s: number): void {
    this.game.speed = s;
    this.renderTopbar();
  }

  private toggleCoverage(): void {
    this.coverage = !this.coverage;
    this.renderToolbar();
  }

  private select(s: Selection): void {
    this.selected = s;
    this.panelDirty = true;
  }

  private deleteSelected(): void {
    const s = this.selected;
    const g = this.game;
    if (!s) return;
    if (s.kind === 'stop') {
      const stop = g.stops.get(s.id);
      const lines = g.linesAtStop(s.id);
      if (stop && lines.length && !confirm(`„${stop.name}“ wird von ${lines.length} Linie(n) bedient. Trotzdem abreißen?`)) return;
      g.removeStop(s.id);
    } else if (s.kind === 'line') {
      const l = g.lines.get(s.id);
      if (l && !confirm(`Linie ${l.name} löschen? Die Busse fahren zurück in den Betriebshof.`)) return;
      g.deleteLine(s.id);
    } else if (s.kind === 'depot') {
      g.removeDepot(s.id);
      if (g.depots.has(s.id)) return;
    } else if (s.kind === 'vehicle') {
      return;
    }
    this.selected = null;
    this.panelDirty = true;
  }

  private save(auto = false): void {
    try {
      localStorage.setItem(SAVE_KEY, JSON.stringify(this.game.serialize()));
      if (!auto) this.toast('Spielstand gespeichert');
    } catch (err) {
      this.toast(`Speichern fehlgeschlagen: ${String(err)}`, 'error');
    }
  }

  toast(msg: string, kind: 'info' | 'error' = 'info'): void {
    const el = document.createElement('div');
    el.className = `toast ${kind}`;
    el.textContent = msg;
    this.toasts.appendChild(el);
    setTimeout(() => el.remove(), kind === 'error' ? 4000 : 2600);
    while (this.toasts.children.length > 4) this.toasts.firstChild!.remove();
  }

  // ------------------------------------------------------------ UI events

  private onUiClick(e: Event): void {
    const el = (e.target as HTMLElement).closest<HTMLElement>('[data-act]');
    if (!el) return;
    const act = el.dataset.act!;
    const id = el.dataset.id ? +el.dataset.id : NaN;
    const g = this.game;
    const line = this.selected?.kind === 'line' ? g.lines.get(this.selected.id) : undefined;
    switch (act) {
      case 'tool': this.setTool(el.dataset.id as Tool); break;
      case 'tier': {
        this.tier = el.dataset.id as Tier;
        const first = Object.values(MODES).find((m) => m.tier === this.tier && m.available);
        if (first) this.mode = first.id;
        this.renderToolbar();
        break;
      }
      case 'mode': {
        const m = MODES[el.dataset.id as ModeId];
        if (!m.available) this.toast(`${m.name} ist noch in Planung – kommt in einer späteren Version`);
        else this.mode = m.id;
        this.renderToolbar();
        break;
      }
      case 'speed': this.setSpeed(id); break;
      case 'coverage': this.toggleCoverage(); break;
      case 'save': this.save(); break;
      case 'menu':
        this.save(true);
        location.reload();
        break;
      case 'close': this.select(null); break;
      case 'select-line': this.select({ kind: 'line', id }); this.focusLine(id); break;
      case 'select-stop': this.select({ kind: 'stop', id }); this.focusOn(g.stops.get(id)?.pos); break;
      case 'select-vehicle': {
        this.select({ kind: 'vehicle', id });
        const v = g.vehicles.get(id);
        if (v && v.state !== 'depot') this.focusOn(v);
        break;
      }
      case 'select-depot': this.select({ kind: 'depot', id }); this.focusOn(g.depots.get(id)?.pos); break;
      case 'new-line': this.setTool('line'); break;
      case 'edit-line': this.startEdit(id); break;
      case 'edit-done': this.setTool('select'); break;
      case 'edit-up':
      case 'edit-down':
      case 'edit-remove': {
        const l = this.editLineId !== null ? g.lines.get(this.editLineId) : undefined;
        const i = +el.dataset.idx!;
        if (!l) break;
        const ids = [...l.stopIds];
        if (act === 'edit-remove') ids.splice(i, 1);
        else {
          const j = act === 'edit-up' ? i - 1 : i + 1;
          if (j < 0 || j >= ids.length) break;
          [ids[i], ids[j]] = [ids[j], ids[i]];
        }
        g.setLineStops(l, ids);
        break;
      }
      case 'finish-line': this.finishDraft(); break;
      case 'cancel-line': this.cancelDraft(); break;
      case 'veh-add': if (line) g.addVehicleToLine(line); break;
      case 'veh-remove': if (line) g.removeVehicleFromLine(line); break;
      case 'color': if (line) { line.color = el.dataset.color!; this.panelDirty = true; } break;
      case 'delete': this.deleteSelected(); break;
      case 'buy': {
        const v = g.buyVehicle(id);
        if (v) this.toast(`${v.name} gekauft`);
        break;
      }
      case 'sell': g.sellVehicle(id); if (!g.vehicles.has(id)) this.select(null); break;
      case 'to-depot': g.assignVehicle(id, null); break;
      case 'line-tab': this.lineTab = el.dataset.tab as typeof this.lineTab; break;
      case 'assign-missing': if (line) g.assignMissing(line); break;
      case 'hire': g.hireDrivers(id, +(el.dataset.n ?? 1)); break;
      case 'fire': g.fireDriver(id); break;
      case 'hire-missing': {
        const n = g.hireMissingDrivers(Number.isNaN(id) ? undefined : id);
        this.toast(n ? `${n} Fahrer eingestellt` : 'Es fehlen keine Fahrer');
        break;
      }
    }
    this.panelDirty = true;
  }

  private onUiChange(e: Event): void {
    const el = e.target as HTMLInputElement | HTMLSelectElement;
    const g = this.game;
    const s = this.selected;
    switch (el.dataset.field) {
      case 'name': {
        const v = el.value.trim();
        if (!v || !s) break;
        const obj = s.kind === 'line' ? g.lines.get(s.id) : s.kind === 'stop' ? g.stops.get(s.id)
          : s.kind === 'vehicle' ? g.vehicles.get(s.id) : g.depots.get(s.id);
        if (obj) obj.name = v;
        break;
      }
      case 'circular': {
        const l = s?.kind === 'line' ? g.lines.get(s.id) : undefined;
        if (l) g.setCircular(l, (el as HTMLInputElement).checked);
        break;
      }
      case 'assign': {
        if (s?.kind !== 'vehicle') break;
        g.assignVehicle(s.id, el.value === '' ? null : +el.value);
        break;
      }
      case 'draft-circular':
        this.draft.circular = (el as HTMLInputElement).checked;
        break;
      case 'tt-start':
      case 'tt-end':
      case 'tt-hw': {
        const l = s?.kind === 'line' ? g.lines.get(s.id) : undefined;
        if (!l) break;
        const tt = { ...l.timetable, headways: [...l.timetable.headways] };
        const val = parseFloat(el.value);
        if (el.dataset.field === 'tt-start') tt.start = val;
        else if (el.dataset.field === 'tt-end') tt.end = val;
        else tt.headways[+el.dataset.period!] = val;
        if (tt.end <= tt.start) {
          this.toast('Das Betriebsende muss nach dem Betriebsbeginn liegen', 'error');
          break;
        }
        g.setTimetable(l, tt);
        break;
      }
    }
    this.panelDirty = true;
    (el as HTMLElement).blur();
  }

  private focusOn(p?: { x: number; y: number }): void {
    if (!p) return;
    this.cam.x = p.x;
    this.cam.y = p.y;
  }

  private focusLine(id: number): void {
    const l = this.game.lines.get(id);
    if (!l) return;
    const p = l.runs[0].path;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let i = 0; i < p.xs.length; i++) {
      minX = Math.min(minX, p.xs[i]); maxX = Math.max(maxX, p.xs[i]);
      minY = Math.min(minY, p.ys[i]); maxY = Math.max(maxY, p.ys[i]);
    }
    this.cam.x = (minX + maxX) / 2;
    this.cam.y = (minY + maxY) / 2;
    const span = Math.max(maxX - minX, maxY - minY, 300);
    this.cam.scale = Math.min(Math.max(Math.min(this.cam.w, this.cam.h) / (span * 1.6), this.cam.minScale), this.cam.maxScale);
  }

  // ------------------------------------------------------------ rendering UI

  private renderToolbar(): void {
    const modes = Object.values(MODES).filter((m) => m.tier === this.tier);
    this.toolbar.innerHTML = `
      <div class="tiers">
        ${TIERS.map((t) => `<button data-act="tier" data-id="${t.id}" class="${t.id === this.tier ? 'on' : ''}" title="${t.desc}">${t.name}</button>`).join('')}
      </div>
      <div class="modes">
        ${modes.map((m) => `<button data-act="mode" data-id="${m.id}" class="${m.id === this.mode ? 'on' : ''} ${m.available ? '' : 'locked'}">
          <span class="ic">${m.icon}</span>${m.name}${m.available ? '' : '<small>geplant</small>'}</button>`).join('')}
      </div>
      <div class="toolbar-section">Werkzeuge</div>
      <div class="tools">
        ${TOOLS.map((t) => `<button data-act="tool" data-id="${t.id}" class="${t.id === this.tool ? 'on' : ''}">
          <span class="ic">${t.icon}</span>${t.name}<kbd>${t.key}</kbd></button>`).join('')}
      </div>
      <div class="toolbar-section">Ansicht</div>
      <label class="toggle" data-act="coverage"><input type="checkbox" ${this.coverage ? 'checked' : ''} style="pointer-events:none"> Einzugsgebiet <kbd style="margin-left:auto;opacity:.6">C</kbd></label>`;
  }

  private renderTopbar(): void {
    const g = this.game;
    const t = g.today;
    const speedIcons = ['⏸', '▶', '▶▶', '▶▶▶'];
    this.topbar.innerHTML = `
      <div class="logo">Stations<span>.</span></div>
      <div class="sep"></div>
      <div class="clock">Tag ${g.day} · ${fmtClock(g.time)}</div>
      <div class="speed">${SPEEDS.map((_, i) => `<button data-act="speed" data-id="${i}" class="${g.speed === i ? 'on' : ''}" title="Taste ${i === 0 ? 'Leertaste' : i}">${speedIcons[i]}</button>`).join('')}</div>
      <div class="sep"></div>
      <div class="stat"><small>${g.sandbox ? 'Kontostand (Sandbox)' : 'Kontostand'}</small><b class="${g.money < 0 ? 'neg' : ''}">${fmtMoney(g.money)}</b></div>
      <div class="stat"><small>Fahrgäste heute</small><b>${fmtNum(t.trips)}</b></div>
      <div class="stat"><small>Einnahmen / Kosten</small><b><span class="pos">${fmtMoney(t.revenue)}</span> / <span class="neg">${fmtMoney(t.costs)}</span></b></div>
      <div class="stat"><small>Wartend / im Bus</small><b>${fmtNum(g.waitingCount)} / ${fmtNum(g.ridingCount)}</b></div>
      <div class="grow"></div>
      <button class="btn small" data-act="save">💾 Speichern</button>
      <button class="btn small" data-act="menu">Menü</button>`;
  }

  private renderPanel(): void {
    // Don't clobber an input the user is typing in
    const active = document.activeElement;
    if (active && this.panel.contains(active) && (active.tagName === 'INPUT' || active.tagName === 'SELECT')) return;
    this.panelDirty = false;
    const scroll = this.panel.scrollTop;
    this.panel.innerHTML = this.panelHtml();
    this.panel.scrollTop = scroll;
  }

  private panelHtml(): string {
    const g = this.game;
    if (this.tool === 'line') return this.draftHtml();
    if (this.tool === 'edit' && this.editLineId !== null && g.lines.has(this.editLineId)) {
      return this.editHtml(this.editLineId);
    }
    const s = this.selected;
    if (s?.kind === 'stop' && g.stops.has(s.id)) return this.stopHtml(s.id);
    if (s?.kind === 'line' && g.lines.has(s.id)) return this.lineHtml(s.id);
    if (s?.kind === 'vehicle' && g.vehicles.has(s.id)) return this.vehicleHtml(s.id);
    if (s?.kind === 'depot' && g.depots.has(s.id)) return this.depotHtml(s.id);
    return this.overviewHtml();
  }

  private overviewHtml(): string {
    const g = this.game;
    const tot = g.map.totals;
    const R = g.router;
    const covRes = R ? R.coveredRes : 0;
    const lines = [...g.lines.values()];
    const depots = [...g.depots.values()];
    const hist = g.history[g.history.length - 1];
    const avgWait = g.today.boardings ? g.today.waitSum / g.today.boardings : NaN;
    return `
      <h2>${escapeHtml(g.map.area.name.split(',')[0])}</h2>
      <div class="kv">
        <span>Einwohner im Gebiet</span><span>${fmtNum(tot.residents)}</span>
        <span>Arbeitsplätze</span><span>${fmtNum(tot.jobs)}</span>
        <span>Erschlossen (≤ 500 m)</span><span>${tot.residents ? Math.round((covRes / tot.residents) * 100) : 0} %</span>
        <span>Einstiege heute</span><span>${fmtNum(g.today.boardings)}</span>
        <span>Ø Wartezeit</span><span>${fmtDuration(avgWait)}</span>
        <span>Aufgegeben heute</span><span>${fmtNum(g.today.lost)}</span>
        ${hist ? `<span>Gestern: Fahrgäste</span><span>${fmtNum(hist.trips)}</span>
        <span>Gestern: Ergebnis</span><span class="${hist.revenue - hist.costs < 0 ? 'neg' : 'pos'}">${fmtMoney(hist.revenue - hist.costs)}</span>` : ''}
      </div>
      ${!lines.length ? `
        <h3>Erste Schritte</h3>
        <ol class="steps">
          <li>Baue einen <b>Betriebshof</b> (B)</li>
          <li>Zeichne eine <b>Linie</b> (L) – klicke dafür nacheinander auf Straßen</li>
          <li>Passe im Linien-Panel unter <b>Fahrplan</b> Betriebszeit und Takt an</li>
          <li>Prüfe mit <b>Einzugsgebiet</b> (C), welche Häuser erschlossen sind</li>
        </ol>` : ''}
      ${this.warningsHtml()}
      <h3>Linien</h3>
      ${lines.map((l) => `
        <div class="row" data-act="select-line" data-id="${l.id}">
          <span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span>
          ${escapeHtml(g.stops.get(l.stopIds[0])?.name ?? '')} – ${escapeHtml(g.stops.get(l.stopIds[l.circular ? 0 : l.stopIds.length - 1])?.name ?? '')}
          <span class="meta">${g.vehiclesOf(l).length} 🚌 · ${fmtNum(l.passengersToday)}</span>
        </div>`).join('') || '<div class="hint">Noch keine Linien.</div>'}
      <div class="actions"><button class="btn primary" data-act="new-line">+ Neue Linie</button></div>
      <h3>Betriebshöfe</h3>
      ${depots.map((d) => {
        const n = [...g.vehicles.values()].filter((v) => v.depotId === d.id).length;
        return `<div class="row" data-act="select-depot" data-id="${d.id}">▣ ${escapeHtml(d.name)}<span class="meta">${n} Fahrzeuge</span></div>`;
      }).join('') || '<div class="hint">Noch kein Betriebshof.</div>'}`;
  }

  private warningsHtml(): string {
    const g = this.game;
    const items: string[] = [];
    for (const l of g.lines.values()) {
      for (const w of this.lineWarnings(l)) {
        items.push(`<div class="row warn-row" data-act="select-line" data-id="${l.id}">
          <span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span>${w}</div>`);
      }
    }
    for (const d of g.depots.values()) {
      const missing = g.driverDemand(d.id) - g.driversOf(d.id).length;
      if (missing > 0) {
        items.push(`<div class="row warn-row" data-act="select-depot" data-id="${d.id}">▣ ${escapeHtml(d.name)}: ${missing} Fahrer fehlen</div>`);
      }
    }
    return items.length ? `<h3>Hinweise</h3>${items.join('')}` : '';
  }

  private draftHtml(): string {
    const d = this.draft;
    const color = LINE_COLORS[this.game.lines.size % LINE_COLORS.length];
    let len = 0, time = 0;
    for (const p of d.paths) {
      for (let i = 0; i + 1 < p.xs.length; i++) len += Math.hypot(p.xs[i + 1] - p.xs[i], p.ys[i + 1] - p.ys[i]);
      time += p.time;
    }
    return `
      <h2><span class="badge" style="background:${color}">neu</span> Linie zeichnen</h2>
      <div class="hint">Klicke nacheinander auf Haltestellen oder Straßen. Neue Haltestellen werden automatisch gesetzt.</div>
      <div class="kv" style="margin-top:10px">
        <span>Haltestellen</span><span>${d.stops.length}</span>
        <span>Länge (eine Richtung)</span><span>${fmtDist(len)}</span>
        <span>Fahrzeit (ohne Halte)</span><span>${fmtDuration(time / 0.9)}</span>
      </div>
      <div class="stoplist" style="margin-top:8px">
        ${d.stops.map((s) => `<div class="row">${escapeHtml(s.name)}</div>`).join('')}
      </div>
      <label class="toggle"><input type="checkbox" data-field="draft-circular" ${d.circular ? 'checked' : ''}> Ringlinie</label>
      <div class="actions">
        <button class="btn primary" data-act="finish-line" ${d.stops.length < 2 ? 'disabled' : ''}>✓ Fertig (Enter)</button>
        <button class="btn" data-act="cancel-line">Abbrechen</button>
      </div>`;
  }

  private stopHtml(id: number): string {
    const g = this.game;
    const s = g.stops.get(id)!;
    const lines = g.linesAtStop(id);
    return `
      <h2>◉ Haltestelle <button class="close" data-act="close">×</button></h2>
      <input type="text" data-field="name" value="${escapeHtml(s.name)}">
      <div class="kv" style="margin-top:10px">
        <span>Wartende Fahrgäste</span><span>${s.waiting.length}</span>
        <span>Einstiege heute</span><span>${fmtNum(s.boardedToday)}</span>
      </div>
      <h3>Linien</h3>
      ${lines.map((l) => `<div class="row" data-act="select-line" data-id="${l.id}"><span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span>
        <span class="meta">alle ${fmtDuration(g.headway(l))}</span></div>`).join('') || '<div class="hint">Keine Linie hält hier.</div>'}
      <div class="actions"><button class="btn danger" data-act="delete">Abreißen</button></div>`;
  }

  private lineTab: 'overview' | 'timetable' | 'stops' = 'overview';

  /** Problems of a line the player should fix, as short texts. */
  private lineWarnings(l: TransitLine): string[] {
    const g = this.game;
    const w: string[] = [];
    if (!g.depots.size) w.push('Kein Betriebshof – baue einen, um Busse einzusetzen');
    const missing = g.peakVehicles(l) - g.vehiclesOf(l).length;
    if (missing > 0) w.push(`${missing} ${missing === 1 ? 'Bus fehlt' : 'Busse fehlen'} für den Fahrplan`);
    if (l.driverShortage) w.push('Fahrermangel – Busse bleiben im Betriebshof');
    return w;
  }

  private lineHtml(id: number): string {
    const g = this.game;
    const l = g.lines.get(id)!;
    const warnings = this.lineWarnings(l);
    const tab = (t: typeof this.lineTab, label: string) =>
      `<button data-act="line-tab" data-tab="${t}" class="${this.lineTab === t ? 'on' : ''}">${label}</button>`;
    const body = this.lineTab === 'timetable' ? this.lineTimetableHtml(l)
      : this.lineTab === 'stops' ? this.lineStopsHtml(l) : this.lineOverviewHtml(l);
    return `
      <h2><span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span> ${MODES[l.mode].name}linie
        <button class="close" data-act="close">×</button></h2>
      ${warnings.map((t) => `<div class="warn">⚠ ${t}</div>`).join('')}
      <div class="tabs">${tab('overview', 'Übersicht')}${tab('timetable', 'Fahrplan')}${tab('stops', 'Strecke')}</div>
      ${body}`;
  }

  private lineOverviewHtml(l: TransitLine): string {
    const g = this.game;
    const vs = g.vehiclesOf(l);
    const hw = g.headway(l);
    const len = l.runs.reduce((a, r) => a + r.path.length, 0);
    const cap = MODES[l.mode].vehicle.capacity;
    const capPerHour = isFinite(hw) ? (3600 / hw) * cap : 0;
    const pi = periodAt(l.timetable, g.time);
    return `
      <input type="text" data-field="name" value="${escapeHtml(l.name)}">
      <div class="swatches" style="margin-top:8px">
        ${LINE_COLORS.map((c) => `<button data-act="color" data-color="${c}" class="${c === l.color ? 'on' : ''}" style="background:${c}"></button>`).join('')}
      </div>
      <div class="kv" style="margin-top:12px">
        <span>Jetzt</span><span>${pi < 0 ? 'Betriebsruhe' : PERIODS[pi].name}</span>
        <span>Takt aktuell</span><span>${isFinite(hw) ? 'alle ' + fmtDuration(hw) : '–'}</span>
        <span>Busse im Einsatz</span><span>${g.activeVehicles(l)} / ${g.requiredVehicles(l)} benötigt</span>
        <span>Kapazität</span><span>${fmtNum(capPerHour)} Plätze/h</span>
        <span>Umlaufzeit</span><span>${fmtDuration(l.cycleTime)}</span>
        <span>Streckenlänge (Umlauf)</span><span>${fmtDist(len)}</span>
        <span>Einstiege heute</span><span>${fmtNum(l.passengersToday)}</span>
        <span>Einnahmen heute</span><span>${fmtMoney(l.revenueToday)}</span>
      </div>
      <h3>Fahrzeugpool</h3>
      <div class="stepper">
        <button class="btn" data-act="veh-remove" ${vs.length ? '' : 'disabled'}>−</button>
        <b>${vs.length}</b>
        <button class="btn" data-act="veh-add">+</button>
        <span class="hint">Spitzenbedarf: <b>${g.peakVehicles(l)}</b></span>
      </div>
      ${vs.map((v) => `<div class="row" data-act="select-vehicle" data-id="${v.id}">🚌 ${escapeHtml(v.name)}
        <span class="meta">${v.state === 'depot' ? 'Reserve' : `${v.passengers.length}/${cap} · ${this.vehicleStatus(v)}`}</span></div>`).join('')}
      <div class="actions"><button class="btn danger" data-act="delete">Linie löschen</button></div>`;
  }

  private lineTimetableHtml(l: TransitLine): string {
    const g = this.game;
    const tt = l.timetable;
    const cur = periodAt(tt, g.time);
    const startOpts = [4, 4.5, 5, 5.5, 6, 6.5, 7, 8];
    const endOpts = [19, 20, 21, 22, 23, 24, 24.5, 25, 26];
    const hwSelect = (i: number) => `
      <select data-field="tt-hw" data-period="${i}">
        <option value="0" ${tt.headways[i] === 0 ? 'selected' : ''}>kein Betrieb</option>
        ${HEADWAY_OPTIONS.map((s) => `<option value="${s}" ${tt.headways[i] === s ? 'selected' : ''}>alle ${s % 60 ? (s / 60).toFixed(1).replace('.', ',') : s / 60} min</option>`).join('')}
      </select>`;
    const missing = g.peakVehicles(l) - g.vehiclesOf(l).length;
    return `
      <div class="tt-hours">
        Betrieb von
        <select data-field="tt-start">${startOpts.map((h) => `<option value="${h}" ${h === tt.start ? 'selected' : ''}>${fmtHour(h)}</option>`).join('')}</select>
        bis
        <select data-field="tt-end">${endOpts.map((h) => `<option value="${h}" ${h === tt.end ? 'selected' : ''}>${fmtHour(h)}${h >= 24 ? ' Uhr (+1)' : ''}</option>`).join('')}</select>
      </div>
      <table class="tt">
        <tr><th>Zeitraum</th><th>Takt</th><th>Busse</th></tr>
        ${PERIODS.map((p, i) => {
          const b = periodBounds(tt, i);
          if (!b) return '';
          return `<tr class="${i === cur ? 'cur' : ''}">
            <td>${p.name}<small>${fmtHour(b[0])}–${fmtHour(b[1])}</small></td>
            <td>${hwSelect(i)}</td>
            <td class="num">${tt.headways[i] ? vehiclesFor(l.cycleTime, tt.headways[i]) : '–'}</td>
          </tr>`;
        }).join('')}
      </table>
      <div class="kv" style="margin-top:10px">
        <span>Umlaufzeit</span><span>${fmtDuration(l.cycleTime)}</span>
        <span>Spitzenbedarf</span><span>${g.peakVehicles(l)} Busse</span>
        <span>Im Pool</span><span>${g.vehiclesOf(l).length} Busse</span>
        <span>Fahrzeugstunden/Tag</span><span>${fmtNum(vehicleHours(tt, l.cycleTime))} h</span>
      </div>
      <div class="actions">
        ${missing > 0 ? `<button class="btn primary" data-act="assign-missing">${missing === 1 ? 'Fehlenden Bus' : `${missing} fehlende Busse`} zuweisen</button>` : ''}
        <button class="btn" data-act="hire-missing">Fehlende Fahrer einstellen</button>
      </div>
      <div class="hint" style="margin-top:8px">Die Disposition schickt nur so viele Busse aus dem Pool los, wie der Takt braucht.
        Überzählige Busse fahren an der Endhaltestelle zurück in den Betriebshof.</div>`;
  }

  private lineStopsHtml(l: TransitLine): string {
    const g = this.game;
    return `
      <label class="toggle"><input type="checkbox" data-field="circular" ${l.circular ? 'checked' : ''}> Ringlinie</label>
      <div class="actions" style="margin-top:4px"><button class="btn" data-act="edit-line" data-id="${l.id}">✎ Strecke bearbeiten</button></div>
      <div class="stoplist" style="margin-top:8px">
        ${l.stopIds.map((sid) => {
          const s = g.stops.get(sid);
          return s ? `<div class="row" data-act="select-stop" data-id="${sid}">${escapeHtml(s.name)}<span class="meta">${s.waiting.length} wartend</span></div>` : '';
        }).join('')}
      </div>`;
  }

  private vehicleStatus(v: Vehicle): string {
    if (v.state === 'deadhead') {
      return v.deadheadTo === 'depot' ? 'Fährt zum Betriebshof' : v.deadheadTo === 'rejoin' ? 'Umleitung' : 'Fährt zum Linienstart';
    }
    return VEHICLE_STATE[v.state];
  }

  private editHtml(id: number): string {
    const g = this.game;
    const l = g.lines.get(id)!;
    const n = l.stopIds.length;
    const len = l.runs.reduce((a, r) => a + r.path.length, 0);
    return `
      <h2><span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span> Strecke bearbeiten</h2>
      <div class="hint">Klick auf eine <b>Straße oder Haltestelle</b> fügt sie an der passenden Stelle ein.
        Klick auf eine <b>Haltestelle der Linie</b> nimmt sie heraus. Busse fahren ohne Unterbrechung weiter.</div>
      <div class="kv" style="margin-top:10px">
        <span>Haltestellen</span><span>${n}</span>
        <span>Streckenlänge (Umlauf)</span><span>${fmtDist(len)}</span>
        <span>Umlaufzeit</span><span>${fmtDuration(l.cycleTime)}</span>
      </div>
      <label class="toggle" style="margin-top:6px"><input type="checkbox" data-field="circular" ${l.circular ? 'checked' : ''}> Ringlinie</label>
      <div class="stoplist" style="margin-top:6px">
        ${l.stopIds.map((sid, i) => `
          <div class="row edit-row">${escapeHtml(g.stops.get(sid)?.name ?? '?')}
            <span class="meta">
              <button class="icon-btn" data-act="edit-up" data-idx="${i}" ${i === 0 ? 'disabled' : ''} title="Nach oben">↑</button>
              <button class="icon-btn" data-act="edit-down" data-idx="${i}" ${i === n - 1 ? 'disabled' : ''} title="Nach unten">↓</button>
              <button class="icon-btn" data-act="edit-remove" data-idx="${i}" ${n <= 2 ? 'disabled' : ''} title="Aus Linie entfernen">×</button>
            </span>
          </div>`).join('')}
      </div>
      <div class="actions"><button class="btn primary" data-act="edit-done">✓ Fertig (Enter)</button></div>`;
  }

  private vehicleHtml(id: number): string {
    const g = this.game;
    const v = g.vehicles.get(id)!;
    const def = MODES[v.mode].vehicle;
    const line = v.lineId !== null ? g.lines.get(v.lineId) : undefined;
    let status = this.vehicleStatus(v);
    if (v.state === 'depot' && line) status = 'Reserve im Betriebshof';
    const drv = v.driverId !== null ? g.drivers.get(v.driverId) : undefined;
    if (line && (v.state === 'moving' || v.state === 'dwell')) {
      const next = g.stops.get(line.runs[v.runIdx].stopIds[v.stopIdx]);
      status += next ? ` – ${v.state === 'moving' ? 'nächster Halt' : ''} ${escapeHtml(next.name)}` : '';
    }
    const load = v.passengers.length / def.capacity;
    return `
      <h2>🚌 Fahrzeug <button class="close" data-act="close">×</button></h2>
      <input type="text" data-field="name" value="${escapeHtml(v.name)}">
      <div class="kv" style="margin-top:10px">
        <span>Typ</span><span>${def.name}</span>
        <span>Status</span><span style="font-weight:500">${status}</span>
        <span>Fahrer</span><span style="font-weight:500">${drv ? `${escapeHtml(drv.name)} (${fmtShift(drv.worked)})` : '–'}</span>
        <span>Geschwindigkeit</span><span>${Math.round(v.v * 3.6)} km/h</span>
        <span>Fahrgäste</span><span>${v.passengers.length} / ${def.capacity}</span>
        <span>Laufleistung</span><span>${fmtDist(v.kmTotal * 1000)}</span>
        <span>Betriebshof</span><span>${escapeHtml(g.depots.get(v.depotId)?.name ?? '–')}</span>
      </div>
      <div class="bar" style="margin-top:8px"><div style="width:${Math.min(load, 1) * 100}%"></div></div>
      <h3>Linie</h3>
      <select data-field="assign">
        <option value="">– keine (im Betriebshof) –</option>
        ${[...g.lines.values()].filter((l) => l.mode === v.mode).map((l) => `<option value="${l.id}" ${l.id === v.lineId ? 'selected' : ''}>Linie ${escapeHtml(l.name)}</option>`).join('')}
      </select>
      <div class="actions">
        ${v.lineId !== null ? `<button class="btn" data-act="to-depot" data-id="${v.id}">Zum Betriebshof</button>` : ''}
        ${v.state === 'depot' ? `<button class="btn danger" data-act="sell" data-id="${v.id}">Verkaufen (${fmtMoney(def.price * 0.5)})</button>` : ''}
      </div>`;
  }

  private depotHtml(id: number): string {
    const g = this.game;
    const d = g.depots.get(id)!;
    const def = MODES[d.mode].vehicle;
    const vs = [...g.vehicles.values()].filter((v) => v.depotId === id);
    return `
      <h2>▣ Betriebshof <button class="close" data-act="close">×</button></h2>
      <input type="text" data-field="name" value="${escapeHtml(d.name)}">
      <div class="kv" style="margin-top:10px">
        <span>Fahrzeuge</span><span>${vs.length}</span>
        <span>Davon im Hof</span><span>${vs.filter((v) => v.state === 'depot').length}</span>
      </div>
      <div class="actions">
        <button class="btn primary" data-act="buy" data-id="${id}" ${g.canAfford(def.price) ? '' : 'disabled'}>+ ${def.name} kaufen (${fmtMoney(def.price)})</button>
      </div>
      ${this.staffHtml(id)}
      <h3>Fahrzeuge</h3>
      ${vs.map((v) => {
        const l = v.lineId !== null ? g.lines.get(v.lineId) : undefined;
        return `<div class="row" data-act="select-vehicle" data-id="${v.id}">🚌 ${escapeHtml(v.name)}
          <span class="meta">${l ? `<span class="badge" style="background:${l.color}">${escapeHtml(l.name)}</span>` : 'frei'}</span></div>`;
      }).join('') || '<div class="hint">Keine Fahrzeuge. Kaufe einen Bus oder nutze „+“ im Linien-Panel.</div>'}
      <div class="actions"><button class="btn danger" data-act="delete">Abreißen</button></div>`;
  }

  private staffHtml(depotId: number): string {
    const g = this.game;
    const ds = g.driversOf(depotId);
    const onDuty = ds.filter((d) => d.vehicleId !== null);
    const done = ds.filter((d) => d.vehicleId === null && d.worked >= SHIFT_MAX - 45 * 60);
    const free = ds.length - onDuty.length - done.length;
    const demand = g.driverDemand(depotId);
    const missing = demand - ds.length;
    const status = (d: Driver) => {
      if (d.vehicleId !== null) return `im Dienst · ${escapeHtml(g.vehicles.get(d.vehicleId)?.name ?? '')}`;
      if (d.worked >= SHIFT_MAX - 45 * 60) return 'Feierabend';
      return d.worked > 0 ? 'Pause' : 'verfügbar';
    };
    const shown = [...ds].sort((a, b) => (b.vehicleId !== null ? 1 : 0) - (a.vehicleId !== null ? 1 : 0)).slice(0, 25);
    return `
      <h3>Personal</h3>
      <div class="kv">
        <span>Fahrer</span><span>${ds.length}</span>
        <span>Im Dienst / verfügbar / Feierabend</span><span>${onDuty.length} / ${free} / ${done.length}</span>
        <span>Bedarf (Schätzung)</span><span class="${missing > 0 ? 'neg' : ''}">${demand}</span>
        <span>Lohnkosten</span><span>${fmtMoney(ds.length * DRIVER_WAGE_DAY)}/Tag</span>
      </div>
      <div class="actions">
        <button class="btn" data-act="hire" data-id="${depotId}" data-n="1">+1 Fahrer</button>
        <button class="btn" data-act="hire" data-id="${depotId}" data-n="5">+5</button>
        ${missing > 0 ? `<button class="btn primary" data-act="hire-missing" data-id="${depotId}">${missing} fehlende einstellen</button>` : ''}
        <button class="btn" data-act="fire" data-id="${depotId}" ${ds.length ? '' : 'disabled'}>−1 entlassen</button>
      </div>
      <div class="hint" style="margin-top:4px">Einstellung ${fmtMoney(DRIVER_HIRE_COST)} · Schicht max. 8 h · Ablösung an der Endhaltestelle</div>
      ${shown.map((d) => `<div class="row small">👤 ${escapeHtml(d.name)}<span class="meta">${status(d)} · ${fmtShift(d.worked)}</span></div>`).join('')}
      ${ds.length > shown.length ? `<div class="hint">… und ${ds.length - shown.length} weitere</div>` : ''}`;
  }
}
