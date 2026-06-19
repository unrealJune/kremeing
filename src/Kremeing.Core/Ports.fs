namespace Kremeing.Core

open System
open Kremeing.Contracts.Domain

/// Inbound dependencies expressed as function types. The Api project
/// supplies concrete implementations (mocks for v0, scrapers + Postgres
/// later). Tests substitute stubs for full control over outcomes.
module Ports =

    type GetStore =
        StoreId -> Async<Result<Store, StoreError>>

    type GetHotLightStatus =
        StoreId -> Async<Result<HotLightObservation, StoreError>>

    /// Append-with-flip-suppression. Each call must update
    /// "last polled at" for the store regardless of outcome —
    /// that's how callers detect poller staleness even when nothing
    /// flipped.
    type RecordObservation =
        HotLightObservation -> Async<Result<RecordOutcome, StoreError>>

    /// Query the flip log for a store within a half-open time range
    /// [since, until). Empty list means the range had no flips, which
    /// is *not* the same as "no data" — combine with GetStoreStatus
    /// for staleness detection.
    type GetHistory =
        StoreId * DateTimeOffset * DateTimeOffset
            -> Async<Result<HotLightObservation list, StoreError>>

    type GetStoreStatus =
        StoreId -> Async<Result<StoreStatus, StoreError>>

    // ──── push notifications ───────────────────────────────────────────

    /// Idempotent subscribe: same (storeId, endpoint) returns the same
    /// PushSubscriptionId; the row's keys are refreshed from the
    /// incoming payload so a browser key-rotation doesn't strand us.
    type SubscribePush =
        StoreId * PushSubscription -> Async<Result<PushSubscriptionId, StoreError>>

    /// User-initiated unsubscribe: drops the row matching
    /// (storeId, endpoint). Idempotent; missing rows succeed.
    type UnsubscribePush =
        StoreId * string -> Async<Result<unit, StoreError>>

    /// Used by the poller-side dispatcher when an On-flip lands —
    /// returns every active subscription for that store, oldest first.
    type FindPushSubscriptionsForStore =
        StoreId -> Async<Result<StoredPushSubscription list, StoreError>>

    /// Used by the dispatcher when a delivery returns 410 Gone (the
    /// subscription is dead — user uninstalled the PWA, cleared site
    /// data, etc.). Removes every row with that endpoint across all
    /// stores; returns the count for logging.
    type DeletePushSubscriptionsByEndpoint =
        string -> Async<Result<int, StoreError>>

    /// Read-side: which stores has *this* browser (identified by its
    /// push endpoint) subscribed to? Used by the web client on app
    /// load to restore the bell's per-store state instead of always
    /// starting in 'idle'.
    type FindSubscribedStoresByEndpoint =
        string -> Async<Result<StoreId list, StoreError>>

    // ──── native device push (FCM) ───────────────────────────────────────

    /// Idempotent subscribe for a native device. Same `Token` refreshes
    /// the stored location/radius rather than creating a duplicate row,
    /// so a device that moved (or widened its radius) just re-registers.
    type SubscribeDevicePush =
        DevicePushRegistration -> Async<Result<DevicePushSubscriptionId, StoreError>>

    /// User-initiated (or token-rotation) unsubscribe: drops the row
    /// matching the FCM token. Idempotent; missing rows succeed.
    type UnsubscribeDevicePush =
        string -> Async<Result<unit, StoreError>>

    /// Used by the poller-side device dispatcher when an On-flip lands:
    /// returns every active device subscription so the fan-out can keep
    /// only those whose (location, radius) contains the flipped store.
    type GetAllDevicePushSubscriptions =
        unit -> Async<Result<StoredDevicePushSubscription list, StoreError>>
