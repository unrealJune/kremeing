namespace Kremeing.Api

open System
open System.Collections.Concurrent
open Kremeing.Contracts.Domain
open Kremeing.Core

module Adapters =

    /// Hand-rolled adapter for offline development. Identifiers match
    /// Krispy Kreme's `shopId` field so swapping to LiveApi is a
    /// pure substitution.
    module InMemory =

        let private seedStores : Map<StoreId, Store> =
            [ { Id = StoreId 899
                Name = "Krispy Kreme Seattle - 1st Ave South"
                Address = "1900 1st Ave S, Seattle, WA 98134"
                Location = { Latitude = 47.58536; Longitude = -122.33407 } }
              { Id = StoreId 898
                Name = "Krispy Kreme North Seattle - Aurora Ave N"
                Address = "12505 Aurora Ave N, Seattle, WA 98133"
                Location = { Latitude = 47.71813; Longitude = -122.34488 } }
              { Id = StoreId 896
                Name = "Krispy Kreme Issaquah - E Lake Sammamish Pkwy"
                Address = "6210 East Lake Sammamish Pkwy, Issaquah, WA 98029"
                Location = { Latitude = 47.5454; Longitude = -122.0468 } } ]
            |> List.map (fun s -> s.Id, s)
            |> Map.ofList

        let private seedStatuses : ConcurrentDictionary<StoreId, HotLightStatus> =
            let d = ConcurrentDictionary<StoreId, HotLightStatus>()
            d.[StoreId 899] <- On
            d.[StoreId 898] <- On
            d.[StoreId 896] <- Off
            d

        let getStore : Ports.GetStore =
            fun id ->
                async {
                    match Map.tryFind id seedStores with
                    | Some store -> return Ok store
                    | None -> return Error (StoreNotFound id)
                }

        let getHotLightStatus : Ports.GetHotLightStatus =
            fun id ->
                async {
                    if Map.containsKey id seedStores then
                        let status =
                            match seedStatuses.TryGetValue id with
                            | true, s -> s
                            | false, _ -> Unknown
                        return Ok {
                            StoreId = id
                            Status = status
                            ObservedAt = DateTimeOffset.UtcNow
                        }
                    else
                        return Error (StoreNotFound id)
                }
