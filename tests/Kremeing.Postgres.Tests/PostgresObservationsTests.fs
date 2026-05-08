module Kremeing.Postgres.Tests.PostgresObservationsTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// These tests run against a real Postgres container and assert the
// SAME contract as InMemoryObservations. Reading them side-by-side
// with ObservationsTests.fs is the equivalence proof — any behavior
// that diverges between the two adapters means one of them is wrong.

type Tests(fx: PostgresFixture.PostgresFixture) =

    let store () =
        // Each test resets the schema then gets a fresh adapter.
        fx.Reset().GetAwaiter().GetResult()
        Postgres.create fx.ConnectionString

    let at h m = DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

    let obs storeId status t =
        { StoreId = StoreId storeId; Status = status; ObservedAt = t }

    let record (s: Postgres.Store) o =
        s.Record o |> Async.RunSynchronously

    interface IClassFixture<PostgresFixture.PostgresFixture>

    [<PgProbe.PgFact>]
    member _.``first observation for a store yields FirstObservation`` () =
        let s = store ()
        match record s (obs 899 On (at 10 0)) with
        | Ok FirstObservation -> ()
        | other -> failwithf "expected FirstObservation, got %A" other

    [<PgProbe.PgFact>]
    member _.``recording the same status twice yields Unchanged`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        match record s (obs 899 On (at 10 5)) with
        | Ok Unchanged -> ()
        | other -> failwithf "expected Unchanged, got %A" other

    [<PgProbe.PgFact>]
    member _.``recording a different status yields Flipped with the previous status`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        match record s (obs 899 Off (at 10 5)) with
        | Ok (Flipped On) -> ()
        | other -> failwithf "expected Flipped On, got %A" other

    [<PgProbe.PgFact>]
    member _.``unchanged observations don't pollute history`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 On (at 10 5))
        let _ = record s (obs 899 On (at 10 10))
        match s.History (StoreId 899, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Ok flips -> flips |> List.length |> should equal 1
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``history range is half-open [since, until)`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 Off (at 10 5))
        let _ = record s (obs 899 On (at 10 10))
        match s.History (StoreId 899, at 10 5, at 10 10) |> Async.RunSynchronously with
        | Ok flips ->
            flips |> List.length |> should equal 1
            (List.head flips).Status |> should equal Off
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``history returns oldest-first regardless of insertion order`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 Off (at 10 5))
        let _ = record s (obs 899 On (at 10 10))
        match s.History (StoreId 899, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Ok flips ->
            flips |> List.length |> should equal 3
            (List.head flips).ObservedAt |> should equal (at 10 0)
            (List.last flips).ObservedAt |> should equal (at 10 10)
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``history returns StoreNotFound for unknown stores`` () =
        let s = store ()
        match s.History (StoreId 999999, at 9 0, at 11 0) |> Async.RunSynchronously with
        | Error (StoreNotFound (StoreId 999999)) -> ()
        | other -> failwithf "expected StoreNotFound, got %A" other

    [<PgProbe.PgFact>]
    member _.``status reports current status, last poll, and first observation`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 Off (at 10 5))
        let _ = record s (obs 899 Off (at 10 30))   // Unchanged but advances LastPolledAt
        match s.Status (StoreId 899) |> Async.RunSynchronously with
        | Ok status ->
            status.CurrentStatus |> should equal Off
            status.LastPolledAt |> should equal (at 10 30)
            status.FirstObservedAt |> should equal (at 10 0)
            status.LastFlippedAt |> should equal (Some (at 10 5))
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``status returns LastFlippedAt = None when there has been no flip`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 On (at 10 30))
        match s.Status (StoreId 899) |> Async.RunSynchronously with
        | Ok status -> status.LastFlippedAt |> should equal None
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``status returns StoreNotFound for unknown stores`` () =
        let s = store ()
        match s.Status (StoreId 12345) |> Async.RunSynchronously with
        | Error (StoreNotFound (StoreId 12345)) -> ()
        | other -> failwithf "expected StoreNotFound, got %A" other

    [<PgProbe.PgFact>]
    member _.``different stores have independent flip state`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        match record s (obs 898 On (at 10 0)) with
        | Ok FirstObservation -> ()
        | other -> failwithf "expected FirstObservation for new store, got %A" other

    [<PgProbe.PgFact>]
    member _.``LastPolledAt advances on Unchanged outcomes — staleness sentinel`` () =
        let s = store ()
        let _ = record s (obs 899 On (at 10 0))
        let _ = record s (obs 899 On (at 10 30))
        match s.Status (StoreId 899) |> Async.RunSynchronously with
        | Ok status -> status.LastPolledAt |> should equal (at 10 30)
        | Error e -> failwithf "expected Ok, got %A" e
