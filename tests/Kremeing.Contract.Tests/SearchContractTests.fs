module Kremeing.Contract.Tests.SearchContractTests

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

let private sampleShop : LiveApi.KrispyShopDto = {
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
    distance = 0.32
}

[<Fact>]
let ``GET /stores/search?q=98109 returns matching stores`` () =
    let stub : HttpHandlers.SearchByQuery =
        fun _ -> async { return Ok [ sampleShop ] }
    let deps = { (TestHost.Stubs.deps()) with SearchByQuery = stub }
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/search?q=98109").Result
    response.StatusCode |> should equal HttpStatusCode.OK

    let body = parse<SearchResponseDto> response
    body.query.q |> should equal "98109"
    body.stores.Length |> should equal 1
    body.stores.[0].id |> should equal 899

[<Fact>]
let ``GET /stores/search without q returns 400 missing_query_param`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/stores/search").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest
    (parse<ErrorDto> response).error |> should equal "missing_query_param"

[<Fact>]
let ``GET /stores/search?q=  (whitespace) is treated as missing`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/stores/search?q=%20%20").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``GET /stores/search rejects q longer than the cap`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let q = String.replicate 200 "a"
    let response = client.GetAsync(sprintf "/stores/search?q=%s" q).Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest
    (parse<ErrorDto> response).error |> should equal "invalid_query"

[<Fact>]
let ``search results are cached: identical q hits upstream once`` () =
    let mutable calls = 0
    let stub : HttpHandlers.SearchByQuery =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&calls) |> ignore
                return Ok [ sampleShop ]
            }
    let deps = { (TestHost.Stubs.deps()) with SearchByQuery = stub }
    use client = TestHost.start deps
    for _ in 1 .. 4 do
        let _ = client.GetAsync("/stores/search?q=98109").Result
        ()
    calls |> should equal 1

[<Fact>]
let ``cache key is case- and whitespace-insensitive`` () =
    let mutable calls = 0
    let stub : HttpHandlers.SearchByQuery =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&calls) |> ignore
                return Ok [ sampleShop ]
            }
    let deps = { (TestHost.Stubs.deps()) with SearchByQuery = stub }
    use client = TestHost.start deps
    let _ = client.GetAsync("/stores/search?q=Seattle%2C%20WA").Result
    let _ = client.GetAsync("/stores/search?q=seattle%2C%20wa").Result
    let _ = client.GetAsync("/stores/search?q=%20%20Seattle%2C%20WA%20%20").Result
    calls |> should equal 1

[<Fact>]
let ``upstream failure surfaces as 502`` () =
    let stub : HttpHandlers.SearchByQuery =
        fun _ -> async { return Error (UpstreamUnavailable "krispykreme.com timed out") }
    let deps = { (TestHost.Stubs.deps()) with SearchByQuery = stub }
    use client = TestHost.start deps
    let response = client.GetAsync("/stores/search?q=98109").Result
    response.StatusCode |> should equal HttpStatusCode.BadGateway
