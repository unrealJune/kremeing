namespace Kremeing.Api

open Kremeing.Contracts.Domain
open Kremeing.Core

/// Fan-out side of push: given a store flip-on, look up all
/// subscriptions for that store, dispatch a push to each, and clean
/// up dead endpoints (`Gone` outcome). Pure plumbing — every input
/// is a port function so the composition is trivial to unit-test.
module PushNotify =

    /// Callback the poller invokes whenever a store flips off→on.
    /// Producing the payload + sending pushes happens inside; the
    /// poller doesn't need to know any push-specific details.
    type OnHotLightFlipOn =
        StoreId * Discovery.RegistryEntry -> Async<unit>

    /// No-op implementation for when push is disabled (no Postgres or
    /// no VAPID env). The poller still runs, just doesn't notify.
    let noop : OnHotLightFlipOn =
        fun _ -> async { () }

    /// Production wiring: fetches subscriptions, dispatches pushes,
    /// cleans up Gone endpoints. The URL we put in the payload is
    /// relative — the service worker resolves it against its own
    /// origin, so the same payload works whether the user installed
    /// from kremeing.junephilip.com or some other deployment.
    let notifyFlipOn
            (find: Ports.FindPushSubscriptionsForStore)
            (deleteByEndpoint: Ports.DeletePushSubscriptionsByEndpoint)
            (dispatch: PushDispatch.Dispatch)
            : OnHotLightFlipOn =
        fun (storeId, entry) ->
            async {
                let (StoreId sid) = storeId
                match! find storeId with
                | Error _ ->
                    // Transient lookup failure — better to skip the
                    // notify than to crash the tick. Subscriptions are
                    // best-effort.
                    return ()
                | Ok subs ->
                    let payload : PushDispatch.Payload = {
                        title = sprintf "🔥 %s" entry.Name
                        body = "Hot doughnuts ready now"
                        url =
                            sprintf "/?store=%d&lat=%.4f&lng=%.4f"
                                sid
                                entry.Location.Latitude
                                entry.Location.Longitude
                        storeId = sid
                    }
                    for sub in subs do
                        let! outcome = dispatch (sub, payload)
                        match outcome with
                        | PushDispatch.Gone ->
                            // The push service told us the device is
                            // dead (uninstalled PWA, cleared site data,
                            // etc.). Remove this endpoint from every
                            // store it had subscribed to.
                            let! _ = deleteByEndpoint sub.Subscription.Endpoint
                            ()
                        | PushDispatch.Delivered
                        | PushDispatch.TransientFailure _ ->
                            // Transient failures are logged elsewhere
                            // and retried on the next flip; we don't
                            // delete the row.
                            ()
            }
