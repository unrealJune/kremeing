module Kremeing.Postgres.Tests.PostgresDevicePushSubscriptionsTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// Device-push (FCM) subscription storage against real Postgres. The
// contract these pin is what the /device-subscriptions endpoints and the
// proximity fan-out read against — and it must mirror the in-memory store
// (see InMemoryDeviceSubscriptionsTests). TRUNCATE between cases.

[<Collection("Postgres")>]
type Tests(fx: PostgresFixture.PostgresFixture) =

    let store () =
        fx.Reset().GetAwaiter().GetResult()
        Postgres.createDevicePushSubscriptions fx.ConnectionString

    let reg token lat lng radius : DevicePushRegistration =
        { Token = token
          Platform = Android
          Location = { Latitude = lat; Longitude = lng }
          RadiusMiles = radius }

    let runSync (a: Async<_>) = a |> Async.RunSynchronously

    interface IClassFixture<PostgresFixture.PostgresFixture>

    [<PgProbe.PgFact>]
    member _.``Subscribe inserts a row and returns its id`` () =
        let s = store ()
        match s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync with
        | Ok (DevicePushSubscriptionId id) -> id |> should be (greaterThan 0L)
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``Subscribe is idempotent on the token — same id returned`` () =
        let s = store ()
        match s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync,
              s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync with
        | Ok id1, Ok id2 -> id1 |> should equal id2
        | other -> failwithf "expected two Ok ids, got %A" other

    [<PgProbe.PgFact>]
    member _.``Re-subscribing refreshes location and radius without duplicating`` () =
        let s = store ()
        let _ = s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync
        let _ = s.Subscribe (reg "tok-1" 40.7 -74.0 50.0) |> runSync
        match s.GetAll () |> runSync with
        | Ok subs ->
            subs |> List.length |> should equal 1
            subs.[0].Registration.Location.Latitude |> should equal 40.7
            subs.[0].Registration.RadiusMiles |> should equal 50.0
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``GetAll returns nothing when no device has registered`` () =
        let s = store ()
        match s.GetAll () |> runSync with
        | Ok subs -> subs |> should be Empty
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``GetAll returns every distinct token`` () =
        let s = store ()
        let _ = s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync
        let _ = s.Subscribe (reg "tok-2" 45.5 -122.6 10.0) |> runSync
        match s.GetAll () |> runSync with
        | Ok subs ->
            subs |> List.map (fun r -> r.Registration.Token)
                 |> Set.ofList
                 |> should equal (Set.ofList [ "tok-1"; "tok-2" ])
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``round-trips the platform and coordinates faithfully`` () =
        let s = store ()
        let _ = s.Subscribe (reg "tok-1" 47.6062 -122.3321 33.0) |> runSync
        match s.GetAll () |> runSync with
        | Ok [ row ] ->
            row.Registration.Platform |> should equal Android
            row.Registration.Location.Latitude  |> should (equalWithin 1e-9) 47.6062
            row.Registration.Location.Longitude |> should (equalWithin 1e-9) -122.3321
            row.Registration.RadiusMiles |> should equal 33.0
        | other -> failwithf "expected one row, got %A" other

    [<PgProbe.PgFact>]
    member _.``Unsubscribe drops only the matching token`` () =
        let s = store ()
        let _ = s.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> runSync
        let _ = s.Subscribe (reg "tok-2" 45.5 -122.6 10.0) |> runSync
        let _ = s.Unsubscribe "tok-1" |> runSync
        match s.GetAll () |> runSync with
        | Ok [ row ] -> row.Registration.Token |> should equal "tok-2"
        | other -> failwithf "expected one remaining row, got %A" other

    [<PgProbe.PgFact>]
    member _.``Unsubscribe of a missing token succeeds silently — idempotent`` () =
        let s = store ()
        match s.Unsubscribe "never-registered" |> runSync with
        | Ok () -> ()
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``GetAll returns rows oldest-first by created_at`` () =
        let s = store ()
        let _ = s.Subscribe (reg "a" 47.6 -122.3 25.0) |> runSync
        System.Threading.Thread.Sleep 5
        let _ = s.Subscribe (reg "b" 47.6 -122.3 25.0) |> runSync
        System.Threading.Thread.Sleep 5
        let _ = s.Subscribe (reg "c" 47.6 -122.3 25.0) |> runSync
        match s.GetAll () |> runSync with
        | Ok subs ->
            subs |> List.map (fun r -> r.Registration.Token)
                 |> should equal [ "a"; "b"; "c" ]
        | Error e -> failwithf "expected Ok, got %A" e
