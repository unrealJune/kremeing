// Manually fire a Web Push to a subscription. Useful for verifying
// VAPID config + service-worker delivery without waiting for a real
// store flip.
//
//   export KREMEING_VAPID_PUBLIC_KEY=...
//   export KREMEING_VAPID_PRIVATE_KEY=...
//   export KREMEING_VAPID_SUBJECT='mailto:you@example.com'
//
//   # Pipe in the subscription JSON (the exact body the browser
//   # POSTs to /subscriptions, OR the toJSON() output from
//   # PushManager.subscribe — the script accepts both shapes).
//   cat subscription.json | dotnet fsi scripts/send-test-push.fsx
//
//   # Or pass a custom title/body:
//   cat subscription.json | dotnet fsi scripts/send-test-push.fsx \
//       "🔥 SODO" "Hot doughnuts ready now"

#r "nuget: Lib.Net.Http.WebPush, 3.3.1"

open System
open System.IO
open System.Text.Json
open Lib.Net.Http.WebPush
open Lib.Net.Http.WebPush.Authentication

let envOrFail name =
    match Environment.GetEnvironmentVariable(name: string) with
    | null | "" -> failwithf "%s not set" name
    | v -> v

let publicKey  = envOrFail "KREMEING_VAPID_PUBLIC_KEY"
let privateKey = envOrFail "KREMEING_VAPID_PRIVATE_KEY"
let subject    =
    match Environment.GetEnvironmentVariable "KREMEING_VAPID_SUBJECT" with
    | null | "" -> "mailto:hotlight@kremeing.invalid"
    | v -> v

let argv = fsi.CommandLineArgs |> Array.skip 1
let title = if argv.Length > 0 then argv.[0] else "🔥 kremeing test"
let body  = if argv.Length > 1 then argv.[1] else "Push delivery works!"

// Read subscription JSON from stdin. Accepts either:
//   { "endpoint": ..., "keys": { "p256dh": ..., "auth": ... } }            ← browser PushSubscription.toJSON()
//   { "subscription": { "endpoint": ..., "keys": {...} }, "storeId": ... } ← /subscriptions request body
let raw = Console.In.ReadToEnd().Trim()
if String.IsNullOrEmpty raw then
    failwith "no subscription JSON on stdin"

let doc = JsonDocument.Parse raw
let root = doc.RootElement
let subElement =
    match root.TryGetProperty "subscription" with
    | true, sub -> sub
    | false, _ -> root
let endpoint = subElement.GetProperty("endpoint").GetString()
let keys = subElement.GetProperty("keys")
let p256dh = keys.GetProperty("p256dh").GetString()
let auth = keys.GetProperty("auth").GetString()
let storeId =
    match root.TryGetProperty "storeId" with
    | true, s -> s.GetInt32()
    | false, _ -> 0

printfn "Sending push to %s" (endpoint.Substring(0, min 50 endpoint.Length) + "…")

let vapid = VapidAuthentication(publicKey, privateKey)
vapid.Subject <- subject

let client = new PushServiceClient()
client.DefaultAuthentication <- vapid

let sub = PushSubscription()
sub.Endpoint <- endpoint
sub.SetKey(PushEncryptionKeyName.P256DH, p256dh)
sub.SetKey(PushEncryptionKeyName.Auth, auth)

let payload =
    {| title = title
       body = body
       url = if storeId > 0 then sprintf "/?store=%d" storeId else "/"
       storeId = storeId |}
let json =
    JsonSerializer.Serialize(
        payload,
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
let message = PushMessage(json)

try
    client.RequestPushMessageDeliveryAsync(sub, message).GetAwaiter().GetResult()
    printfn "✓ Push delivered."
with
| :? PushServiceClientException as ex ->
    printfn "✗ Push failed: HTTP %d %s" (int ex.StatusCode) ex.Message
    if int ex.StatusCode = 410 then
        printfn "  (subscription is dead — caller would normally call DeleteByEndpoint)"
    exit 1
| ex ->
    printfn "✗ %s" ex.Message
    exit 1
