namespace Kremeing.Contracts

open System

/// Wire-level DTOs. Field names here are the API contract — renaming
/// breaks clients. Domain types live in Domain.fs and may evolve freely.
module Api =

    [<CLIMutable>]
    type StoreDto = {
        id: int
        name: string
        address: string
        latitude: float
        longitude: float
    }

    [<CLIMutable>]
    type HotLightStatusDto = {
        storeId: int
        status: string
        observedAt: DateTimeOffset
    }

    [<CLIMutable>]
    type ErrorDto = {
        error: string
        message: string
    }

    [<CLIMutable>]
    type NearbyQueryDto = {
        latitude: float
        longitude: float
        limit: int
    }

    [<CLIMutable>]
    type NearbyStoreDto = {
        id: int
        name: string
        address: string
        latitude: float
        longitude: float
        distanceMiles: float
        currentStatus: string
        /// Null if we've not yet seen a flip for this store (we may
        /// have only one observation, or none at all).
        lastFlippedAt: System.Nullable<DateTimeOffset>
        /// Null if the store has never been observed by us. Lets clients
        /// indicate "tracked since X" or "no history yet" appropriately.
        firstObservedAt: System.Nullable<DateTimeOffset>
    }

    [<CLIMutable>]
    type NearbyResponseDto = {
        query: NearbyQueryDto
        stores: NearbyStoreDto[]
    }

    [<CLIMutable>]
    type SearchQueryDto = {
        q: string
        limit: int
    }

    [<CLIMutable>]
    type SearchResponseDto = {
        query: SearchQueryDto
        stores: NearbyStoreDto[]
    }

    [<CLIMutable>]
    type HotLightFlipDto = {
        storeId: int
        /// "on" or "off" — the status the store flipped *to*.
        status: string
        observedAt: DateTimeOffset
    }

    [<CLIMutable>]
    type HotLightHistoryDto = {
        storeId: int
        rangeStart: DateTimeOffset
        rangeEnd: DateTimeOffset
        /// Oldest flip first. Includes the FirstObservation anchor row,
        /// so clients can reconstruct intervals by pairing entries.
        flips: HotLightFlipDto[]
    }

    [<CLIMutable>]
    type UptimeBucketDto = {
        startUtc: DateTimeOffset
        endUtc: DateTimeOffset
        onSeconds: float
        offSeconds: float
        observedSeconds: float
        totalSeconds: float
        fractionOn: float
    }

    [<CLIMutable>]
    type UptimeResponseDto = {
        storeId: int
        bucket: string                  // "hour" | "day"
        rangeStart: DateTimeOffset
        rangeEnd: DateTimeOffset
        buckets: UptimeBucketDto[]
    }

    // ──── push subscriptions ───────────────────────────────────────────

    [<CLIMutable>]
    type SubscriptionKeysDto = {
        p256dh: string
        auth: string
    }

    /// Mirrors the JSON shape of the browser's `PushSubscription.toJSON()`.
    /// We deliberately don't model `expirationTime` — it's optional and
    /// we don't act on it.
    [<CLIMutable>]
    type WebPushSubscriptionDto = {
        endpoint: string
        keys: SubscriptionKeysDto
    }

    [<CLIMutable>]
    type SubscribeRequestDto = {
        storeId: int
        subscription: WebPushSubscriptionDto
    }

    [<CLIMutable>]
    type SubscribeResponseDto = {
        id: int64
        storeId: int
    }

    [<CLIMutable>]
    type UnsubscribeRequestDto = {
        storeId: int
        endpoint: string
    }

    [<CLIMutable>]
    type VapidPublicKeyResponseDto = {
        publicKey: string
    }

    /// Response of `GET /subscriptions?endpoint=...`. Lets the web
    /// client tell, on page load, which stores this browser is already
    /// subscribed to — so the bell button shows the right state.
    [<CLIMutable>]
    type SubscribedStoresResponseDto = {
        endpoint: string
        storeIds: int[]
    }

    module BucketSizes =
        [<Literal>]
        let Hour = "hour"
        [<Literal>]
        let Day = "day"

    module ErrorCodes =
        [<Literal>]
        let StoreNotFound = "store_not_found"
        [<Literal>]
        let UpstreamUnavailable = "upstream_unavailable"
        [<Literal>]
        let InvalidStoreId = "invalid_store_id"
        [<Literal>]
        let RateLimited = "rate_limited"
        [<Literal>]
        let RangeTooWide = "range_too_wide"
        [<Literal>]
        let PushDisabled = "push_disabled"
        [<Literal>]
        let InvalidSubscription = "invalid_subscription"

    module StatusValues =
        [<Literal>]
        let On = "on"
        [<Literal>]
        let Off = "off"
        [<Literal>]
        let Unknown = "unknown"
