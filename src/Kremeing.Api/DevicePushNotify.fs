namespace Kremeing.Api

open Kremeing.Contracts.Domain
open Kremeing.Core

/// Fan-out side of native (device) push. Given a store flip-on, find
/// every device subscription whose (location, radius) contains the
/// flipped store and send each an FCM message; clean up tokens FCM
/// reports as `Gone`. Mirrors `PushNotify` but keys on proximity rather
/// than a per-store subscription row, because native subscribers ask
/// about "lit stores near me", not one specific shopId.
module DevicePushNotify =

    /// No-op for when device push is disabled (no FCM config). The
    /// poller stays push-agnostic and just doesn't notify devices.
    let noop : PushNotify.OnHotLightFlipOn =
        fun _ -> async { () }

    /// Production wiring: fetch all device subscriptions, dispatch to the
    /// ones within range of the flipped store, and unsubscribe any token
    /// FCM reports dead. A transient lookup failure is swallowed so it
    /// can't crash the poller tick — device pushes are best-effort.
    let notifyFlipOn
            (getAll: Ports.GetAllDevicePushSubscriptions)
            (unsubscribe: Ports.UnsubscribeDevicePush)
            (dispatch: DevicePushDispatch.Dispatch)
            : PushNotify.OnHotLightFlipOn =
        fun (storeId, entry) ->
            async {
                match! getAll () with
                | Error _ -> return ()
                | Ok subs ->
                    let (StoreId sid) = storeId
                    let payload : DevicePushDispatch.Payload = {
                        title = sprintf "🔥 %s" entry.Name
                        body = "Hot doughnuts ready now"
                        storeId = sid
                        storeName = entry.Name
                        latitude = entry.Location.Latitude
                        longitude = entry.Location.Longitude
                    }
                    for sub in subs do
                        let inRange =
                            Geo.withinRadius
                                sub.Registration.Location
                                sub.Registration.RadiusMiles
                                entry.Location
                        if inRange then
                            let! outcome = dispatch (sub, payload)
                            match outcome with
                            | DevicePushDispatch.Gone ->
                                // Token permanently invalid (app removed,
                                // token rotated). Drop it from the store.
                                let! _ = unsubscribe sub.Registration.Token
                                ()
                            | DevicePushDispatch.Delivered
                            | DevicePushDispatch.TransientFailure _ ->
                                ()
            }

    /// Sequentially run two flip-on callbacks as one. Lets the composition
    /// root drive web push and device push from the poller's single
    /// `notifyFlipOn` hook without the poller knowing either exists.
    let combine
            (first: PushNotify.OnHotLightFlipOn)
            (second: PushNotify.OnHotLightFlipOn)
            : PushNotify.OnHotLightFlipOn =
        fun arg ->
            async {
                do! first arg
                do! second arg
            }
