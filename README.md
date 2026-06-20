# kremeing

Hot-light tracking for every Krispy Kreme location in the US.

A small F# / ASP.NET Core service that polls Krispy Kreme's public store
endpoints every five minutes, records flip events (the moments a store's
hot light goes on or off), and exposes the data through a typed HTTP API
plus an OpenAPI 3.1 reference at `/docs`.

> **Independent project.** Not affiliated with, endorsed by, or sponsored
> by Krispy Kreme Doughnut Corporation. *Krispy Kreme*, *Hot Light*, and
> *Original Glazed* are trademarks of their respective owners. This
> project is not a Krispy Kreme product and reflects only what is
> publicly visible at `krispykreme.com` and `site.krispykreme.com`.

## What it does

```
                 ┌──────────────┐         ┌──────────────┐
   clients  ───► │ kremeing-api │ ──reads─┤              │
                 │  replicas: N │         │   Postgres   │
                 └──────────────┘         │              │
                                          │              │
                 ┌──────────────┐         │              │
                 │ kremeing-    │ ─writes►│              │
                 │  poller      │         │              │
                 │  replicas: 1 │         └──────────────┘
                 └──────────────┘
                       │  every 5 min:
                       ▼  one call per (city, state) → up to 12 stores per call
                  api.krispykreme.com
                  site.krispykreme.com
```

- **Discovery**: scrapes `site.krispykreme.com` on startup and then
  periodically (every 12 hours by default), resolving each (city, state)
  to upstream `shopId`s via `api.krispykreme.com/shops/search`. Yields
  ~344 US stores. A refresh that resolves **0 stores** (or otherwise
  fails) is rejected — the previous good registry is kept rather than
  silently clobbered to empty.
- **Flip-only storage**: only status *changes* land in `flip_events`. A
  separate `store_status` row per shop tracks `last_polled_at` so
  staleness stays observable even when nothing flipped.
- **Two roles, one binary**: `KREMEING_ROLE=api` for HTTP-only replicas,
  `KREMEING_ROLE=poller` for the singleton writer. Both share Postgres.

## API at a glance

| Endpoint | Purpose |
|---|---|
| `GET /health` | Liveness probe |
| `GET /docs` | Rendered OpenAPI 3.1 reference (Redoc) |
| `GET /openapi.yaml` | Raw spec |
| `GET /stores/nearby?lat=&lng=&limit=` | Up to 12 stores nearest a coordinate, enriched with cached temporal context |
| `GET /stores/{id}/hot-light` | Live status for one store |
| `GET /stores/{id}/history?since=&until=` | Flip events in a time range |
| `GET /stores/{id}/uptime?bucket=hour\|day&since=&until=` | Bucketed time-series for charting |
| `GET /vapid-public-key` | Web Push VAPID public key for browser subscriptions |
| `POST` / `DELETE /subscriptions` | Web Push (browser) per-store subscribe/unsubscribe |
| `POST` / `DELETE /device-subscriptions` | Native (Android/FCM) location+radius subscribe/unsubscribe — powers the Android Auto app |

`{id}` is Krispy Kreme's `shopId` (e.g. SODO Seattle = `899`).

## Quickstart

### Run locally without Postgres

```bash
dotnet run --project src/Kremeing.Api
# → http://localhost:5234
# Observations are kept in memory; history is lost on restart.
```

### Run locally with Postgres (recommended)

```bash
brew install postgresql@16
brew services start postgresql@16
createdb kremeing

KREMEING_DATABASE_URL="Host=localhost;Database=kremeing;Username=$USER" \
  dotnet run --project src/Kremeing.Api
```

Both URL form (`postgres://user:pass@host:port/db`) and Npgsql key=value
form work — Npgsql normalizes them.

### Try it

```bash
curl http://localhost:5234/health
curl http://localhost:5234/stores/899/hot-light
curl "http://localhost:5234/stores/nearby?lat=47.6&lng=-122.3&limit=3"
open  http://localhost:5234/docs       # rendered OpenAPI reference
```

## Tests

```bash
dotnet test                # everything: Core + Api + Postgres + Contract

# Postgres tests skip cleanly if no DB is reachable. To run them, either
# have a local Postgres up (peer auth on $USER) or set:
KREMEING_TEST_DATABASE_URL="Host=...;Database=kremeing_test;Username=...;Password=..." \
  dotnet test
```

131 tests across four backend projects:

| Project | Tests | What it verifies |
|---|---:|---|
| `Kremeing.Core.Tests` | 76 | Pure functions: validation, DTO mapping, uptime bucketing, haversine geo, device-registration validation |
| `Kremeing.Api.Tests` | 94 | LiveApi adapter, Discovery, in-memory observations, poller, web + device push notify/dispatch |
| `Kremeing.Postgres.Tests` | 33 | Real-DB observations + web/device push subscription stores (mirror in-memory; skip without a DB) |
| `Kremeing.Contract.Tests` | 72 | HTTP wire-level, including OpenAPI completeness |

The Android Auto app's pure-Kotlin logic has its own JUnit suite (44 tests):

```bash
cd android && ./gradlew :logic:test    # no Android SDK required
```

It covers lit-store filtering, card text/distance formatting, `geo:` navigation
URI building, FCM data-message parsing, flip detection, the JSON codec, and the
backend HTTP client (against an in-process server). See
[`android/README.md`](android/README.md).

## Project layout

```
kremeing/
├── src/
│   ├── Kremeing.Contracts/   wire DTOs + domain types (no deps)
│   ├── Kremeing.Core/        pure logic: validation, mapping, uptime math, port types
│   └── Kremeing.Api/         adapters + Giraffe handlers + composition root
│       ├── LiveApi.fs        api.krispykreme.com client
│       ├── Discovery.fs      site.krispykreme.com scraper + registry resolver
│       ├── Observations.fs   in-memory flip-only store
│       ├── Postgres.fs       Postgres-backed store (same surface)
│       ├── Poller.fs         BackgroundService that ticks every 5 min
│       ├── HttpHandlers.fs   Giraffe routes + DTOs
│       ├── DevicePushDispatch.fs  FCM HTTP v1 send-side (data-only messages)
│       ├── DevicePushNotify.fs    proximity fan-out on Off→On flips
│       └── openapi.yaml      hand-written OpenAPI 3.1 spec
├── android/                 Android Auto app — see android/README.md
│   ├── logic/               pure-Kotlin/JVM logic + JUnit tests (builds anywhere)
│   └── app/                 Android Auto shell: CarAppService, FCM service (opt-in build)
├── tests/
│   ├── Kremeing.Core.Tests/
│   ├── Kremeing.Api.Tests/
│   ├── Kremeing.Postgres.Tests/
│   ├── Kremeing.Contract.Tests/
│   └── fixtures/             real captured KK responses (~50 KB)
├── k8s/                      manifests + README — see k8s/README.md
├── .github/workflows/
│   ├── ci.yml                test + manifest validation + image build
│   └── release.yml           multi-arch GHCR publish on v*.*.* tags
└── Dockerfile                multi-stage SDK→aspnet, non-root, port 8080
```

## Configuration

| Env var | Required | Default | Notes |
|---|---|---|---|
| `KREMEING_DATABASE_URL` | recommended | — (in-memory) | URL or Npgsql DSN. Falls back to in-memory if unset. |
| `KREMEING_ROLE` | no | `all` | `api` (HTTP only), `poller` (HTTP + poller), `all` (both — local dev default) |
| `KREMEING_DISCOVERY_REFRESH_INTERVAL` | no | `12` | Hours between discovery refreshes (positive number; e.g. `6` or `0.5`). Only the poller/`all` roles refresh. |
| `KREMEING_VAPID_PUBLIC_KEY` | no | — | Web Push (browser) VAPID public key. Push endpoints return `503 push_disabled` until set (with the private key + Postgres). |
| `KREMEING_VAPID_PRIVATE_KEY` | no | — | Web Push VAPID private key. **Secret.** |
| `KREMEING_VAPID_SUBJECT` | no | `mailto:` placeholder | Web Push VAPID subject (`mailto:` or origin URL). |
| `KREMEING_FCM_PROJECT_ID` | no | — | Firebase project id. Enables native (Android) device push for the Android Auto app; `/device-subscriptions` returns `503 push_disabled` until set. |
| `KREMEING_FCM_ACCESS_TOKEN` | no | — | OAuth2 bearer for FCM HTTP v1. **Secret.** Mint from a Firebase service account (short-lived; refresh via a sidecar/cron). Without it, subscriptions are still stored but sends are skipped. |
| `KREMEING_TEST_DATABASE_URL` | no | localhost peer | Used only by `Kremeing.Postgres.Tests`. |
| `ASPNETCORE_URLS` | no | `http://localhost:5000` | Standard ASP.NET Core. |

## Deployment

See [`k8s/README.md`](k8s/README.md) for full apply order and a managed-Postgres
swap. TL;DR:

```bash
# Build and push the image (release pipeline does this on v*.*.* tags).
docker build -t ghcr.io/<owner>/kremeing:0.2.0 .
docker push ghcr.io/<owner>/kremeing:0.2.0

# Apply.
cp k8s/10-database-secret.example.yaml k8s/10-database-secret.yaml
# … edit CHANGEME values …
kubectl apply -f k8s/
```

## CI / release

- **`ci.yml`** runs on push-to-main and PRs: `dotnet test` against a real
  Postgres service, `kubectl --dry-run=client` on every manifest, and a
  `docker build` smoke test. Test results upload as artifacts.
- **`release.yml`** runs on `v*.*.*` tags: builds **multi-arch**
  (`linux/amd64` + `linux/arm64`), pushes to GHCR with three tags
  (full version, stripped semver, `latest`), and writes a markdown
  summary to the run page.

```bash
git tag v0.2.0 && git push origin v0.2.0   # → ghcr.io/<owner>/kremeing:0.2.0
```

## Architecture notes worth reading

- **Ports as F# function types.** `Ports.GetHotLightStatus`,
  `RecordObservation`, `GetHistory`, etc. are plain function types
  (`StoreId -> Async<Result<...>>`). Stubbing in a test is just a
  different lambda — no mocking framework. Adapters are values, not
  classes implementing interfaces.
- **Total mappings.** `Mapping.statusToWire`, `errorToStatusCode`,
  `errorToDto` are exhaustive matches. Adding a `StoreError` case is a
  compile error until you decide its HTTP shape — the type system
  enforces wire-contract completeness.
- **Flip-only with staleness sentinel.** `flip_events` only stores
  changes; `store_status.last_polled_at` advances on every successful
  poll regardless of outcome. Clients can detect "the poller is down"
  by comparing `last_polled_at` to wall-clock without keeping per-poll
  rows.
- **Discovery via two-phase scrape.** Walk root → state pages on
  `site.krispykreme.com`, then resolve each `(city, state)` to
  `shopId`s via `api.krispykreme.com/shops/search`. ~150 API calls,
  ~30 seconds, dedupes ~344 stores. Repeats on a timer (12h default) so
  the registry self-heals; an empty/failed sweep never replaces a
  good registry. `/health` reports the live store count and last refresh
  time so a stuck-at-zero registry is observable, not silently green.

See `k8s/README.md` for deployment-shape rationale.

## Responsible use

The poller calls `api.krispykreme.com` once per unique `(city, state)`
every 5 minutes — about 30 requests per minute sustained, well under
typical rate limits and similar to what a single web user does in a
browsing session. **Forks should keep this cadence or extend it, not
shorten it.** This project is intended to be a polite citizen of
infrastructure that is not designed for high-frequency third-party
polling.

## License

MIT — see [`LICENSE`](LICENSE).
