module Kremeing.Contract.Tests.ViewportContractTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Api

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

let private parse<'T> (response: HttpResponseMessage) : 'T =
    JsonSerializer.Deserialize<'T>(response.Content.ReadAsStringAsync().Result, jsonOptions)

let private entry id lat lng : Discovery.RegistryEntry = {
    ShopId = id
    Name = sprintf "Krispy Kreme Store %d" id
    Address = sprintf "%d Doughnut Way" id
    Location = { Latitude = lat; Longitude = lng }
    ShopUrl = sprintf "https://example.com/%d" id
    SearchKey = "Seattle, WA"
}

let private status id current lastFlipped firstObserved : StoreMapStatus = {
    StoreId = StoreId id
    CurrentStatus = current
    LastFlippedAt = lastFlipped
    FirstObservedAt = firstObserved
}

[<Fact>]
let ``GET /stores/viewport requires all four bounds`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response =
        client.GetAsync("/stores/viewport?north=48&south=47&east=-122").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest
    let dto = parse<ErrorDto> response
    dto.error |> should equal "missing_query_param"
    dto.message |> should haveSubstring "west"

[<Fact>]
let ``GET /stores/viewport returns every visible registry store with current status`` () =
    let stores = [
        for id in 1..25 do
            entry id (47.60 + float id / 10_000.0) -122.33
        entry 999 45.52 -122.68
    ]
    let mutable requestedHistory = true
    let mutable requestedIds = []
    let mapStatuses includeHistoryAndIds =
        let includeHistory, ids = includeHistoryAndIds
        requestedHistory <- includeHistory
        requestedIds <- ids
        async {
            return
                ids
                |> List.map (fun (StoreId id) ->
                    status id (if id % 2 = 0 then On else Off) None None)
                |> Ok
        }
    let deps = {
        (TestHost.Stubs.deps()) with
            ListStores = fun () -> stores
            MapStatuses = mapStatuses
    }
    use client = TestHost.start deps

    let response =
        client.GetAsync(
            "/stores/viewport?north=48&south=47&east=-122&west=-123").Result
    response.StatusCode |> should equal HttpStatusCode.OK
    let body = parse<ViewportResponseDto> response

    body.stores.Length |> should equal 25
    requestedIds.Length |> should equal 25
    requestedHistory |> should equal false
    body.stores |> Array.filter (fun store -> store.currentStatus = "on")
                |> Array.length
                |> should equal 12

[<Fact>]
let ``GET /stores/viewport only includes temporal context at detail zoom`` () =
    let firstObserved = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let lastFlipped = DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
    let mutable requestedHistory = false
    let deps = {
        (TestHost.Stubs.deps()) with
            ListStores = fun () -> [ entry 899 47.585 -122.334 ]
            MapStatuses =
                fun (includeHistory, _) ->
                    requestedHistory <- includeHistory
                    async {
                        return Ok [
                            status 899 On (Some lastFlipped) (Some firstObserved)
                        ]
                    }
    }
    use client = TestHost.start deps

    let response =
        client.GetAsync(
            "/stores/viewport?north=48&south=47&east=-122&west=-123&includeHistory=true").Result
    let body = parse<ViewportResponseDto> response
    let store = body.stores.[0]

    requestedHistory |> should equal true
    store.lastFlippedAt.HasValue |> should equal true
    store.lastFlippedAt.Value |> should equal lastFlipped
    store.firstObservedAt.HasValue |> should equal true
    store.firstObservedAt.Value |> should equal firstObserved

[<Fact>]
let ``GET /stores/viewport supports bounds crossing the antimeridian`` () =
    let deps = {
        (TestHost.Stubs.deps()) with
            ListStores =
                fun () -> [
                    entry 1 10.0 179.0
                    entry 2 10.0 -179.0
                    entry 3 10.0 0.0
                ]
    }
    use client = TestHost.start deps

    let response =
        client.GetAsync(
            "/stores/viewport?north=20&south=0&east=-170&west=170").Result
    let body = parse<ViewportResponseDto> response

    body.stores |> Array.map (fun store -> store.id) |> Set.ofArray
                |> should equal (set [ 1; 2 ])
