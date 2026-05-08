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
