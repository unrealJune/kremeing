module Kremeing.Contract.Tests.DeviceSubscriptionsContractTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Core
open Kremeing.Api

// Pins the wire shape of /device-subscriptions — the endpoints the
// Android Auto companion app calls to register/unregister for nearby
// hot-light alerts. Two modes:
//   - Native push disabled (no FCM config): every endpoint 503s.
//   - Enabled: POST registers (201), DELETE removes (204), bad input 400.

let private camelCase =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    o.PropertyNameCaseInsensitive <- true
    o

let private parse<'T> (response: HttpResponseMessage) : 'T =
    JsonSerializer.Deserialize<'T>(response.Content.ReadAsStringAsync().Result, camelCase)

let private jsonContent (value: obj) : HttpContent =
    let body = JsonSerializer.Serialize(value, camelCase)
    new StringContent(body, Encoding.UTF8, "application/json")

/// Stub device-push deps backed by an in-memory store, so the contract
/// layer is exercised end to end against a real (if volatile) backend.
let private makeDeviceDeps () =
    let store = InMemoryDeviceSubscriptions.create ()
    let deps : HttpHandlers.DevicePushDeps = {
        Subscribe = store.Subscribe
        Unsubscribe = store.Unsubscribe
    }
    deps, store

// ──── native push disabled (default) ─────────────────────────────────

[<Fact>]
let ``POST /device-subscriptions returns 503 when not configured`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let body = jsonContent {|
        token = "fcm-abc"; platform = "android"
        latitude = 47.6; longitude = -122.3; radiusMiles = 25.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 503)
    (parse<ErrorDto> r).error |> should equal ErrorCodes.PushDisabled

[<Fact>]
let ``DELETE /device-subscriptions returns 503 when not configured`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let req = new HttpRequestMessage(HttpMethod.Delete, "/device-subscriptions")
    req.Content <- jsonContent {| token = "fcm-abc" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 503)

// ──── native push enabled ────────────────────────────────────────────

[<Fact>]
let ``POST /device-subscriptions accepts a valid registration and returns 201`` () =
    let device, store = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps

    let body = jsonContent {|
        token = "fcm-token-abc"; platform = "android"
        latitude = 47.6062; longitude = -122.3321; radiusMiles = 25.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.Created
    let res = parse<DeviceSubscribeResponseDto> r
    res.id |> should be (greaterThan 0L)
    res.radiusMiles |> should equal 25.0

    // Round-trips into the store.
    let all = store.GetAll () |> Async.RunSynchronously
    match all with
    | Ok subs ->
        subs |> List.length |> should equal 1
        subs.[0].Registration.Token |> should equal "fcm-token-abc"
    | Error e -> failwithf "store error: %A" e

[<Fact>]
let ``POST /device-subscriptions is idempotent on the token`` () =
    let device, store = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps

    let post lat radius =
        let body = jsonContent {|
            token = "same-token"; platform = "android"
            latitude = lat; longitude = -122.3; radiusMiles = radius
        |}
        client.PostAsync("/device-subscriptions", body).Result.StatusCode
        |> should equal HttpStatusCode.Created
    post 47.6 25.0
    post 40.7 50.0   // same token, moved + widened

    match store.GetAll () |> Async.RunSynchronously with
    | Ok subs ->
        subs |> List.length |> should equal 1
        subs.[0].Registration.RadiusMiles |> should equal 50.0
    | Error e -> failwithf "store error: %A" e

[<Fact>]
let ``POST /device-subscriptions rejects a missing token with invalid_device_subscription`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let body = jsonContent {|
        token = ""; platform = "android"
        latitude = 47.6; longitude = -122.3; radiusMiles = 25.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    (parse<ErrorDto> r).error |> should equal ErrorCodes.InvalidDeviceSubscription

[<Fact>]
let ``POST /device-subscriptions rejects an unknown platform`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let body = jsonContent {|
        token = "t"; platform = "ios"
        latitude = 47.6; longitude = -122.3; radiusMiles = 25.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    (parse<ErrorDto> r).error |> should equal ErrorCodes.InvalidDeviceSubscription

[<Fact>]
let ``POST /device-subscriptions rejects an out-of-range radius`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let body = jsonContent {|
        token = "t"; platform = "android"
        latitude = 47.6; longitude = -122.3; radiusMiles = 9999.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``POST /device-subscriptions rejects an out-of-range coordinate`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let body = jsonContent {|
        token = "t"; platform = "android"
        latitude = 200.0; longitude = -122.3; radiusMiles = 25.0
    |}
    let r = client.PostAsync("/device-subscriptions", body).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``DELETE /device-subscriptions returns 204 and removes the token`` () =
    let device, store = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps

    // Register first.
    let body = jsonContent {|
        token = "fcm-token-abc"; platform = "android"
        latitude = 47.6; longitude = -122.3; radiusMiles = 25.0
    |}
    client.PostAsync("/device-subscriptions", body).Result.StatusCode
    |> should equal HttpStatusCode.Created

    let req = new HttpRequestMessage(HttpMethod.Delete, "/device-subscriptions")
    req.Content <- jsonContent {| token = "fcm-token-abc" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal HttpStatusCode.NoContent

    match store.GetAll () |> Async.RunSynchronously with
    | Ok subs -> subs |> should be Empty
    | Error e -> failwithf "store error: %A" e

[<Fact>]
let ``DELETE /device-subscriptions of an unknown token still returns 204`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let req = new HttpRequestMessage(HttpMethod.Delete, "/device-subscriptions")
    req.Content <- jsonContent {| token = "never-registered" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal HttpStatusCode.NoContent

[<Fact>]
let ``DELETE /device-subscriptions rejects a missing token with 400`` () =
    let device, _ = makeDeviceDeps ()
    let deps = { (TestHost.Stubs.deps()) with DevicePush = Some device }
    use client = TestHost.start deps
    let req = new HttpRequestMessage(HttpMethod.Delete, "/device-subscriptions")
    req.Content <- jsonContent {| token = "" |}
    let r = client.SendAsync(req).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
