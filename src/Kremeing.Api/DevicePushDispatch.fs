namespace Kremeing.Api

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open Kremeing.Contracts.Domain

/// Send-side of native push: turns a stored device subscription + a
/// payload into a delivery via Firebase Cloud Messaging (HTTP v1). Web
/// push (`PushDispatch`) can't reach a native Android app, so Android
/// Auto needs this parallel transport. Message construction is a pure,
/// unit-tested function; the network call is the only impure part.
module DevicePushDispatch =

    /// FCM project identity + a token source. `AccessToken` returns a
    /// fresh OAuth2 bearer for the FCM v1 API (caller decides how it's
    /// minted/cached — service account, sidecar, etc.), keeping this
    /// module free of Google-auth specifics and easy to substitute.
    type Fcm = {
        ProjectId: string
        AccessToken: unit -> Async<string>
    }

    /// What the device receives. Sent as an FCM *data* message so the
    /// app builds the Android Auto notification itself (consistent UX
    /// whether the app is foreground, background, or in the car).
    [<CLIMutable>]
    type Payload = {
        title: string
        body: string
        storeId: int
        storeName: string
        latitude: float
        longitude: float
    }

    /// One call's outcome. `Gone` triggers cleanup — FCM told us the
    /// token is permanently invalid (app uninstalled, token rotated).
    /// `TransientFailure` means try again on the next flip.
    type Outcome =
        | Delivered
        | Gone
        | TransientFailure of reason: string

    /// Adapter-shaped function. Production wires it to FCM; tests
    /// substitute lambdas that record what they were asked to deliver.
    type Dispatch =
        StoredDevicePushSubscription * Payload -> Async<Outcome>

    /// Build the FCM HTTP v1 request body for one token. Data values
    /// must be strings (FCM requirement), so numbers are stringified.
    /// Pure — the contract the Android client parses is pinned by tests.
    let buildMessageJson (token: string) (p: Payload) : string =
        let message =
            {| message =
                {| token = token
                   data =
                    {| title = p.title
                       body = p.body
                       storeId = string p.storeId
                       storeName = p.storeName
                       latitude = p.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                       longitude = p.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) |}
                   android = {| priority = "high" |} |} |}
        JsonSerializer.Serialize message

    /// Production dispatcher backed by FCM HTTP v1. Reuses the supplied
    /// `HttpClient`. 404 (`UNREGISTERED`) and 410 map to `Gone`; other
    /// non-success responses are transient.
    let create (fcm: Fcm) (http: HttpClient) : Dispatch =
        let endpoint =
            sprintf "https://fcm.googleapis.com/v1/projects/%s/messages:send" fcm.ProjectId
        fun (sub, payload) ->
            async {
                try
                    let! token = fcm.AccessToken ()
                    let json = buildMessageJson sub.Registration.Token payload
                    use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                    let! resp = http.SendAsync req |> Async.AwaitTask
                    if resp.IsSuccessStatusCode then
                        return Delivered
                    elif resp.StatusCode = HttpStatusCode.NotFound
                         || resp.StatusCode = HttpStatusCode.Gone then
                        return Gone
                    else
                        let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                        return TransientFailure (sprintf "FCM %d: %s" (int resp.StatusCode) body)
                with ex ->
                    return TransientFailure ex.Message
            }
