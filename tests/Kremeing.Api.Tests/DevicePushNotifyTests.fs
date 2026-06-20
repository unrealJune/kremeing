module Kremeing.Api.Tests.DevicePushNotifyTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core
open Kremeing.Api

// DevicePushNotify.notifyFlipOn is the native-push fan-out: given a flip,
// fetch every device subscription, dispatch only to those within range of
// the flipped store, and unsubscribe tokens FCM reports as Gone. Every
// test feeds it stub ports and asserts on what got called.

let private storeEntry sid lat lng : Discovery.RegistryEntry = {
    ShopId = sid
    Name = sprintf "Krispy Kreme Test %d" sid
    Address = "1 Test Way"
    Location = { Latitude = lat; Longitude = lng }
    ShopUrl = ""
    SearchKey = "Test, ZZ"
}

let private storedSub id token lat lng radius : StoredDevicePushSubscription = {
    Id = DevicePushSubscriptionId (int64 id)
    Registration = {
        Token = token
        Platform = Android
        Location = { Latitude = lat; Longitude = lng }
        RadiusMiles = radius
    }
    CreatedAt = DateTimeOffset.UtcNow
}

// Seattle store, used as the flip location throughout.
let private seattleStore = storeEntry 899 47.6062 -122.3321

[<Fact>]
let ``dispatches to every subscription whose radius covers the flipped store`` () =
    // Two Seattle-area devices (small radius, in range) + one New York
    // device (in range only if radius were huge — it's not).
    let near1 = storedSub 1 "near-1" 47.61 -122.33 10.0
    let near2 = storedSub 2 "near-2" 47.55 -122.30 10.0
    let farNY = storedSub 3 "far-ny" 40.7128 -74.0060 10.0

    let dispatched = ResizeArray<string>()
    let dispatch : DevicePushDispatch.Dispatch =
        fun (sub, _) ->
            async {
                dispatched.Add sub.Registration.Token
                return DevicePushDispatch.Delivered
            }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ near1; near2; farNY ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    dispatched |> Seq.toList |> should equal [ "near-1"; "near-2" ]

[<Fact>]
let ``does not dispatch to a subscription outside its radius`` () =
    // Portland device with a 50-mi radius; Seattle is ~145 mi away.
    let portland = storedSub 1 "portland" 45.5152 -122.6784 50.0
    let mutable dispatchCalls = 0
    let dispatch : DevicePushDispatch.Dispatch =
        fun _ -> async { dispatchCalls <- dispatchCalls + 1; return DevicePushDispatch.Delivered }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ portland ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    dispatchCalls |> should equal 0

[<Fact>]
let ``a wide radius brings a distant device back into range`` () =
    // Same Portland device, but a 300-mi radius now covers Seattle.
    let portland = storedSub 1 "portland-wide" 45.5152 -122.6784 300.0
    let dispatched = ResizeArray<string>()
    let dispatch : DevicePushDispatch.Dispatch =
        fun (sub, _) -> async { dispatched.Add sub.Registration.Token; return DevicePushDispatch.Delivered }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ portland ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    dispatched |> Seq.toList |> should equal [ "portland-wide" ]

[<Fact>]
let ``Gone outcome unsubscribes the dead token`` () =
    let near = storedSub 1 "dead-token" 47.61 -122.33 10.0
    let unsubscribed = ResizeArray<string>()
    let dispatch : DevicePushDispatch.Dispatch =
        fun _ -> async { return DevicePushDispatch.Gone }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ near ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun token -> async { unsubscribed.Add token; return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    unsubscribed |> Seq.toList |> should equal [ "dead-token" ]

[<Fact>]
let ``Delivered and TransientFailure outcomes do NOT unsubscribe`` () =
    let subs = [
        storedSub 1 "healthy" 47.61 -122.33 10.0
        storedSub 2 "blip"    47.60 -122.34 10.0
    ]
    let mutable index = 0
    let dispatch : DevicePushDispatch.Dispatch =
        fun _ ->
            async {
                let outcome =
                    if index = 0 then DevicePushDispatch.Delivered
                    else DevicePushDispatch.TransientFailure "5xx"
                index <- index + 1
                return outcome
            }
    let mutable unsubCalls = 0
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok subs }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { unsubCalls <- unsubCalls + 1; return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    unsubCalls |> should equal 0

[<Fact>]
let ``out-of-range subscriptions are never dispatched even if Gone elsewhere`` () =
    // The far device must not be contacted at all, so it can't be cleaned
    // up by accident — proximity gates dispatch entirely.
    let far = storedSub 1 "far" 40.7128 -74.0060 5.0
    let mutable dispatchCalls = 0
    let mutable unsubCalls = 0
    let dispatch : DevicePushDispatch.Dispatch =
        fun _ -> async { dispatchCalls <- dispatchCalls + 1; return DevicePushDispatch.Gone }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ far ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { unsubCalls <- unsubCalls + 1; return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    dispatchCalls |> should equal 0
    unsubCalls |> should equal 0

[<Fact>]
let ``payload carries store id, name, and coordinates for the nav handoff`` () =
    let near = storedSub 1 "near" 47.61 -122.33 10.0
    let mutable seen : DevicePushDispatch.Payload option = None
    let dispatch : DevicePushDispatch.Dispatch =
        fun (_, payload) -> async { seen <- Some payload; return DevicePushDispatch.Delivered }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Ok [ near ] }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { return Ok () }

    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    match seen with
    | Some p ->
        p.storeId |> should equal 899
        p.storeName |> should haveSubstring "Krispy Kreme Test 899"
        p.latitude |> should equal 47.6062
        p.longitude |> should equal -122.3321
        p.title |> should haveSubstring "Krispy Kreme Test 899"
    | None -> failwith "dispatch was never called"

[<Fact>]
let ``getAll error is swallowed so a transient lookup blip doesn't crash the tick`` () =
    let dispatch : DevicePushDispatch.Dispatch =
        fun _ -> async { return failwith "must not be called when getAll fails" }
    let getAll : Ports.GetAllDevicePushSubscriptions =
        fun () -> async { return Error (UpstreamUnavailable "transient") }
    let unsubscribe : Ports.UnsubscribeDevicePush =
        fun _ -> async { return Ok () }

    // Must not throw.
    DevicePushNotify.notifyFlipOn getAll unsubscribe dispatch (StoreId 899, seattleStore)
    |> Async.RunSynchronously

[<Fact>]
let ``combine runs both callbacks in order`` () =
    let calls = ResizeArray<string>()
    let a : PushNotify.OnHotLightFlipOn = fun _ -> async { calls.Add "a" }
    let b : PushNotify.OnHotLightFlipOn = fun _ -> async { calls.Add "b" }

    DevicePushNotify.combine a b (StoreId 899, seattleStore)
    |> Async.RunSynchronously

    calls |> Seq.toList |> should equal [ "a"; "b" ]
