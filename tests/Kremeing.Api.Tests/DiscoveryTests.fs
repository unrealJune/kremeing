module Kremeing.Api.Tests.DiscoveryTests

open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// Fixtures captured from site.krispykreme.com on 2026-05-08. Re-capture
// when the site layout changes; failure modes here will surface that.

let private rootFixture () = HttpStubs.readFixture "site-root.html"
let private waFixture () = HttpStubs.readFixture "site-wa.html"

module ParsePage =

    [<Fact>]
    let ``WA state page lists Seattle as a multi-store city`` () =
        let page = Discovery.parsePage (waFixture())
        let target : Discovery.ScrapedCity = { State = "wa"; City = "seattle" }
        page.Cities |> should contain target

    [<Fact>]
    let ``WA state page exposes single-store cities as direct store links`` () =
        let page = Discovery.parsePage (waFixture())
        let stores = page.Stores
        // Issaquah/Spokane/Tacoma/Vancouver/Yakima/Richland are all 1-shop cities.
        let seenCities = stores |> List.map (fun s -> s.City) |> Set.ofList
        seenCities |> should contain "issaquah"
        seenCities |> should contain "spokane"
        seenCities |> should contain "tacoma"

    [<Fact>]
    let ``slug for Issaquah store is its address-derived path`` () =
        let page = Discovery.parsePage (waFixture())
        let issaquah = page.Stores |> List.find (fun s -> s.City = "issaquah")
        // This slug is what /shops/search returns as `shopUrl` — exact match
        // is what links a scraped slug to a registry entry later.
        issaquah.Slug |> should equal "6210-east-lake-sammamish-pkwy"

    [<Fact>]
    let ``parsePage doesn't pollute Cities with the state's own self-link`` () =
        // Regression guard: the state page links to itself with `wa` (no city
        // segment); the parser must not interpret that as a city named "wa".
        let page = Discovery.parsePage (waFixture())
        page.Cities |> List.iter (fun c -> c.City |> should not' (equal ""))

module ParseRootStates =

    [<Fact>]
    let ``root page exposes every continental US state we know KK operates in`` () =
        let states = Discovery.parseRootStates (rootFixture()) |> Set.ofList
        // Spot-check across the country — if these 6 are missing, the regex
        // is wrong, not just one entry.
        for s in [ "wa"; "ca"; "tx"; "fl"; "ny"; "ga" ] do
            states |> should contain s

    [<Fact>]
    let ``root page yields more than 30 distinct US states`` () =
        // KK operates in ~40 US states; if we get noticeably less, the
        // selector is missing something.
        let states = Discovery.parseRootStates (rootFixture())
        states |> List.length |> should be (greaterThan 30)

    [<Fact>]
    let ``state slugs are deduplicated`` () =
        let states = Discovery.parseRootStates (rootFixture())
        let unique = states |> List.distinct |> List.length
        states |> List.length |> should equal unique

    [<Fact>]
    let ``every state slug is exactly two lowercase letters`` () =
        let states = Discovery.parseRootStates (rootFixture())
        for s in states do
            s.Length |> should equal 2
            s |> should equal (s.ToLowerInvariant())

module EnumerateCities =

    let private fixtureSend () =
        // Same fixture stands in for every state page for this test — we're
        // proving the orchestration shape, not that every state's HTML is
        // perfectly distinct. Per-state failure paths are tested separately.
        HttpStubs.routeByPath
            [ "/wa", waFixture()           // most-specific path first
              "/", rootFixture() ]

    [<Fact>]
    let ``aggregates root + state pages into a single deduped result`` () =
        let result = Discovery.enumerateCities (fixtureSend()) |> Async.RunSynchronously
        match result with
        | Ok page ->
            // Direct stores from root (e.g. CT/Uncasville) survive
            let storeCities = page.Stores |> List.map (fun s -> s.City) |> Set.ofList
            storeCities |> should contain "uncasville"
            // Cities from state pages survive
            let cityCities = page.Cities |> List.map (fun c -> c.City) |> Set.ofList
            cityCities |> should contain "seattle"
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``returns Error if root page itself fails to fetch`` () =
        let send: LiveApi.SendHttp =
            fun _ ->
                async {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                }
        match Discovery.enumerateCities send |> Async.RunSynchronously with
        | Error (UpstreamUnavailable _) -> ()
        | other -> failwithf "expected UpstreamUnavailable, got %A" other

module ResolveRegistry =

    let private fixtureSearch (cities: Discovery.ScrapedCity list)
        : string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>> =
        // For any "Seattle, WA" query, return the Seattle fixture — we're
        // testing dedup + mapping, not the network.
        let body = HttpStubs.readFixture "shops-search-seattle.json"
        let parsed =
            match LiveApi.parseShops body with
            | Ok s -> s
            | Error e -> failwithf "fixture parse: %s" e
        fun _ -> async { return Ok parsed }

    [<Fact>]
    let ``produces a RegistryEntry per unique shopId across cities`` () =
        let page : Discovery.ScrapedPage = {
            Cities = [
                { State = "wa"; City = "seattle" }
                { State = "wa"; City = "issaquah" }      // returns same fixture, same shops
            ]
            Stores = []
        }
        let registry =
            Discovery.resolveRegistry (fixtureSearch page.Cities) page
            |> Async.RunSynchronously
        // The fixture has 12 unique shops; calling search twice must not
        // produce 24 entries.
        registry |> List.length |> should equal 12
        registry
        |> List.map (fun e -> e.ShopId)
        |> List.distinct
        |> List.length
        |> should equal 12

    [<Fact>]
    let ``each registry entry carries a SearchKey that round-trips through LiveApi`` () =
        let page : Discovery.ScrapedPage = {
            Cities = [ { State = "wa"; City = "seattle" } ]
            Stores = []
        }
        let registry =
            Discovery.resolveRegistry (fixtureSearch page.Cities) page
            |> Async.RunSynchronously
        // SearchKey is "{city}, {state}" so the poller can re-search later.
        let sodo = registry |> List.find (fun e -> e.ShopId = 899)
        sodo.SearchKey |> should equal "Seattle, WA"

    [<Fact>]
    let ``registry entry name and address are formatted for client display`` () =
        let page : Discovery.ScrapedPage = {
            Cities = [ { State = "wa"; City = "seattle" } ]
            Stores = []
        }
        let registry =
            Discovery.resolveRegistry (fixtureSearch page.Cities) page
            |> Async.RunSynchronously
        let sodo = registry |> List.find (fun e -> e.ShopId = 899)
        sodo.Name |> should haveSubstring "Krispy Kreme"
        sodo.Address |> should haveSubstring "1900 1st Ave S"
        sodo.Address |> should haveSubstring "WA 98134"
