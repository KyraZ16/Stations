import { buildMapData } from './map/mapdata';
import { loadOsm } from './map/overpass';
import { Game } from './sim/game';
import { App } from './ui/app';
import { showStart } from './ui/start';

const root = document.getElementById('app')!;

const start = showStart(root, async (choice) => {
  try {
    const osm = await loadOsm(choice.area, (m) => start.status(m));
    start.status('Baue Straßennetz und Stadtmodell …');
    await new Promise((r) => setTimeout(r, 30));
    const map = buildMapData(choice.area, osm);
    if (map.graph.segA.length === 0) throw new Error('In diesem Gebiet wurden keine Straßen gefunden.');
    const game = choice.save ? Game.restore(map, choice.save) : new Game(map, { sandbox: choice.sandbox });
    start.close();
    const app = new App(root, game);
    (window as unknown as { stations: unknown }).stations = { game, app, map };
  } catch (err) {
    console.error(err);
    start.status(err instanceof Error ? err.message : String(err), true);
  }
});
