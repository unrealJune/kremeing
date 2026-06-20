namespace Kremeing.Contracts

open System

module Domain =

    /// Identifier as Krispy Kreme exposes it (their `shopId` field).
    /// We adopt the upstream identifier so observations always join
    /// cleanly back to a real shop.
    type StoreId = StoreId of int

    type Coordinates = {
        Latitude: float
        Longitude: float
    }

    type HotLightStatus =
        | On
        | Off
        | Unknown

    type Store = {
        Id: StoreId
        Name: string
        Address: string
        Location: Coordinates
    }

    type HotLightObservation = {
        StoreId: StoreId
        Status: HotLightStatus
        ObservedAt: DateTimeOffset
    }

    type StoreError =
        | StoreNotFound of StoreId
        | UpstreamUnavailable of reason: string
        | InvalidStoreId of raw: string

    /// Outcome of recording one observation. `Flipped` carries the prior
    /// status so callers (alerting, logs) can describe what changed
    /// without a second read.
    type RecordOutcome =
        | FirstObservation
        | Unchanged
        | Flipped of previous: HotLightStatus

    /// Last-known state for a store. Distinct from `HotLightObservation`
    /// because the latter records *what was seen*; this records
    /// *what we believe right now* plus the temporal context clients
    /// need to render uptime UI ("on for 17 minutes", "tracked since…").
    type StoreStatus = {
        StoreId: StoreId
        CurrentStatus: HotLightStatus
        LastPolledAt: DateTimeOffset
        /// ObservedAt of the most recent flip. None if we've only ever
        /// recorded one observation (no flip has occurred yet).
        LastFlippedAt: DateTimeOffset option
        /// ObservedAt of the very first observation we recorded for this
        /// store — i.e., when our history begins.
        FirstObservedAt: DateTimeOffset
    }

    /// Cryptographic material the browser hands us when it subscribes
    /// to push. Endpoint is the push service URL (different per
    /// browser: FCM on Chrome, Mozilla autopush on Firefox, Apple
    /// web-push on Safari/iOS). p256dh + auth are the per-subscription
    /// keys we use to encrypt push payloads (RFC 8291).
    type PushSubscription = {
        Endpoint: string
        P256dh: string
        Auth: string
    }

    type PushSubscriptionId = PushSubscriptionId of int64

    type StoredPushSubscription = {
        Id: PushSubscriptionId
        StoreId: StoreId
        Subscription: PushSubscription
        CreatedAt: DateTimeOffset
    }

    /// Native (non-browser) push platforms we can target. The Android
    /// Auto companion app registers an FCM token here. Modeled as a DU
    /// so adding iOS/APNs later is a compile-time fan-out, not a string
    /// soup.
    type DevicePlatform =
        | Android

    /// A native device's request to be notified when *any* store near a
    /// point lights up. Unlike `PushSubscription` (web push, scoped to a
    /// single store), device subscriptions are location + radius based:
    /// the Android Auto user cares about "lit stores near me", not one
    /// specific shopId.
    type DevicePushRegistration = {
        /// FCM registration token identifying this device install. Unique
        /// per install; rotates when the app clears data or reinstalls.
        Token: string
        Platform: DevicePlatform
        /// Where the device is (or last reported). A hot-light flip within
        /// `RadiusMiles` of this point triggers a push to this token.
        Location: Coordinates
        RadiusMiles: float
    }

    type DevicePushSubscriptionId = DevicePushSubscriptionId of int64

    type StoredDevicePushSubscription = {
        Id: DevicePushSubscriptionId
        Registration: DevicePushRegistration
        CreatedAt: DateTimeOffset
    }
