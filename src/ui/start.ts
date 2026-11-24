import { Area } from '../core/geo';
import { escapeHtml } from '../core/util';
import { searchPlace } from '../map/overpass';
import { SaveData } from '../sim/game';

export const SAVE_KEY = 'stations-save-v1';

export interface StartChoice {
  area: Area;
  sandbox: boolean;
  save?: SaveData;
}

const PRESETS: Area[] = [
  { name: 'Hamburg Innenstadt', lat: 53.5530, lon: 9.9930, radius: 2500 },
  { name: 'Berlin Mitte', lat: 52.5200, lon: 13.4050, radius: 2500 },
  { name: 'München Zentrum', lat: 48.1390, lon: 11.5660, radius: 2500 },
  { name: 'Köln Innenstadt', lat: 50.9400, lon: 6.9550, radius: 2500 },
  { name: 'Leipzig Zentrum', lat: 51.3400, lon: 12.3740, radius: 2500 },
  { name: 'Freiburg', lat: 47.9960, lon: 7.8460, radius: 2500 },
];

const SIZES = [
  { r: 1500, label: 'Klein (3 × 3 km)' },
  { r: 2500, label: 'Mittel (5 × 5 km)' },
  { r: 4000, label: 'Groß (8 × 8 km, lädt länger)' },
];

export function loadSave(): SaveData | null {
  try {
    const raw = localStorage.getItem(SAVE_KEY);
    return raw ? (JSON.parse(raw) as SaveData) : null;
  } catch {
    return null;
  }
}

export function showStart(root: HTMLElement, onChoose: (c: StartChoice) => void): {
  status: (msg: string, error?: boolean) => void;
  close: () => void;
} {
  const el = document.createElement('div');
  el.id = 'start';
  root.appendChild(el);
  const save = loadSave();
  let chosen: Area = { ...PRESETS[0] };
  let radius = 2500;
  let sandbox = true;
  let results: { name: string; lat: number; lon: number }[] = [];
  let busy = false;

  const render = () => {
    el.innerHTML = `
    <div class="card">
      <h1>Stations<span>.</span></h1>
      <div class="sub">ÖPNV-Sandbox auf echten Straßen (OpenStreetMap)</div>
      <div class="tiers-info">
        <div><b>🚌 Lokal</b>Bus · Tram (bald)</div>
        <div><b>Ⓤ Stadtweit</b>U-Bahn · S-Bahn (geplant)</div>
        <div><b>🚆 Regional</b>Regionalzug (geplant)</div>
      </div>
      <label class="lbl">Stadt wählen</label>
      <div class="presets">
        ${PRESETS.map((p, i) => `<button class="btn ${p.name === chosen.name ? 'on' : ''}" data-preset="${i}">${p.name}</button>`).join('')}
      </div>
      <label class="lbl">… oder Ort suchen</label>
      <form class="search">
        <input type="text" name="q" placeholder="z. B. Dresden Neustadt, Wien, Zürich HB">
        <button class="btn" type="submit">Suchen</button>
      </form>
      <div class="results">
        ${results.map((r, i) => `<button class="btn ${r.name === chosen.name ? 'on' : ''}" data-result="${i}">${escapeHtml(r.name)}</button>`).join('')}
      </div>
      <div class="opts" style="margin-top:16px">
        <label>Kartengröße
          <select name="size">${SIZES.map((s) => `<option value="${s.r}" ${s.r === radius ? 'selected' : ''}>${s.label}</option>`).join('')}</select>
        </label>
        <label class="toggle"><input type="checkbox" name="sandbox" ${sandbox ? 'checked' : ''}> Sandbox (unbegrenztes Geld)</label>
      </div>
      <div class="go">
        <span class="chosen">📍 ${escapeHtml(chosen.name.split(',').slice(0, 2).join(','))}</span>
        ${save ? `<button class="btn" data-continue>Weiterspielen: ${escapeHtml(save.area.name.split(',')[0])}, Tag ${Math.floor(save.time / 86400) + 1}</button>` : ''}
        <button class="btn primary" data-start>Neues Spiel</button>
      </div>
      <div class="status"></div>
      <div class="credit">Kartendaten © OpenStreetMap-Mitwirkende (ODbL). Abgerufen über die Overpass API.</div>
    </div>`;
    el.querySelector<HTMLFormElement>('form.search')!.onsubmit = async (e) => {
      e.preventDefault();
      const q = (new FormData(e.target as HTMLFormElement).get('q') as string).trim();
      if (!q) return;
      setStatus('Suche …');
      try {
        results = await searchPlace(q);
        setStatus(results.length ? '' : 'Nichts gefunden');
        if (results.length) chosen = { ...results[0], radius };
        render();
      } catch (err) {
        setStatus(String(err), true);
      }
    };
    el.querySelector<HTMLSelectElement>('select[name=size]')!.onchange = (e) => {
      radius = parseInt((e.target as HTMLSelectElement).value, 10);
    };
    el.querySelector<HTMLInputElement>('input[name=sandbox]')!.onchange = (e) => {
      sandbox = (e.target as HTMLInputElement).checked;
    };
  };

  el.addEventListener('click', (e) => {
    const t = (e.target as HTMLElement).closest('button');
    if (!t || busy) return;
    if (t.dataset.preset) {
      chosen = { ...PRESETS[+t.dataset.preset] };
      render();
    } else if (t.dataset.result) {
      chosen = { ...results[+t.dataset.result], radius };
      render();
    } else if (t.hasAttribute('data-start')) {
      busy = true;
      onChoose({ area: { ...chosen, radius }, sandbox });
    } else if (t.hasAttribute('data-continue') && save) {
      busy = true;
      onChoose({ area: save.area, sandbox: save.sandbox, save });
    }
  });

  const setStatus = (msg: string, error = false) => {
    const s = el.querySelector<HTMLElement>('.status');
    if (!s) return;
    s.textContent = msg;
    s.classList.toggle('error', error);
    if (error) busy = false;
  };

  render();
  return { status: setStatus, close: () => el.remove() };
}
