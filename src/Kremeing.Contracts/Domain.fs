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
