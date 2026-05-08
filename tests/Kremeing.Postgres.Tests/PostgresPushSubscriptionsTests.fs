module Kremeing.Postgres.Tests.PostgresPushSubscriptionsTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// Push-subscription storage tests against real Postgres. The contract
// these tests pin is what the API endpoints, the poller dispatcher,
// and the bounce-cleaner all read against. Same fixture as the
// observations tests — TRUNCATE between cases for isolation.

[<Collection("Postgres")>]
type Tests(fx: PostgresFixture.PostgresFixture) =

    let store () =
        fx.Reset().GetAwaiter().GetResult()
        Postgres.createPushSubscriptions fx.ConnectionString

    let sub endpoint p256dh auth : PushSubscription =
        { Endpoint = endpoint; P256dh = p256dh; Auth = auth }

    let example =
        sub "https://fcm.googleapis.com/fcm/send/abc" "p256-key-material" "auth-secret"

    let runSync (a: Async<_>) = a |> Async.RunSynchronously

    interface IClassFixture<PostgresFixture.PostgresFixture>

    [<PgProbe.PgFact>]
    member _.``Subscribe inserts a row and returns its id`` () =
        let s = store ()
        match s.Subscribe (StoreId 899, example) |> runSync with
        | Ok (PushSubscriptionId id) -> id |> should be (greaterThan 0L)
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``Subscribe is idempotent on (store_id, endpoint) — same id returned`` () =
        let s = store ()
        match s.Subscribe (StoreId 899, example) |> runSync,
              s.Subscribe (StoreId 899, example) |> runSync with
        | Ok id1, Ok id2 -> id1 |> should equal id2
        | other -> failwithf "expected two Ok ids, got %A" other

    [<PgProbe.PgFact>]
    member _.``Re-subscribing refreshes p256dh and auth without creating a duplicate`` () =
        // Browsers rotate push key material occasionally; the row should
        // update in place rather than producing two rows for one device.
        let s = store ()
        let _ = s.Subscribe (StoreId 899, example) |> runSync
        let rotated = { example with P256dh = "new-p256"; Auth = "new-auth" }
        let _ = s.Subscribe (StoreId 899, rotated) |> runSync
        match s.FindForStore (StoreId 899) |> runSync with
        | Ok subs ->
            subs |> List.length |> should equal 1
            subs.[0].Subscription.P256dh |> should equal "new-p256"
            subs.[0].Subscription.Auth   |> should equal "new-auth"
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``Same endpoint can subscribe to multiple stores — independent rows`` () =
        let s = store ()
        let _ = s.Subscribe (StoreId 899, example) |> runSync
        let _ = s.Subscribe (StoreId 898, example) |> runSync
        match s.FindForStore (StoreId 899) |> runSync,
              s.FindForStore (StoreId 898) |> runSync with
        | Ok a, Ok b ->
            a |> List.length |> should equal 1
            b |> List.length |> should equal 1
        | other -> failwithf "expected two Oks, got %A" other

    [<PgProbe.PgFact>]
    member _.``FindForStore returns nothing for stores nobody subscribed to`` () =
        let s = store ()
        match s.FindForStore (StoreId 12345) |> runSync with
        | Ok subs -> subs |> should be Empty
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``Unsubscribe drops only the matching (store_id, endpoint) row`` () =
        let s = store ()
        let endpointA = "https://fcm.googleapis.com/fcm/send/A"
        let endpointB = "https://fcm.googleapis.com/fcm/send/B"
        let _ = s.Subscribe (StoreId 899, sub endpointA "p1" "a1") |> runSync
        let _ = s.Subscribe (StoreId 899, sub endpointB "p2" "a2") |> runSync

        let _ = s.Unsubscribe (StoreId 899, endpointA) |> runSync
        match s.FindForStore (StoreId 899) |> runSync with
        | Ok [ s1 ] ->
            s1.Subscription.Endpoint |> should equal endpointB
        | other -> failwithf "expected one remaining row for B, got %A" other

    [<PgProbe.PgFact>]
    member _.``Unsubscribe of a missing row succeeds silently — idempotent`` () =
        let s = store ()
        match s.Unsubscribe (StoreId 899, "no-such-endpoint") |> runSync with
        | Ok () -> ()
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``DeleteByEndpoint removes every row sharing that endpoint`` () =
        // 410 Gone cleanup: a device that's gone should be cleared from
        // every store it was subscribed to, not just one.
        let s = store ()
        let dead = "https://fcm.googleapis.com/fcm/send/dead-device"
        let _ = s.Subscribe (StoreId 899, sub dead "p1" "a1") |> runSync
        let _ = s.Subscribe (StoreId 898, sub dead "p2" "a2") |> runSync
        let _ = s.Subscribe (StoreId 896, sub dead "p3" "a3") |> runSync
        // A subscription with a different endpoint must NOT be touched.
        let _ = s.Subscribe (StoreId 899, sub "https://other/123" "px" "ax") |> runSync

        match s.DeleteByEndpoint dead |> runSync with
        | Ok n -> n |> should equal 3
        | Error e -> failwithf "expected Ok, got %A" e

        // The unrelated subscription is still there.
        match s.FindForStore (StoreId 899) |> runSync with
        | Ok subs ->
            subs |> List.length |> should equal 1
            subs.[0].Subscription.Endpoint |> should equal "https://other/123"
        | Error e -> failwithf "expected Ok, got %A" e

    [<PgProbe.PgFact>]
    member _.``DeleteByEndpoint of a missing endpoint returns 0`` () =
        let s = store ()
        match s.DeleteByEndpoint "https://nope" |> runSync with
        | Ok 0 -> ()
        | other -> failwithf "expected Ok 0, got %A" other

    [<PgProbe.PgFact>]
    member _.``FindForStore returns rows oldest-first by created_at`` () =
        // Order matters for the dispatcher — older subscriptions are
        // typically more valuable (more likely to be active long-lived
        // devices) than ones registered seconds ago that may be a
        // probe or test.
        let s = store ()
        let _ = s.Subscribe (StoreId 899, sub "https://e/1" "p1" "a1") |> runSync
        // Microsecond gap — Postgres now() will advance.
        System.Threading.Thread.Sleep 5
        let _ = s.Subscribe (StoreId 899, sub "https://e/2" "p2" "a2") |> runSync
        System.Threading.Thread.Sleep 5
        let _ = s.Subscribe (StoreId 899, sub "https://e/3" "p3" "a3") |> runSync

        match s.FindForStore (StoreId 899) |> runSync with
        | Ok subs ->
            subs |> List.map (fun r -> r.Subscription.Endpoint)
                 |> should equal [ "https://e/1"; "https://e/2"; "https://e/3" ]
        | Error e -> failwithf "expected Ok, got %A" e
