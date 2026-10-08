// Basemap tile config. The tile URL (and its per-client key) comes from the
// backend's /map-config so it can change without touching this file; if that
// fetch fails we fall back to the keyless URL, whose tiles 403 visibly while
// the map shell and pins keep working.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.KREMEING_BASEMAP = factory();
  }
}(typeof self !== 'undefined' ? self : this, function () {
  const FALLBACK_TILE_URL =
    'https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png';
  const FALLBACK_ATTRIBUTION = '&copy; OpenStreetMap contributors &copy; CARTO';
  const FALLBACK_MAX_ZOOM = 20;
  const FETCH_TIMEOUT_MS = 3000;

  const FALLBACK = Object.freeze({
    url: FALLBACK_TILE_URL,
    attribution: FALLBACK_ATTRIBUTION,
    maxZoom: FALLBACK_MAX_ZOOM,
  });

  // Turns a /map-config response body into Leaflet tileLayer arguments.
  // Anything missing or malformed falls back field by field.
  function buildTileConfig(config) {
    const url = typeof config?.tileUrlTemplate === 'string' && config.tileUrlTemplate
      ? config.tileUrlTemplate
      : FALLBACK_TILE_URL;
    const attribution = typeof config?.attribution === 'string' && config.attribution
      ? config.attribution
      : FALLBACK_ATTRIBUTION;
    const maxZoom = Number.isInteger(config?.maxZoom) && config.maxZoom > 0
      ? config.maxZoom
      : FALLBACK_MAX_ZOOM;
    return { url, attribution, maxZoom };
  }

  // Fetches /map-config?client=web, resolving to tileLayer arguments. Never
  // rejects: network errors, non-2xx and timeouts all resolve to FALLBACK.
  async function loadTileConfig(apiBase, fetchImpl, timeoutMs = FETCH_TIMEOUT_MS) {
    const doFetch = fetchImpl || fetch;
    const controller = typeof AbortController === 'function' ? new AbortController() : null;
    const timer = setTimeout(() => controller?.abort(), timeoutMs);
    try {
      const res = await doFetch(`${apiBase || ''}/map-config?client=web`, {
        signal: controller?.signal,
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return buildTileConfig(await res.json());
    } catch (err) {
      if (typeof console !== 'undefined') {
        console.warn('[kremeing] /map-config failed; using keyless basemap:', err);
      }
      return FALLBACK;
    } finally {
      clearTimeout(timer);
    }
  }

  return { FALLBACK, buildTileConfig, loadTileConfig };
}));
