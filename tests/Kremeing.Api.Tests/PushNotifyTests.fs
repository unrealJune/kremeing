module Kremeing.Api.Tests.PushNotifyTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core
open Kremeing.Api

// PushNotify.notifyFlipOn is the fan-out side: given a flip, fetch
// subscriptions, dispatch a push to each, clean up Gone endpoints.
// Every test below feeds it stub ports and asserts on what got called.

let private storeEntry sid : Discovery.RegistryEntry = {
    ShopId = sid
    Name = sprintf "Krispy Kreme Test %d" sid
    Address = "1 Test Way"
    Location = { Latitude = 47.6; Longitude = -122.3 }
    ShopUrl = ""
    SearchKey = "Test, ZZ"
}

let private storedSub id endpoint : StoredPushSubscription = {
    Id = PushSubscriptionId (int64 id)
    StoreId = StoreId 0   // unused by dispatcher
    Subscription = { Endpoint = endpoint; P256dh = "p"; Auth = "a" }
    CreatedAt = DateTimeOffset.UtcNow
}

[<Fact>]
let ``dispatches one push per subscription found for the store`` () =
    let sub1 = storedSub 1 "https://fcm/A"
    let sub2 = storedSub 2 "https://fcm/B"
    let sub3 = storedSub 3 "https://fcm/C"

    let dispatched = ResizeArray<string>()
    let dispatch : PushDispatch.Dispatch =
        fun (sub, _) ->
            async {
                dispatched.Add sub.Subscription.Endpoint
                return PushDispatch.Delivered
            }
    let find : Ports.FindPushSubscriptionsForStore =
        fun _ -> async { return Ok [ sub1; sub2; sub3 ] }
    let deleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
        fun _ -> async { return Ok 0 }

    let notify = PushNotify.notifyFlipOn find deleteByEndpoint dispatch
    notify (StoreId 899, storeEntry 899) |> Async.RunSynchronously

    dispatched.Count |> should equal 3
    dispatched |> Seq.toList
              |> should equal [ "https://fcm/A"; "https://fcm/B"; "https://fcm/C" ]

[<Fact>]
let ``Gone outcome triggers DeleteByEndpoint cleanup`` () =
    let sub = storedSub 1 "https://fcm/dead"
    let deleted = ResizeArray<string>()
    let dispatch : PushDispatch.Dispatch =
        fun _ -> async { return PushDispatch.Gone }
    let find : Ports.FindPushSubscriptionsForStore =
        fun _ -> async { return Ok [ sub ] }
    let deleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
        fun ep ->
            async {
                deleted.Add ep
                return Ok 1
            }

    PushNotify.notifyFlipOn find deleteByEndpoint dispatch
        (StoreId 899, storeEntry 899)
    |> Async.RunSynchronously

    deleted |> Seq.toList |> should equal [ "https://fcm/dead" ]

[<Fact>]
let ``Delivered and TransientFailure outcomes do NOT delete`` () =
    // Bouncing on transient failures would delete healthy subscriptions
    // any time the push service had a hiccup. Only Gone deletes.
    let subs = [
        storedSub 1 "https://fcm/healthy"
        storedSub 2 "https://fcm/blip"
    ]
    let mutable index = 0
    let dispatch : PushDispatch.Dispatch =
        fun _ ->
            async {
                let outcome =
                    if index = 0 then PushDispatch.Delivered
                    else PushDispatch.TransientFailure "5xx"
                index <- index + 1
                return outcome
            }
    let mutable deleteCalls = 0
    let find : Ports.FindPushSubscriptionsForStore =
        fun _ -> async { return Ok subs }
    let deleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
        fun _ -> async { deleteCalls <- deleteCalls + 1; return Ok 1 }

    PushNotify.notifyFlipOn find deleteByEndpoint dispatch
        (StoreId 899, storeEntry 899)
    |> Async.RunSynchronously

    deleteCalls |> should equal 0

[<Fact>]
let ``payload URL is relative and includes store + lat/lng`` () =
    // Service-worker resolves relative URLs against its own origin, so
    // the payload doesn't need to know the deployed hostname.
    let mutable seenUrl = ""
    let mutable seenStoreId = 0
    let mutable seenTitle = ""
    let dispatch : PushDispatch.Dispatch =
        fun (_, payload) ->
            async {
                seenUrl <- payload.url
                seenStoreId <- payload.storeId
                seenTitle <- payload.title
                return PushDispatch.Delivered
            }
    let find : Ports.FindPushSubscriptionsForStore =
        fun _ -> async { return Ok [ storedSub 1 "https://x/y" ] }
    let deleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
        fun _ -> async { return Ok 0 }

    PushNotify.notifyFlipOn find deleteByEndpoint dispatch
        (StoreId 899, storeEntry 899)
    |> Async.RunSynchronously

    seenStoreId |> should equal 899
    seenUrl |> should haveSubstring "/?store=899"
    seenUrl |> should haveSubstring "lat=47.6"
    seenUrl |> should haveSubstring "lng=-122.3"
    // Title carries the store name so the notification is useful at a glance.
    seenTitle |> should haveSubstring "Krispy Kreme Test 899"

[<Fact>]
let ``find error is swallowed so a transient lookup blip doesn't crash the tick`` () =
    let dispatch : PushDispatch.Dispatch =
        fun _ -> async { return failwith "must not be called when find fails" }
    let find : Ports.FindPushSubscriptionsForStore =
        fun _ -> async { return Error (UpstreamUnavailable "transient") }
    let deleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
        fun _ -> async { return Ok 0 }

    // Must not throw.
    PushNotify.notifyFlipOn find deleteByEndpoint dispatch
        (StoreId 899, storeEntry 899)
    |> Async.RunSynchronously
