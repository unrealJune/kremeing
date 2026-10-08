module Kremeing.Api.Tests.MapConfigTests

open Xunit
open FsUnit.Xunit
open Kremeing.Api

// /map-config hands each client its basemap tile URL. The key is chosen
// per client so one can be revoked without breaking the other.

let private config : HttpHandlers.MapConfig = {
    UrlTemplate = HttpHandlers.DefaultBasemapUrl
    WebKey = Some "WEB"
    AndroidKey = Some "DROID"
}

[<Fact>]
let ``web client gets the web key`` () =
    HttpHandlers.tileUrlFor config (Some "web")
    |> should equal "https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=WEB"

[<Fact>]
let ``android client gets the android key`` () =
    HttpHandlers.tileUrlFor config (Some "android")
    |> should equal "https://basemaps.junephilip.com/light_all/{z}/{x}/{y}{r}.png?key=DROID"

[<Fact>]
let ``unknown or missing client falls back to the web key`` () =
    HttpHandlers.tileUrlFor config (Some "ios") |> should endWith "?key=WEB"
    HttpHandlers.tileUrlFor config None |> should endWith "?key=WEB"

[<Fact>]
let ``missing key returns the URL without a key param`` () =
    HttpHandlers.tileUrlFor { config with AndroidKey = None } (Some "android")
    |> should equal HttpHandlers.DefaultBasemapUrl
