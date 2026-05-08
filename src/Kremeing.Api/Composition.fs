namespace Kremeing.Api

open System
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Composition root. The only place where ports meet adapters; tests
/// build their own Deps struct using stubs instead of calling here.
module Composition =

    /// Bag of the three observations ports as plain functions. Both
    /// InMemoryObservations.Store and Postgres.Store expose methods
    /// that match these signatures — `Program` picks one and wraps
    /// it in this record before calling `build`.
    type ObservationsAdapter = {
        Record: Ports.RecordObservation
        History: Ports.GetHistory
        Status: Ports.GetStoreStatus
    }

    let inMemoryAdapter (s: InMemoryObservations.Store) : ObservationsAdapter = {
        Record = s.Record
        History = s.History
        Status = s.Status
    }

    let postgresAdapter (s: Postgres.Store) : ObservationsAdapter = {
        Record = s.Record
        History = s.History
        Status = s.Status
    }

    /// Production dependencies the host wires into Giraffe + Hosted services.
    type ProductionDeps = {
        Handlers: HttpHandlers.Deps
        /// Exposed so the poller writes through the same port the handlers
        /// read from — never spin up a second observations store.
        Record: Ports.RecordObservation
    }

    let build
            (send: LiveApi.SendHttp)
            (registry: Discovery.RegistryEntry list)
            (observations: ObservationsAdapter)
            : ProductionDeps =

        // Lookup cityStateZip query for a given StoreId by walking the
        // in-memory registry. O(n) per call is fine — n ≈ 400.
        let lookupQuery : StoreId -> Async<Result<string, StoreError>> =
            fun id ->
                async {
                    let (StoreId sid) = id
                    match registry |> List.tryFind (fun e -> e.ShopId = sid) with
                    | Some e -> return Ok e.SearchKey
                    | None -> return Error (StoreNotFound id)
                }

        let now () = DateTimeOffset.UtcNow

        let getHotLight =
            LiveApi.getHotLightStatus send lookupQuery now

        let searchNearby : HttpHandlers.SearchNearby =
            fun (lat, lng) -> LiveApi.searchByLocation send lat lng

        let handlers : HttpHandlers.Deps = {
            GetHotLightStatus = getHotLight
            SearchNearby = searchNearby
            History = observations.History
            Status = observations.Status
            Now = now
        }

        { Handlers = handlers; Record = observations.Record }
