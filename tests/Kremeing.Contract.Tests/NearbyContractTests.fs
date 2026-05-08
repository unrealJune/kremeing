module Kremeing.Contract.Tests.NearbyContractTests

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

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

let private parse<'T> (response: HttpResponseMessage) : 'T =
    JsonSerializer.Deserialize<'T>(response.Content.ReadAsStringAsync().Result, jsonOptions)

let private sodoShop : LiveApi.KrispyShopDto = {
    shopId = 899
    shopName = "Seattle - 1st Ave South"
    shopUrl = "https://site.krispykreme.com/wa/seattle/1900-1st-ave-s"
    address1 = "1900 1st Ave S"
    city = "Seattle"
    state = "WA"
    zipCode = "98134"
    latitude = 47.585
    longitude = -122.334
    hotLightOn = true
    hoursDescriptionHotlight = [| "7:00 AM - 9:00 AM"; "5:00 PM - 7:00 PM" |]
    distance = 0.42
}

[<Fact>]
let ``GET /stores/nearby missing lat returns 400 with helpful error code`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/stores/nearby?lng=-122.3").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest
    let dto = parse<ErrorDto> response
    dto.error |> should equal "missing_query_param"
    dto.message |> should haveSubstring "lat"

[<Fact>]
let ``GET /stores/nearby missing lng returns 400`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/stores/nearby?lat=47.6").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``GET /stores/nearby returns enriched DTO with cached temporal fields`` () =
    // Seed an observation so the cache has a FirstObservedAt to surface.
    let observations = InMemoryObservations.create()
    let observedAt = DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero)
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = observedAt }
        |> Async.RunSynchronously

    let search : HttpHandlers.SearchNearby =
        fun _ -> async { return Ok [ sodoShop ] }

    let deps = {
        (TestHost.Stubs.deps()) with
            SearchNearby = search
            Status = observations.Status
    }
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/nearby?lat=47.6&lng=-122.3").Result
    response.StatusCode |> should equal HttpStatusCode.OK

    let body = parse<NearbyResponseDto> response
    body.stores.Length |> should equal 1
    let s = body.stores.[0]
    s.id |> should equal 899
    s.currentStatus |> should equal "on"
    s.firstObservedAt.HasValue |> should equal true
    s.firstObservedAt.Value |> should equal observedAt
    // Single observation → no flip yet
    s.lastFlippedAt.HasValue |> should equal false

[<Fact>]
let ``GET /stores/nearby returns null temporal fields when store has no history`` () =
    let search : HttpHandlers.SearchNearby =
        fun _ -> async { return Ok [ sodoShop ] }
    let deps = { (TestHost.Stubs.deps()) with SearchNearby = search }
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/nearby?lat=47.6&lng=-122.3").Result
    let body = parse<NearbyResponseDto> response
    let s = body.stores.[0]
    s.lastFlippedAt.HasValue |> should equal false
    s.firstObservedAt.HasValue |> should equal false
    // currentStatus still comes through from upstream
    s.currentStatus |> should equal "on"

[<Fact>]
let ``GET /stores/nearby?limit=N truncates response`` () =
    let shops = [
        for id in 100..120 -> { sodoShop with shopId = id }
    ]
    let search : HttpHandlers.SearchNearby =
        fun _ -> async { return Ok shops }
    let deps = { (TestHost.Stubs.deps()) with SearchNearby = search }
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/nearby?lat=47.6&lng=-122.3&limit=5").Result
    let body = parse<NearbyResponseDto> response
    body.stores.Length |> should equal 5

[<Fact>]
let ``GET /stores/nearby returns 502 when upstream fails`` () =
    let search : HttpHandlers.SearchNearby =
        fun _ -> async { return Error (UpstreamUnavailable "krispykreme.com timed out") }
    let deps = { (TestHost.Stubs.deps()) with SearchNearby = search }
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/nearby?lat=47.6&lng=-122.3").Result
    response.StatusCode |> should equal HttpStatusCode.BadGateway
    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.UpstreamUnavailable
