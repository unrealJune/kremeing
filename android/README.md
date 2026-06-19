# Kremeing Hot Light — Android Auto app

A companion Android app (with an **Android Auto** experience) for the Kremeing
hot-light tracker. It:

- **Watches nearby stores** — registers a location + radius push subscription
  with the backend's `POST /device-subscriptions` endpoint.
- **Notifies on flip-on** — the backend fans out an FCM *data* message whenever
  a store within your radius turns its hot light on; the app renders a
  high-priority notification.
- **Maps to it** — every notification and every Android Auto card has a
  **Navigate** action that hands the store's coordinates to your active
  navigation app (Google Maps) via a `geo:` intent.
- **Shows up in the car as cards** — the Car App Library renders a list of
  POI cards (store name, distance, address), nearest lit store first.

## Modules

| Module | Type | Built where |
|---|---|---|
| `:logic` | pure Kotlin / JVM | **anywhere** — plain JDK + Gradle, no Android SDK |
| `:app` | Android application | only with an Android SDK **and** Google Maven access |

All decision logic lives in `:logic` so it can be exhaustively unit-tested off
the device:

- `HotLightStatus` / `NearbyStore` — wire models + status parsing
- `Geo` — haversine distance (in parity with the backend `Geo` module)
- `LitStoreFilter` — "what appears on the car screen" (lit only, nearest first)
- `CardFormatter` — card title/subtitle and glanceable distance text
- `NavigationIntent` — the `geo:` URIs that trigger turn-by-turn
- `FcmMessage` — parses the backend's FCM data payload into typed content
- `FlipDetector` — newly-lit stores between two polls
- `ApiCodec` + `KremeingApiClient` — JSON contract + HTTP client for the backend

The `:app` module is a thin shell that binds these into Android components
(`KremeingCarAppService` / `HotLightScreen`, `KremeingMessagingService`,
`MainActivity`).

## Build & test

```bash
# Logic tests — no Android SDK needed (CI-friendly):
./gradlew :logic:test

# Full Android app build (requires Android SDK + Google Maven reachable):
./gradlew assembleDebug -PbuildAndroidApp=true \
  -PkremeingBaseUrl=https://your-kremeing-host
```

`:app` is **opt-in** (`-PbuildAndroidApp=true` or env
`KREMEING_BUILD_ANDROID_APP=1`) so a default/restricted build only configures
`:logic`. Building `:app` also needs an Android SDK (set `ANDROID_SDK_ROOT` or a
`local.properties` with `sdk.dir=...`).

### Backend base URL

The app reads `BuildConfig.KREMEING_BASE_URL`, overridable at build time with
`-PkremeingBaseUrl=...` (defaults to a placeholder).

### Firebase

Native push uses Firebase Cloud Messaging. The module compiles **without**
`google-services.json` (the `com.google.gms.google-services` plugin is not
applied), but a real device needs that file to initialize Firebase. Drop your
`google-services.json` into `app/` and apply the plugin before shipping.

## How the pieces line up with the backend

| App | Backend |
|---|---|
| `KremeingApiClient.nearbyStores` | `GET /stores/nearby` |
| `KremeingApiClient.subscribeDevice` | `POST /device-subscriptions` |
| `KremeingApiClient.unsubscribeDevice` | `DELETE /device-subscriptions` |
| `FcmMessage.parse` | `DevicePushDispatch.buildMessageJson` (FCM data keys) |

The FCM data contract (string-valued, per FCM rules) is:
`title`, `body`, `storeId`, `storeName`, `latitude`, `longitude`.
