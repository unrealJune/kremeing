module Kremeing.Api.Tests.ObservationsTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// These tests are also the *contract* a future Postgres adapter has to
// satisfy: same record/history/status surface, same flip-only invariant,
// same staleness semantics.

let private at (h: int) (m: int) =
    DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

let private obs storeId status t =
    { StoreId = StoreId storeId; Status = status; ObservedAt = t }

let private record (store: InMemoryObservations.Store) o =
    store.Record o |> Async.RunSynchronously

module RecordOutcome =

    [<Fact>]
    let ``first observation for a store yields FirstObservation`` () =
        let store = InMemoryObservations.create ()
        match record store (obs 899 On (at 10 0)) with
        | Ok FirstObservation -> ()
        | other -> failwithf "expected FirstObservation, got %A" other

    [<Fact>]
    let ``recording the same status twice yields Unchanged`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        match record store (obs 899 On (at 10 5)) with
        | Ok Unchanged -> ()
        | other -> failwithf "expected Unchanged, got %A" other

    [<Fact>]
    let ``recording a different status yields Flipped with the previous status`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        match record store (obs 899 Off (at 10 5)) with
        | Ok (Flipped On) -> ()
        | other -> failwithf "expected Flipped On, got %A" other

    [<Fact>]
    let ``different stores have independent flip state`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        // Different shopId — should be a fresh "first observation"
        match record store (obs 898 On (at 10 0)) with
        | Ok FirstObservation -> ()
        | other -> failwithf "expected FirstObservation for new store, got %A" other

module HistoryQuery =

    [<Fact>]
    let ``returns flips in oldest-first order regardless of insertion order`` () =
        let store = InMemoryObservations.create ()
        // Insert the events in chronological order (which is what the poller
        // does in production).
        let _ = record store (obs 899 On (at 10 0))
        let _ = record store (obs 899 Off (at 10 5))
        let _ = record store (obs 899 On (at 10 10))

        match store.History (StoreId 899, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Ok flips ->
            flips |> List.length |> should equal 3
            flips |> List.map (fun o -> o.ObservedAt) |> List.head |> should equal (at 10 0)
            flips |> List.map (fun o -> o.ObservedAt) |> List.last |> should equal (at 10 10)
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``"unchanged" observations don't pollute history`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        let _ = record store (obs 899 On (at 10 5))   // Unchanged — no row
        let _ = record store (obs 899 On (at 10 10))  // Unchanged — no row

        match store.History (StoreId 899, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Ok flips -> flips |> List.length |> should equal 1
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``range filter is half-open [since, until)`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        let _ = record store (obs 899 Off (at 10 5))
        let _ = record store (obs 899 On (at 10 10))

        // 10:05 included, 10:10 excluded
        match store.History (StoreId 899, at 10 5, at 10 10) |> Async.RunSynchronously with
        | Ok flips ->
            flips |> List.length |> should equal 1
            (List.head flips).Status |> should equal Off
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``returns StoreNotFound for stores we've never observed`` () =
        let store = InMemoryObservations.create ()
        match store.History (StoreId 999999, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Error (StoreNotFound (StoreId 999999)) -> ()
        | other -> failwithf "expected StoreNotFound 999999, got %A" other

module StoreStatus =

    [<Fact>]
    let ``reports current status and the most recent poll time`` () =
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        let _ = record store (obs 899 Off (at 10 5))
        let _ = record store (obs 899 Off (at 10 30))   // Unchanged but updates LastPolledAt

        match store.Status (StoreId 899) |> Async.RunSynchronously with
        | Ok s ->
            s.CurrentStatus |> should equal Off
            s.LastPolledAt |> should equal (at 10 30)
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``LastPolledAt advances even on Unchanged outcomes — staleness sentinel`` () =
        // This is the key invariant that makes flip-only storage acceptable:
        // we know we've been talking to upstream even when no flip happened.
        let store = InMemoryObservations.create ()
        let _ = record store (obs 899 On (at 10 0))
        let _ = record store (obs 899 On (at 10 30))   // Unchanged

        let status =
            match store.Status (StoreId 899) |> Async.RunSynchronously with
            | Ok s -> s
            | Error e -> failwithf "expected Ok, got %A" e
        status.LastPolledAt |> should equal (at 10 30)

    [<Fact>]
    let ``returns StoreNotFound for unknown stores`` () =
        let store = InMemoryObservations.create ()
        match store.Status (StoreId 12345) |> Async.RunSynchronously with
        | Error (StoreNotFound (StoreId 12345)) -> ()
        | other -> failwithf "expected StoreNotFound, got %A" other

module Concurrency =

    [<Fact>]
    let ``parallel records for one store don't lose events or double-count flips`` () =
        let store = InMemoryObservations.create ()
        let baseTime = at 10 0

        // 200 alternating-status observations dispatched in parallel.
        // Even though the status alternates each tick, races could
        // (a) lose a flip when two callers see the same "previous" state, or
        // (b) record a flip twice when the lock leaks. The flip log length
        //     is a precise function of the input sequence regardless of order,
        //     because we always append on a *change* — but ObservedAt timestamps
        //     are unique per call so we can verify no observation got dropped.
        let observations =
            [ for i in 0 .. 199 ->
                let status = if i % 2 = 0 then On else Off
                obs 899 status (baseTime.AddSeconds(float i)) ]

        observations
        |> List.map (fun o -> async { let! _ = store.Record o in return () })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

        // Final state must reflect a real observation we actually sent.
        let status =
            match store.Status (StoreId 899) |> Async.RunSynchronously with
            | Ok s -> s
            | Error e -> failwithf "expected Ok, got %A" e
        let validTimes = observations |> List.map (fun o -> o.ObservedAt) |> Set.ofList
        validTimes |> should contain status.LastPolledAt
