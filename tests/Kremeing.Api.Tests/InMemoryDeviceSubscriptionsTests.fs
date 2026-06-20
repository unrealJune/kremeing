module Kremeing.Api.Tests.InMemoryDeviceSubscriptionsTests

open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// The in-memory device store backs native push when no Postgres is
// configured and is the contract the Postgres store must mirror. Key
// invariant: idempotent on the FCM token (re-register UPSERTs in place).

let private reg token lat lng radius : DevicePushRegistration = {
    Token = token
    Platform = Android
    Location = { Latitude = lat; Longitude = lng }
    RadiusMiles = radius
}

let private run a = Async.RunSynchronously a
let private ok = function Ok v -> v | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``subscribe then GetAll returns the registration`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> run |> ok |> ignore
    let all = store.GetAll () |> run |> ok
    all |> List.length |> should equal 1
    all.[0].Registration.Token |> should equal "tok-1"
    all.[0].Registration.RadiusMiles |> should equal 25.0

[<Fact>]
let ``re-subscribing the same token upserts rather than duplicating`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> run |> ok |> ignore
    // Same token, moved location and widened radius.
    store.Subscribe (reg "tok-1" 40.7 -74.0 50.0) |> run |> ok |> ignore

    let all = store.GetAll () |> run |> ok
    all |> List.length |> should equal 1
    all.[0].Registration.Location.Latitude |> should equal 40.7
    all.[0].Registration.RadiusMiles |> should equal 50.0

[<Fact>]
let ``re-subscribing preserves the original id and created time`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    let id1 = store.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> run |> ok
    let createdBefore = (store.GetAll () |> run |> ok).[0].CreatedAt
    let id2 = store.Subscribe (reg "tok-1" 40.7 -74.0 50.0) |> run |> ok
    id2 |> should equal id1
    (store.GetAll () |> run |> ok).[0].CreatedAt |> should equal createdBefore

[<Fact>]
let ``distinct tokens create distinct rows`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.Subscribe (reg "tok-2" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.GetAll () |> run |> ok |> List.length |> should equal 2

[<Fact>]
let ``unsubscribe removes the matching token`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Subscribe (reg "tok-1" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.Subscribe (reg "tok-2" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.Unsubscribe "tok-1" |> run |> ok
    let all = store.GetAll () |> run |> ok
    all |> List.map (fun s -> s.Registration.Token) |> should equal [ "tok-2" ]

[<Fact>]
let ``unsubscribing an unknown token is a no-op success`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Unsubscribe "never-registered" |> run |> ok
    store.GetAll () |> run |> ok |> should be Empty

[<Fact>]
let ``GetAll returns rows oldest-first by insertion`` () =
    let store = InMemoryDeviceSubscriptions.create ()
    store.Subscribe (reg "a" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.Subscribe (reg "b" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.Subscribe (reg "c" 47.6 -122.3 25.0) |> run |> ok |> ignore
    store.GetAll () |> run |> ok
    |> List.map (fun s -> s.Registration.Token)
    |> should equal [ "a"; "b"; "c" ]
