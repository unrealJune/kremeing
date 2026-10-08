import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const Basemap = require('./basemap-utils.js');

const KEYED = 'https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=WEB';

test('buildTileConfig uses the server template, attribution and maxZoom', () => {
  const cfg = Basemap.buildTileConfig({
    tileUrlTemplate: KEYED,
    attribution: '© OpenStreetMap contributors © CARTO',
    maxZoom: 20,
  });
  assert.deepEqual(cfg, {
    url: KEYED,
    attribution: '© OpenStreetMap contributors © CARTO',
    maxZoom: 20,
  });
});

test('buildTileConfig keeps {r} and has no subdomain placeholder', () => {
  const { url } = Basemap.buildTileConfig({ tileUrlTemplate: KEYED });
  assert.ok(url.includes('{r}'));
  assert.ok(!url.includes('{s}'));
});

test('buildTileConfig falls back field by field', () => {
  assert.deepEqual(Basemap.buildTileConfig(null), Basemap.FALLBACK);
  assert.deepEqual(
    Basemap.buildTileConfig({ tileUrlTemplate: '', maxZoom: 'x' }),
    Basemap.FALLBACK);
});

test('fallback URL is keyless and never points at cartocdn', () => {
  assert.ok(!Basemap.FALLBACK.url.includes('key='));
  assert.ok(!Basemap.FALLBACK.url.includes('cartocdn'));
});

test('loadTileConfig requests the web client config', async () => {
  let requested;
  const cfg = await Basemap.loadTileConfig('http://api', async (url) => {
    requested = url;
    return { ok: true, json: async () => ({ tileUrlTemplate: KEYED, maxZoom: 20 }) };
  });
  assert.equal(requested, 'http://api/map-config?client=web');
  assert.equal(cfg.url, KEYED);
});

test('loadTileConfig resolves to the fallback on HTTP errors', async () => {
  const cfg = await Basemap.loadTileConfig('', async () => ({ ok: false, status: 500 }));
  assert.deepEqual(cfg, Basemap.FALLBACK);
});

test('loadTileConfig resolves to the fallback on timeout', async () => {
  const hang = (_url, { signal }) => new Promise((_resolve, reject) => {
    signal.addEventListener('abort', () => reject(new Error('aborted')));
  });
  const cfg = await Basemap.loadTileConfig('', hang, 10);
  assert.deepEqual(cfg, Basemap.FALLBACK);
});
