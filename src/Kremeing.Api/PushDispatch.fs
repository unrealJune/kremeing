namespace Kremeing.Api

open System
open System.Net.Http
open System.Text.Json
open Lib.Net.Http.WebPush
open Lib.Net.Http.WebPush.Authentication
open Kremeing.Contracts.Domain

/// Send-side of web push: converts a stored subscription + a payload
/// into a delivery via the appropriate browser push service. The
/// poller (Phase 3) calls into this on every Off→On flip; for now,
/// the seam exists and is unit-testable but isn't wired upstream yet.
module PushDispatch =

    /// VAPID identity. `Subject` is a `mailto:` URL or HTTPS URL the
    /// push service can use to contact us if a delivery looks abusive.
    /// PublicKey + PrivateKey are URL-safe-base64 EC P-256 keys.
    type Vapid = {
        Subject: string
        PublicKey: string
        PrivateKey: string
    }

    /// What we serialize into the encrypted push body. The service
    /// worker reads this back and turns it into a Notification.
    [<CLIMutable>]
    type Payload = {
        title: string
        body: string
        url: string
        storeId: int
    }

    /// One call's outcome. `Gone` triggers cleanup — the subscription
    /// is permanently dead (uninstalled PWA, cleared site data).
    /// `TransientFailure` means try again later (network, 5xx, etc.).
    type Outcome =
        | Delivered
        | Gone
        | TransientFailure of reason: string

    /// Adapter-shaped function. Production wires it to
    /// Lib.Net.Http.WebPush; tests substitute lambdas that record
    /// what they were asked to deliver.
    type Dispatch =
        StoredPushSubscription * Payload -> Async<Outcome>

    let private payloadJson (p: Payload) : string =
        JsonSerializer.Serialize(
            p,
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    /// Production dispatcher backed by Lib.Net.Http.WebPush. Holds a
    /// long-lived `PushServiceClient` (its underlying HttpClient is
    /// reused, per the library's intent).
    let create (vapid: Vapid) : Dispatch =
        let auth = VapidAuthentication(vapid.PublicKey, vapid.PrivateKey)
        auth.Subject <- vapid.Subject
        let client = new PushServiceClient()
        client.DefaultAuthentication <- auth

        fun (sub, payload) ->
            async {
                try
                    let pushSub = PushSubscription()
                    pushSub.Endpoint <- sub.Subscription.Endpoint
                    pushSub.SetKey(PushEncryptionKeyName.P256DH, sub.Subscription.P256dh)
                    pushSub.SetKey(PushEncryptionKeyName.Auth, sub.Subscription.Auth)
                    let message = PushMessage(payloadJson payload)
                    do! client.RequestPushMessageDeliveryAsync(pushSub, message)
                        |> Async.AwaitTask
                    return Delivered
                with
                | :? PushServiceClientException as ex when int ex.StatusCode = 410 ->
                    return Gone
                | :? PushServiceClientException as ex when int ex.StatusCode = 404 ->
                    // Some push services use 404 instead of 410 for dead
                    // subscriptions — same cleanup action.
                    return Gone
                | ex ->
                    return TransientFailure ex.Message
            }
