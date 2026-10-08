# Spec: move Kremeing's maps to the self-hosted basemap server

Status: ready to implement · Owner: (assign) · Written 2026-10-07

## Why

Both map clients load CARTO raster tiles from
`https://{a-d}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png`.
**Those URLs no longer work.** CARTO restricted its hosted basemaps to
enterprise customers. Every tile at every zoom now comes back as the same
2 KB "API KEY REQUIRED" placeholder image (HTTP 200, so nothing errors). Users
see a grey grid with that text where the map should be, behind working store
pins.

A self-hosted, URL-compatible replacement is live in the homelab cluster
(`buttercup` repo, `apps/basemaps/`). It renders the same CARTO Positron
style from OpenStreetMap data.

## The tile server (contract)

```
https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=<KEY>
```

| Property | Value |
|---|---|
| Styles | `light_all` (Positron, what Kremeing uses), `dark_all`, `rastertiles/voyager` |
| `{r}` | empty → 256 px tile; `@2x` → 512 px retina tile |
| Zoom | 0–20 (Kremeing uses ≤ 19) |
| Subdomains | **none**. Use one host and drop `{s}` / `a–d` |
| Auth | `?key=` query param, **required**. Missing/unknown → `403` |
| Rate limits | per client IP: ~20 req/s sustained, 150 burst (×up to 3). Over → `429` |
| Caching | `200` responses carry `Cache-Control: public, max-age=604800`; errors carry `no-store` |
| CORS | `Access-Control-Allow-Origin: *` |
| Attribution | unchanged: `© OpenStreetMap contributors © CARTO` (styles are CARTO's, BSD-3; data is OSM, ODbL) |

Every other path returns `404`, and only `GET`/`HEAD` are accepted.

### Keys

There is one key per client, so one can be revoked without breaking the
others:

- `kremeing-web`
- `kremeing-android`

The keys live SOPS-encrypted in `buttercup/apps/basemaps/secret.yaml`. Get
them from the repo owner; **never commit a key to this repo**. They end up in
public clients anyway, so they are revocable identifiers, not secrets. Still,
deliver them through config (below), not source.

## Design: serve the tile config from the backend

Don't hard-code the URL or key into the clients. The Android APK is the
problem: anything compiled in needs an app release to change. The previous
CARTO URL is exactly how this outage got baked into shipped builds.

Add one small backend endpoint that tells each client which tile URL to use.
The clients fetch it at startup and fall back to a compiled-in default if it
fails.

### 1. Backend (`src/Kremeing.Api`)

New route, next to `/vapid-public-key` in `HttpHandlers.fs`:

```
GET /map-config?client=web|android
200 {
  "tileUrlTemplate": "https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=…",
  "attribution": "© OpenStreetMap contributors © CARTO",
  "maxZoom": 20
}
```

- Configuration comes from env vars, read in `Program.fs` the same way as the
  `KREMEING_VAPID_*` ones:
  - `KREMEING_BASEMAP_URL`: the template **without** the key. Default:
    `https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png`
  - `KREMEING_BASEMAP_KEY_WEB`, `KREMEING_BASEMAP_KEY_ANDROID`
- Append `?key=<key>` for the requested `client`. If `client` is missing or
  unknown, use the web key.
- If the key for that client isn't configured, still return `200` with the
  URL and no key. Log a startup warning like the VAPID "disabled" path does.
  The client will then get 403 tiles, which is visible and diagnosable. Don't
  fail startup.
- Response header: `Cache-Control: public, max-age=3600`.
- Add the DTO to `Kremeing.Contracts/Api.fs` and the path to `openapi.yaml`.
- Tests in `tests/Kremeing.Api.Tests`: web key, android key, unknown client
  falls back to web, and missing key returns the URL without `?key=`.

### 2. Web (`web/map.jsx`, `web/index.html`)

- Before `L.tileLayer(...)`, fetch `/map-config?client=web` (same origin).
- Use `tileUrlTemplate` as the Leaflet URL. Remove the `subdomains` option.
  Keep `{r}` so Leaflet requests `@2x` on retina screens. Take
  `attribution`/`maxZoom` from the response.
- **Fallback** if the fetch fails or times out (~3 s): use
  `https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png` with no key.
  It renders 403s, but the map shell and pins still work, and the failure is
  obvious.
- The map is initialized once in a `useEffect([])`. Either fetch before
  creating the layer and add it when the fetch resolves, or create the map
  immediately and `addTo(map)` the tile layer later. Don't block the stores
  list on the tile config.
- `web/sw.js` only handles push. Confirm it doesn't start caching tiles.
- Test: add a case to the existing `*.test.mjs` style if the URL-building
  logic is pulled into a plain function (recommended:
  `buildTileUrl(config)` in a non-JSX file).

### 3. Android (`android/app/.../map/MapActivity.kt`)

The current code is `XYTileSource("CartoLight", 0, 20, 256, ".png", arrayOf(4 × cartocdn))`.

- **osmdroid can't take a query string through `XYTileSource`.** It builds
  `baseUrl + z/x/y + ext`. Add a small `OnlineTileSourceBase` subclass that
  overrides `getTileURLString(pMapTileIndex)` and returns the template with
  `{z}`, `{x}` and `{y}` substituted (use `MapTileIndex.getZoom/X/Y`) and
  `{r}` → `""`.
  - Keep 256 px tiles, since `setTilesScaledToDpi(true)` is already on.
  - Use `@2x` only if you also set the tile size to 512. Not required.
- **Give the new source a new name**, e.g. `"KremeingBasemapLight"`.
  - osmdroid keys its on-disk tile cache by source name.
  - Reusing `"CartoLight"` would keep serving the cached "API KEY REQUIRED"
    tiles on devices that already loaded them.
  - Optionally purge the old cache once:
    `SqlTileWriter().purgeCache("CartoLight")`.
- Fetch `GET {KREMEING_BASE_URL}/map-config?client=android` through the
  existing `KremeingApiClient` (`android/logic`). Add a method and a test in
  `KremeingApiClientTest.kt` and a stub in `FakeKremeingApiClient.kt`.
- Cache the last good template in `SharedPreferences`.
- On startup, use the cached template immediately. Otherwise use the
  compiled default
  `BuildConfig.KREMEING_BASEMAP_URL + "?key=" + BuildConfig.KREMEING_BASEMAP_KEY`.
- Refresh from the API in the background. If the template changed, swap the
  tile source (`mapView.setTileSource(...)`).
- Add the compiled defaults as `buildConfigField`s in
  `android/app/build.gradle.kts`, using the same `project.findProperty(...)`
  pattern as `kremeingBaseUrl`: `-PkremeingBasemapKey=…`. Default to an empty
  key, **not a real key in source**.
- Keep `userAgentValue = packageName`.
- Attribution text stays as-is.
- Tests: extend `MapActivityTest.kt` to assert the source name and that the
  generated URL for a known tile index is
  `…/light_all/14/2624/5721.png?key=TEST`.

### 4. Deployment (`buttercup` repo, ops; not this repo)

- Add `KREMEING_BASEMAP_KEY_WEB` and `KREMEING_BASEMAP_KEY_ANDROID` to the
  SOPS-encrypted `apps/kremeing/secret.yaml`.
- Wire them into `apps/kremeing/api-deployment.yaml` as `secretKeyRef` env
  vars, like the VAPID keys. The poller doesn't need them.
- CI and release Android builds pass `-PkremeingBasemapKey=<kremeing-android key>`.

## Acceptance

Run from a machine on the internet (not the LAN):

1. Check the endpoint:
   `curl https://kremeing.junephilip.com/map-config?client=web` returns the
   template with the web key.
2. The web app shows the Positron basemap at zooms 4–19. DevTools shows tiles
   from `basemaps.junephilip.com` with `200`, and no requests to `cartocdn.com`.
3. A fresh Android install and an upgrade over an old build both show the
   basemap. On the upgraded install, no "API KEY REQUIRED" tiles survive from
   the old cache.
4. Killing network access to `/map-config` still loads tiles from the
   cached/default template.
5. `grep -r cartocdn` over `web/`, `android/` and `src/` finds nothing.
6. No tile key appears in committed source.

## Out of scope

- Dark mode or Voyager (the server supports `dark_all` and
  `rastertiles/voyager` if wanted later; it's a URL change only).
- Vector tiles or MapLibre. The server only exposes raster.
- Android Auto (`car/`) screens render without a basemap today. Leave them
  unchanged.
