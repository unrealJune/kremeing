module Kremeing.Contract.Tests.MapConfigContractTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Api

let private parse (body: string) =
    JsonSerializer.Deserialize<MapConfigResponseDto>(
        body, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

[<Fact>]
let ``GET /map-config returns the client's tile URL with cache headers`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let r = client.GetAsync("/map-config?client=android").Result
    int r.StatusCode |> should equal 200
    r.Headers.CacheControl.Public |> should equal true
    r.Headers.CacheControl.MaxAge.Value.TotalSeconds |> should equal 3600.0
    let body = parse (r.Content.ReadAsStringAsync().Result)
    body.tileUrlTemplate
    |> should equal "https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=ANDROIDKEY"
    body.attribution |> should equal "© OpenStreetMap contributors © CARTO"
    body.maxZoom |> should equal 20
