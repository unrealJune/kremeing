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
