module Kremeing.Api.Tests.LiveApiParseTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// Pure tests on `parseShops` and the domain mappers. They run against
// the real captured Seattle response so changes in KK's wire format
// are caught by a single fixture refresh — not scattered across tests.

let private seattleFixture () =
    HttpStubs.readFixture "shops-search-seattle.json"

module ParseShops =

    [<Fact>]
    let ``empty array parses to empty list, not Error`` () =
        match LiveApi.parseShops "[]" with
        | Ok shops -> shops |> should equal ([] : LiveApi.KrispyShopDto list)
        | Error msg -> failwithf "expected Ok, got Error: %s" msg

    [<Fact>]
    let ``malformed json returns Error`` () =
        match LiveApi.parseShops "not json" with
        | Ok _ -> failwith "expected Error"
        | Error _ -> ()

    [<Fact>]
    let ``object instead of array returns Error`` () =
        match LiveApi.parseShops "{\"shopId\": 1}" with
        | Ok _ -> failwith "expected Error"
        | Error _ -> ()

    [<Fact>]
    let ``Seattle fixture parses without error`` () =
        match LiveApi.parseShops (seattleFixture()) with
        | Ok shops -> shops |> should not' (be Empty)
        | Error msg -> failwithf "expected Ok, got Error: %s" msg

    [<Fact>]
    let ``Seattle fixture contains the three known WA shopIds`` () =
        // Regression guard: SODO=899, Aurora=898, Issaquah=896 are
        // anchors we use elsewhere in tests + InMemory seed data.
        let shops =
            match LiveApi.parseShops (seattleFixture()) with
            | Ok s -> s
            | Error msg -> failwithf "fixture failed to parse: %s" msg
        let ids = shops |> List.map (fun s -> s.shopId) |> Set.ofList
        ids |> should contain 899
        ids |> should contain 898
        ids |> should contain 896

    [<Fact>]
    let ``hotLightOn is preserved verbatim from upstream JSON`` () =
        let shops =
            match LiveApi.parseShops (seattleFixture()) with
            | Ok s -> s
            | Error msg -> failwithf "fixture failed to parse: %s" msg
        // At capture time SODO was on; this is the calibration point
        // that proves we're reading the right bool field.
        let sodo = shops |> List.find (fun s -> s.shopId = 899)
        sodo.hotLightOn |> should equal true

module ShopToStore =

    let private sample : LiveApi.KrispyShopDto = {
        shopId = 899
        shopName = "Seattle - 1st Ave South"
        shopUrl = "https://site.krispykreme.com/wa/seattle/1900-1st-ave-s"
        address1 = "1900 1st Ave S"
        city = "Seattle"
        state = "WA"
        zipCode = "98134"
        latitude = 47.58536
        longitude = -122.33407
        hotLightOn = true
        hoursDescriptionHotlight = [| "7:00 AM - 9:00 AM"; "5:00 PM - 7:00 PM" |]
        distance = 0.32
    }

    [<Fact>]
    let ``id is StoreId of the upstream shopId`` () =
        let store = LiveApi.shopToStore sample
        store.Id |> should equal (StoreId 899)

    [<Fact>]
    let ``name is prefixed with "Krispy Kreme " for display`` () =
        let store = LiveApi.shopToStore sample
        store.Name |> should equal "Krispy Kreme Seattle - 1st Ave South"

    [<Fact>]
    let ``address composes line1 city state zip with comma separators`` () =
        let store = LiveApi.shopToStore sample
        store.Address |> should equal "1900 1st Ave S, Seattle, WA 98134"

    [<Fact>]
    let ``coordinates carry over with full float precision`` () =
        let store = LiveApi.shopToStore sample
        store.Location.Latitude |> should equal 47.58536
        store.Location.Longitude |> should equal -122.33407

module ShopToObservation =

    let private sample (hot: bool) : LiveApi.KrispyShopDto = {
        shopId = 899
        shopName = "X"
        shopUrl = ""
        address1 = ""
        city = ""
        state = ""
        zipCode = ""
        latitude = 0.0
        longitude = 0.0
        hotLightOn = hot
        hoursDescriptionHotlight = [||]
        distance = 0.0
    }

    [<Fact>]
    let ``hotLightOn=true maps to On`` () =
        let now = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)
        let obs = LiveApi.shopToObservation now (sample true)
        obs.Status |> should equal On

    [<Fact>]
    let ``hotLightOn=false maps to Off`` () =
        let now = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)
        let obs = LiveApi.shopToObservation now (sample false)
        obs.Status |> should equal Off

    [<Fact>]
    let ``observedAt is exactly the now value passed in`` () =
        // Determinism is required: the poller stamps observations
        // with a single "tick start" timestamp so all stores in a
        // batch are aligned even if the batch takes seconds.
        let now = DateTimeOffset(2026, 5, 8, 12, 30, 45, TimeSpan.Zero)
        let obs = LiveApi.shopToObservation now (sample true)
        obs.ObservedAt |> should equal now
