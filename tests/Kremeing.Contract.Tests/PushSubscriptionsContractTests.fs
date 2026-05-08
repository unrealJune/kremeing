module Kremeing.Contract.Tests.PushSubscriptionsContractTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Core
open Kremeing.Api

// These pin the wire shape that the service worker (Phase 4) and the
// in-browser pushManager flow will speak. The endpoints have two
// modes:
//   - Push disabled (no Postgres + VAPID env): every endpoint 503s
//     with push_disabled. This is what clients see in dev when the
//     operator hasn't configured the feature.
//   - Push enabled: subscribe writes a row, unsubscribe removes one,
//     /vapid-public-key returns the configured key.

let private camelCase =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    o.PropertyNameCaseInsensitive <- true
    o

let private parse<'T> (response: HttpResponseMessage) : 'T =
    JsonSerializer.Deserialize<'T>(response.Content.ReadAsStringAsync().Result, camelCase)

let private jsonContent (value: obj) : HttpContent =
    let body = JsonSerializer.Serialize(value, camelCase)
    new StringContent(body, Encoding.UTF8, "application/json")

let private makePushDeps () =
    let mutable subscribed : ResizeArray<int * PushSubscription> = ResizeArray()
    let mutable unsubscribed : ResizeArray<int * string> = ResizeArray()
    let subscribe : Ports.SubscribePush =
        fun (StoreId sid, sub) ->
            async {
                subscribed.Add (sid, sub)
                return Ok (PushSubscriptionId (int64 (subscribed.Count + 1000)))
            }
    let unsubscribe : Ports.UnsubscribePush =
        fun (StoreId sid, endpoint) ->
            async {
                unsubscribed.Add (sid, endpoint)
                return Ok ()
            }
    let deps : HttpHandlers.PushDeps = {
        Subscribe = subscribe
        Unsubscribe = unsubscribe
        VapidPublicKey = "BTestVapidPublicKey_AAA"
    }
    deps, subscribed, unsubscribed

// ──── push disabled (default) ────────────────────────────────────────

[<Fact>]
let ``GET /vapid-public-key returns 503 push_disabled when not configured`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let r = client.GetAsync("/vapid-public-key").Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 503)
    (parse<ErrorDto> r).error |> should equal ErrorCodes.PushDisabled

[<Fact>]
let ``POST /subscriptions returns 503 when not configured`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let body = jsonContent {|
        storeId = 899
        subscription = {|
            endpoint = "https://fcm.googleapis.com/fcm/send/x"
            keys = {| p256dh = "p"; auth = "a" |}
        |}
    |}
    let r = client.PostAsync("/subscriptions", body).Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 503)
    (parse<ErrorDto> r).error |> should equal ErrorCodes.PushDisabled

[<Fact>]
let ``DELETE /subscriptions returns 503 when not configured`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let req = new HttpRequestMessage(HttpMethod.Delete, "/subscriptions")
    req.Content <- jsonContent {| storeId = 899; endpoint = "https://x" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 503)

// ──── push enabled ───────────────────────────────────────────────────

[<Fact>]
let ``GET /vapid-public-key returns the configured key`` () =
    let push, _, _ = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps

    let r = client.GetAsync("/vapid-public-key").Result
    r.StatusCode |> should equal HttpStatusCode.OK
    let body = parse<VapidPublicKeyResponseDto> r
    body.publicKey |> should equal "BTestVapidPublicKey_AAA"

[<Fact>]
let ``POST /subscriptions accepts a valid pushManager payload and returns 201`` () =
    let push, subscribed, _ = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps

    let body = jsonContent {|
        storeId = 899
        subscription = {|
            endpoint = "https://fcm.googleapis.com/fcm/send/abc"
            keys = {| p256dh = "p256-key"; auth = "auth-secret" |}
        |}
    |}
    let r = client.PostAsync("/subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.Created
    let res = parse<SubscribeResponseDto> r
    res.storeId |> should equal 899
    res.id |> should be (greaterThan 0L)

    subscribed.Count |> should equal 1
    let (sid, sub) = subscribed.[0]
    sid |> should equal 899
    sub.Endpoint |> should equal "https://fcm.googleapis.com/fcm/send/abc"
    sub.P256dh   |> should equal "p256-key"
    sub.Auth     |> should equal "auth-secret"

[<Fact>]
let ``POST /subscriptions rejects missing fields with invalid_subscription`` () =
    let push, _, _ = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps

    // Missing endpoint
    let body = jsonContent {|
        storeId = 899
        subscription = {| endpoint = ""; keys = {| p256dh = "p"; auth = "a" |} |}
    |}
    let r = client.PostAsync("/subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    (parse<ErrorDto> r).error |> should equal ErrorCodes.InvalidSubscription

[<Fact>]
let ``POST /subscriptions rejects non-positive storeId`` () =
    let push, _, _ = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps
    let body = jsonContent {|
        storeId = 0
        subscription = {|
            endpoint = "https://x/"
            keys = {| p256dh = "p"; auth = "a" |}
        |}
    |}
    let r = client.PostAsync("/subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``DELETE /subscriptions returns 204 and forwards to the port`` () =
    let push, _, unsubscribed = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps

    let req = new HttpRequestMessage(HttpMethod.Delete, "/subscriptions")
    req.Content <- jsonContent {|
        storeId = 899
        endpoint = "https://fcm.googleapis.com/fcm/send/abc"
    |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal HttpStatusCode.NoContent

    unsubscribed.Count |> should equal 1
    let (sid, endpoint) = unsubscribed.[0]
    sid |> should equal 899
    endpoint |> should equal "https://fcm.googleapis.com/fcm/send/abc"

[<Fact>]
let ``DELETE /subscriptions rejects missing endpoint`` () =
    let push, _, _ = makePushDeps ()
    let deps = { (TestHost.Stubs.deps()) with Push = Some push }
    use client = TestHost.start deps

    let req = new HttpRequestMessage(HttpMethod.Delete, "/subscriptions")
    req.Content <- jsonContent {| storeId = 899; endpoint = "" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
