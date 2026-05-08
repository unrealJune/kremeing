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

    module StatusValues =
        [<Literal>]
        let On = "on"
        [<Literal>]
        let Off = "off"
        [<Literal>]
        let Unknown = "unknown"
