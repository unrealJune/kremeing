namespace Kremeing.Api

open System
open System.Collections.Concurrent
open Kremeing.Contracts.Domain
open Kremeing.Core

/// In-memory implementation of the Observations ports. The flip-only
/// invariant lives here: only status *changes* append to the flip log,
/// but every successful observation refreshes `LastPolledAt`. A future
/// Postgres adapter must preserve the same invariants — see ObservationsTests.
module InMemoryObservations =

    [<NoEquality; NoComparison>]
    type private StoreState = {
        mutable Flips: HotLightObservation list   // newest first
        mutable LastPolledAt: DateTimeOffset
        mutable CurrentStatus: HotLightStatus
        mutable HasObservation: bool
        mutable FirstObservedAt: DateTimeOffset
        mutable LastFlippedAt: DateTimeOffset option
    }

    let private freshState () : StoreState = {
        Flips = []
        LastPolledAt = DateTimeOffset.MinValue
        CurrentStatus = Unknown
        HasObservation = false
        FirstObservedAt = DateTimeOffset.MinValue
        LastFlippedAt = None
    }

    type Store private () =
        let states = ConcurrentDictionary<StoreId, StoreState>()

        member private _.GetOrAdd (id: StoreId) : StoreState =
            states.GetOrAdd(id, fun _ -> freshState ())

        member this.Record : Ports.RecordObservation =
            fun obs ->
                async {
                    let s = this.GetOrAdd obs.StoreId
                    let outcome =
                        lock s (fun () ->
                            // Always refresh staleness sentinel — even
                            // when the status is unchanged. The flip log
                            // captures *changes*, but staleness needs the
                            // last successful contact.
                            s.LastPolledAt <- obs.ObservedAt
                            if not s.HasObservation then
                                s.Flips <- [obs]
                                s.CurrentStatus <- obs.Status
                                s.HasObservation <- true
                                s.FirstObservedAt <- obs.ObservedAt
                                FirstObservation
                            elif s.CurrentStatus = obs.Status then
                                Unchanged
                            else
                                let prev = s.CurrentStatus
                                s.Flips <- obs :: s.Flips
                                s.CurrentStatus <- obs.Status
                                s.LastFlippedAt <- Some obs.ObservedAt
                                Flipped prev)
                    return Ok outcome
                }

        member this.History : Ports.GetHistory =
            fun (id, since, until) ->
                async {
                    match states.TryGetValue id with
                    | false, _ -> return Error (StoreNotFound id)
                    | true, s ->
                        let snapshot =
                            lock s (fun () -> s.Flips)
                        let inRange =
                            snapshot
                            |> List.filter (fun obs ->
                                obs.ObservedAt >= since && obs.ObservedAt < until)
                            |> List.rev   // hand back oldest-first for charting
                        return Ok inRange
                }

        member this.Status : Ports.GetStoreStatus =
            fun id ->
                async {
                    match states.TryGetValue id with
                    | false, _ -> return Error (StoreNotFound id)
                    | true, s ->
                        let result =
                            lock s (fun () ->
                                { StoreId = id
                                  CurrentStatus = s.CurrentStatus
                                  LastPolledAt = s.LastPolledAt
                                  LastFlippedAt = s.LastFlippedAt
                                  FirstObservedAt = s.FirstObservedAt })
                        return Ok result
                }

        static member Empty () = Store()

    let create () = Store.Empty()

/// In-memory device-push subscription store. Used when native push is
/// configured but no Postgres is available (e.g. local Android dev), and
/// as a fast, dependency-free target for tests. Idempotent on the FCM
/// token: re-registering the same token refreshes its location/radius.
module InMemoryDeviceSubscriptions =

    [<NoEquality; NoComparison>]
    type private Row = {
        Id: int64
        Registration: DevicePushRegistration
        CreatedAt: DateTimeOffset
    }

    type Store private () =
        // Keyed by token so a re-registration upserts in place.
        let rows = ConcurrentDictionary<string, Row>()
        let mutable nextId = 0L
        let gate = obj ()

        member _.Subscribe : Ports.SubscribeDevicePush =
            fun registration ->
                async {
                    let row =
                        lock gate (fun () ->
                            match rows.TryGetValue registration.Token with
                            | true, existing ->
                                let updated =
                                    { existing with Registration = registration }
                                rows.[registration.Token] <- updated
                                updated
                            | false, _ ->
                                nextId <- nextId + 1L
                                let row =
                                    { Id = nextId
                                      Registration = registration
                                      CreatedAt = DateTimeOffset.UtcNow }
                                rows.[registration.Token] <- row
                                row)
                    return Ok (DevicePushSubscriptionId row.Id)
                }

        member _.Unsubscribe : Ports.UnsubscribeDevicePush =
            fun token ->
                async {
                    rows.TryRemove token |> ignore
                    return Ok ()
                }

        member _.GetAll : Ports.GetAllDevicePushSubscriptions =
            fun () ->
                async {
                    let all =
                        rows.Values
                        |> Seq.sortBy (fun r -> r.Id)
                        |> Seq.map (fun r ->
                            { Id = DevicePushSubscriptionId r.Id
                              Registration = r.Registration
                              CreatedAt = r.CreatedAt } : StoredDevicePushSubscription)
                        |> List.ofSeq
                    return Ok all
                }

        static member Empty () = Store()

    let create () = Store.Empty()
