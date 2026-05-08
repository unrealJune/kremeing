// Data layer — shapes match the OpenAPI contract in src/Kremeing.Api/openapi.yaml.
// `window.MOCK_STORES` and `window.mockUptime` are used as fallbacks ONLY when
// the page is opened with `?mock=1`. Otherwise an API failure surfaces as an
// error so users don't see fake NYC stores while the API is down.

// API_BASE resolution:
//   1. Explicit override via `window.KREMEING_API_BASE` (cross-origin dev).
//   2. Same-origin (empty string) when the page is served from a real
//      domain — e.g. kremeing.junephilip.com serves this page AND the API.
//   3. Fall back to the dev port when the page is on localhost (typical
//      `python -m http.server` workflow against a separately-running API).
const API_BASE =
  window.KREMEING_API_BASE
  ?? ((window.location.hostname === 'localhost'
       || window.location.hostname === '127.0.0.1')
      ? 'http://localhost:5234'
      : '');

const USE_MOCKS = new URLSearchParams(window.location.search).has('mock');

// NearbyStore: { id, name, address, latitude, longitude, distanceMiles,
//                currentStatus: 'on'|'off'|'unknown',
//                lastFlippedAt: ISO|null, firstObservedAt: ISO|null }
async function fetchNearbyStores(lat, lng, limit = 12) {
  const url = `${API_BASE}/stores/nearby?lat=${lat}&lng=${lng}&limit=${limit}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const body = await res.json();
  return body.stores;
}

// UptimeBucket: { startUtc, endUtc, onSeconds, offSeconds,
//                 observedSeconds, totalSeconds, fractionOn }
async function fetchUptime(storeId, bucket /* 'hour' | 'day' */) {
  const url = `${API_BASE}/stores/${storeId}/uptime?bucket=${bucket}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const body = await res.json();
  return body.buckets;
}

// ── relative time formatter for `lastFlippedAt` ─────────────────────────
function relativeTime(iso) {
  if (!iso) return null;
  const then = new Date(iso).getTime();
  if (isNaN(then)) return null;
  const sec = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (sec < 60) return `${sec}s ago`;
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} min ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr} hr ago`;
  const days = Math.round(hr / 24);
  return `${days}d ago`;
}

function shortName(name) {
  // Strip leading "Krispy Kreme — " or "Krispy Kreme " or "Krispy Kreme - " variants
  return name
    .replace(/^Krispy Kreme\s*[-—–]\s*/i, '')
    .replace(/^Krispy Kreme\s+/i, '');
}

function isHot(store) {
  return store.currentStatus === 'on';
}

// ── mock data (NearbyStore shape) ───────────────────────────────────────
function mins(n) {
  return new Date(Date.now() - n * 60_000).toISOString();
}

const MOCK_STORES = [
  { id: 1, name: 'Krispy Kreme — Times Square', address: '265 W 42nd St, New York, NY',
    latitude: 40.7580, longitude: -73.9855, distanceMiles: 0.0,
    currentStatus: 'on', lastFlippedAt: mins(12), firstObservedAt: mins(60 * 24 * 90) },
  { id: 2, name: 'Krispy Kreme — Penn Station', address: '2 Penn Plaza, New York, NY',
    latitude: 40.7506, longitude: -73.9930, distanceMiles: 0.6,
    currentStatus: 'on', lastFlippedAt: mins(4), firstObservedAt: mins(60 * 24 * 90) },
  { id: 3, name: 'Krispy Kreme — Union Square', address: '14 Union Sq E, New York, NY',
    latitude: 40.7359, longitude: -73.9911, distanceMiles: 1.6,
    currentStatus: 'off', lastFlippedAt: mins(140), firstObservedAt: mins(60 * 24 * 90) },
  { id: 4, name: 'Krispy Kreme — Brooklyn Heights', address: '195 Montague St, Brooklyn, NY',
    latitude: 40.6961, longitude: -73.9933, distanceMiles: 4.5,
    currentStatus: 'off', lastFlippedAt: mins(310), firstObservedAt: mins(60 * 24 * 90) },
  { id: 5, name: 'Krispy Kreme — Columbus Circle', address: '10 Columbus Cir, New York, NY',
    latitude: 40.7681, longitude: -73.9819, distanceMiles: 0.7,
    currentStatus: 'on', lastFlippedAt: mins(23), firstObservedAt: mins(60 * 24 * 90) },
  { id: 6, name: 'Krispy Kreme — Chelsea Market', address: '75 9th Ave, New York, NY',
    latitude: 40.7421, longitude: -74.0061, distanceMiles: 1.2,
    currentStatus: 'unknown', lastFlippedAt: null, firstObservedAt: null },
];

// Mock uptime buckets matching the UptimeBucket schema. `seed` keeps each
// store's pattern stable across renders.
function mockUptime(seed, bucket) {
  const count = bucket === 'hour' ? 24 : 90;
  const stepSec = bucket === 'hour' ? 3600 : 86400;
  const now = Date.now();
  let s = (seed * 9301 + 49297) % 233280;
  const rand = () => { s = (s * 9301 + 49297) % 233280; return s / 233280; };
  const out = [];
  for (let i = 0; i < count; i++) {
    const startMs = now - (count - i) * stepSec * 1000;
    let frac;
    if (bucket === 'hour') {
      const h = new Date(startMs).getHours();
      const peak = (h >= 5 && h <= 10) || (h >= 17 && h <= 20);
      frac = peak
        ? Math.max(0, Math.min(1, 0.75 + (rand() - 0.5) * 0.4))
        : Math.max(0, Math.min(1, rand() * 0.12));
    } else {
      frac = Math.max(0, Math.min(1, 0.18 + rand() * 0.32));
    }
    out.push({
      startUtc: new Date(startMs).toISOString(),
      endUtc: new Date(startMs + stepSec * 1000).toISOString(),
      onSeconds: frac * stepSec,
      offSeconds: (1 - frac) * stepSec,
      observedSeconds: stepSec,
      totalSeconds: stepSec,
      fractionOn: frac,
    });
  }
  return out;
}

Object.assign(window, {
  KREMEING_API: { fetchNearbyStores, fetchUptime },
  KREMEING_USE_MOCKS: USE_MOCKS,
  MOCK_STORES,
  mockUptime,
  relativeTime,
  shortName,
  isHot,
});
