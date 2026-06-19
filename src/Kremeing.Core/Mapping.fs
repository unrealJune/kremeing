namespace Kremeing.Core

open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api

/// Total functions between domain types and wire DTOs. No I/O, no
/// exceptions — pattern matches are exhaustive so adding a domain
/// case forces a compile-time decision about the wire representation.
module Mapping =

    let statusToWire (status: HotLightStatus) : string =
        match status with
        | On -> StatusValues.On
        | Off -> StatusValues.Off
        | Unknown -> StatusValues.Unknown

    /// Wire string for a native push platform. Exhaustive, so adding a
    /// platform forces a decision here and in the parser.
    let platformToWire (platform: DevicePlatform) : string =
        match platform with
        | Android -> "android"

    let storeIdValue (StoreId raw) = raw

    let storeToDto (store: Store) : StoreDto = {
        id = storeIdValue store.Id
        name = store.Name
        address = store.Address
        latitude = store.Location.Latitude
        longitude = store.Location.Longitude
    }

    let observationToDto (obs: HotLightObservation) : HotLightStatusDto = {
        storeId = storeIdValue obs.StoreId
        status = statusToWire obs.Status
        observedAt = obs.ObservedAt
    }

    let errorToDto (err: StoreError) : ErrorDto =
        match err with
        | StoreNotFound (StoreId id) ->
            { error = ErrorCodes.StoreNotFound
              message = sprintf "No store with id %d." id }
        | UpstreamUnavailable reason ->
            { error = ErrorCodes.UpstreamUnavailable
              message = reason }
        | InvalidStoreId raw ->
            { error = ErrorCodes.InvalidStoreId
              message = sprintf "Invalid store id: '%s'." raw }

    /// HTTP status code for a domain error. Single source of truth so
    /// that every code path returning the same error returns the same
    /// status — no scattered `if`-chains in handlers.
    let errorToStatusCode (err: StoreError) : int =
        match err with
        | StoreNotFound _ -> 404
        | UpstreamUnavailable _ -> 502
        | InvalidStoreId _ -> 400
