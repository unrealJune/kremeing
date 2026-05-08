module Kremeing.Contract.Tests.SecurityContractTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Core
open Kremeing.Api

// These are the audit-driven hardening guarantees: rate limit, range
// bound, coordinate validation, and read-through caching. They lock in
// the v0.2.0 abuse-prevention contract — regressing any of these is
// what would make the API unsafe to expose publicly.

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
let private parse<'T> (response: HttpResponseMessage) : 'T =
    JsonSerializer.Deserialize<'T>(response.Content.ReadAsStringAsync().Result, jsonOptions)

let private fixedNow = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)

let private sodoShop : LiveApi.KrispyShopDto = {
    shopId = 899
    shopName = "Seattle - 1st Ave South"
    shopUrl = ""
    address1 = "1900 1st Ave S"
    city = "Seattle"
    state = "WA"
    zipCode = "98134"
    latitude = 47.585
    longitude = -122.334
    hotLightOn = true
    hoursDescriptionHotlight = [||]
    distance = 0.42
}

// ──── rate limit ────────────────────────────────────────────────────────

[<Fact>]
let ``proxy rate limit: capacity allowed, capacity+1 returns 429 with Retry-After`` () =
    // Tiny limiter so we don't have to hit the API 100× per test.
    let mutable upstreamCalls = 0
    let search : HttpHandlers.SearchNearby =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&upstreamCalls) |> ignore
                return Ok [ { sodoShop with shopId = 100 + upstreamCalls } ]
            }
    let deps =
        { (TestHost.Stubs.deps()) with
            SearchNearby = search
            // Refill barely-anything per second so the next request
            // after the burst is still rejected.
            ProxyRateLimit = RateLimit.Limiter(capacity = 3.0, refillPerSecond = 0.0001) }
    use client = TestHost.start deps

    // 3 successes (different lat/lng → cache misses, all hit upstream)
    for i in 0 .. 2 do
        let url = sprintf "/stores/nearby?lat=47.%d&lng=-122.3" i
        let r = client.GetAsync(url).Result
        r.StatusCode |> should equal HttpStatusCode.OK

    // 4th request → 429 with Retry-After
    let r = client.GetAsync("/stores/nearby?lat=47.99&lng=-122.99").Result
    r.StatusCode |> should equal (enum<HttpStatusCode> 429)
    let retryAfter = r.Headers.GetValues "Retry-After" |> Seq.head
    retryAfter |> should equal "60"
    let body = parse<ErrorDto> r
    body.error |> should equal ErrorCodes.RateLimited

[<Fact>]
let ``rate limit applies to /stores/{id}/hot-light too`` () =
    let stub = TestHost.Stubs.alwaysReturns
                { StoreId = StoreId 899; Status = On; ObservedAt = fixedNow }
    let deps =
        { (TestHost.Stubs.deps()) with
            GetHotLightStatus = stub
            ProxyRateLimit = RateLimit.Limiter(capacity = 1.0, refillPerSecond = 0.0001) }
    use client = TestHost.start deps

    // First request consumes the only token
    let r1 = client.GetAsync("/stores/899/hot-light").Result
    r1.StatusCode |> should equal HttpStatusCode.OK

    // Second request rejected
    let r2 = client.GetAsync("/stores/899/hot-light").Result
    r2.StatusCode |> should equal (enum<HttpStatusCode> 429)

[<Fact>]
let ``rate limit does NOT apply to /health`` () =
    let deps =
        { (TestHost.Stubs.deps()) with
            ProxyRateLimit = RateLimit.Limiter(capacity = 1.0, refillPerSecond = 0.0001) }
    use client = TestHost.start deps
    // Hammer /health past the proxy limiter's capacity
    for _ in 1 .. 10 do
        let r = client.GetAsync("/health").Result
        r.StatusCode |> should equal HttpStatusCode.OK

// ──── caching ───────────────────────────────────────────────────────────

[<Fact>]
let ``read-through cache collapses identical /hot-light calls into one upstream`` () =
    let mutable calls = 0
    let stub : Ports.GetHotLightStatus =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&calls) |> ignore
                return Ok { StoreId = StoreId 899; Status = On; ObservedAt = fixedNow }
            }
    let deps = { (TestHost.Stubs.deps()) with GetHotLightStatus = stub }
    use client = TestHost.start deps
    for _ in 1 .. 5 do
        let r = client.GetAsync("/stores/899/hot-light").Result
        r.StatusCode |> should equal HttpStatusCode.OK
    calls |> should equal 1

[<Fact>]
let ``read-through cache collapses /nearby calls within ~1km of each other`` () =
    let mutable calls = 0
    let search : HttpHandlers.SearchNearby =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&calls) |> ignore
                return Ok [ sodoShop ]
            }
    let deps = { (TestHost.Stubs.deps()) with SearchNearby = search }
    use client = TestHost.start deps

    // Coordinates differ by <0.005 → quantize to the same key.
    let _ = client.GetAsync("/stores/nearby?lat=47.6062&lng=-122.3321").Result
    let _ = client.GetAsync("/stores/nearby?lat=47.6065&lng=-122.3325").Result
    calls |> should equal 1

// ──── range bounds ──────────────────────────────────────────────────────

[<Fact>]
let ``/history rejects ranges wider than 90 days with range_too_wide`` () =
    let observations = InMemoryObservations.create()
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = fixedNow }
        |> Async.RunSynchronously
    let deps =
        { (TestHost.Stubs.deps()) with
            History = observations.History
            Status = observations.Status
            Now = fun () -> fixedNow }
    use client = TestHost.start deps

    let since = fixedNow.AddDays -91.0
    let url = sprintf "/stores/899/history?since=%s&until=%s"
                (Uri.EscapeDataString (since.ToString "o"))
                (Uri.EscapeDataString (fixedNow.ToString "o"))
    let r = client.GetAsync(url).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    let body = parse<ErrorDto> r
    body.error |> should equal ErrorCodes.RangeTooWide

[<Fact>]
let ``/uptime rejects ranges wider than 90 days too`` () =
    let observations = InMemoryObservations.create()
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = fixedNow }
        |> Async.RunSynchronously
    let deps =
        { (TestHost.Stubs.deps()) with
            History = observations.History
            Status = observations.Status
            Now = fun () -> fixedNow }
    use client = TestHost.start deps

    let since = fixedNow.AddDays -100.0
    let url = sprintf "/stores/899/uptime?bucket=day&since=%s&until=%s"
                (Uri.EscapeDataString (since.ToString "o"))
                (Uri.EscapeDataString (fixedNow.ToString "o"))
    let r = client.GetAsync(url).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    let body = parse<ErrorDto> r
    body.error |> should equal ErrorCodes.RangeTooWide

// ──── coordinate validation ─────────────────────────────────────────────

[<Theory>]
[<InlineData("NaN", "-122.3")>]
[<InlineData("Infinity", "-122.3")>]
[<InlineData("91", "-122.3")>]      // out of [-90, 90]
[<InlineData("47.6", "200")>]       // out of [-180, 180]
let ``/nearby rejects garbage coordinates with invalid_coordinate``
        (lat: string) (lng: string) =
    use client = TestHost.start (TestHost.Stubs.deps())
    let r = client.GetAsync(sprintf "/stores/nearby?lat=%s&lng=%s" lat lng).Result
    r.StatusCode |> should equal HttpStatusCode.BadRequest
    let body = parse<ErrorDto> r
    body.error |> should equal "invalid_coordinate"
