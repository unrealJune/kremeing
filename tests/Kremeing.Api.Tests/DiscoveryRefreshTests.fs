module Kremeing.Api.Tests.DiscoveryRefreshTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// Regression suite for the silent-empty outage: discovery once cached an
// empty registry forever. These tests pin the guard — an empty or failed
// refresh must never replace a known-good registry.

let private entry shopId searchKey : Discovery.RegistryEntry = {
    ShopId = shopId
    Name = sprintf "Krispy Kreme %d" shopId
    Address = "irrelevant"
    Location = { Latitude = 0.0; Longitude = 0.0 }
    ShopUrl = ""
    SearchKey = searchKey
}

module Decide =

    [<Fact>]
    let ``a non-empty discovery result is adopted`` () =
        let fresh = [ entry 899 "Seattle, WA"; entry 898 "Seattle, WA" ]
        match DiscoveryRefresh.decide (Ok fresh) with
        | DiscoveryRefresh.Replaced entries -> entries |> should equal fresh
        | other -> failwithf "expected Replaced, got %A" other

    [<Fact>]
    let ``an empty discovery result is rejected (keep existing)`` () =
        // The incident in one assertion: discovery returns 0 stores and we
        // must NOT adopt it.
        match DiscoveryRefresh.decide (Ok []) with
        | DiscoveryRefresh.KeptEmpty -> ()
        | other -> failwithf "expected KeptEmpty, got %A" other

    [<Fact>]
    let ``a failed discovery is rejected (keep existing)`` () =
        match DiscoveryRefresh.decide (Error (UpstreamUnavailable "boom")) with
        | DiscoveryRefresh.KeptError (UpstreamUnavailable "boom") -> ()
        | other -> failwithf "expected KeptError, got %A" other

module Holder =

    let private at = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)

    [<Fact>]
    let ``Get returns the seeded registry until a Set swaps it`` () =
        let seed = [ entry 899 "Seattle, WA" ]
        let holder = Registry.Holder(seed, at)
        holder.Get() |> should equal seed
        holder.Count |> should equal 1
        holder.LastRefreshedAt |> should equal at

        let fresh = [ entry 899 "Seattle, WA"; entry 100 "Atlanta, GA" ]
        let later = at.AddHours 12.0
        holder.Set(fresh, later)
        holder.Get() |> should equal fresh
        holder.Count |> should equal 2
        holder.LastRefreshedAt |> should equal later

    [<Fact>]
    let ``a rejected (empty) refresh leaves the holder untouched`` () =
        // Mirrors the service loop: decide → only Set on Replaced.
        let seed = [ entry 899 "Seattle, WA"; entry 100 "Atlanta, GA" ]
        let holder = Registry.Holder(seed, at)
        match DiscoveryRefresh.decide (Ok []) with
        | DiscoveryRefresh.Replaced entries -> holder.Set(entries, DateTimeOffset.UtcNow)
        | DiscoveryRefresh.KeptEmpty | DiscoveryRefresh.KeptError _ -> ()
        // Still the original two stores; nothing was clobbered.
        holder.Get() |> should equal seed
        holder.Count |> should equal 2
        holder.LastRefreshedAt |> should equal at

module RunDiscovery =

    let private rootFixture () = HttpStubs.readFixture "site-root.html"
    let private waFixture () = HttpStubs.readFixture "site-wa.html"

    let private fixtureSearch
        : string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>> =
        let body = HttpStubs.readFixture "shops-search-seattle.json"
        let parsed =
            match LiveApi.parseShops body with
            | Ok s -> s
            | Error e -> failwithf "fixture parse: %s" e
        fun _ -> async { return Ok parsed }

    [<Fact>]
    let ``enumerate + resolve yields a non-empty registry`` () =
        let send = HttpStubs.routeByPath [ "/wa", waFixture(); "/", rootFixture() ]
        match DiscoveryRefresh.runDiscovery send fixtureSearch |> Async.RunSynchronously with
        | Ok registry -> registry |> List.isEmpty |> should equal false
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``a failed root enumeration surfaces as Error (so decide keeps existing)`` () =
        let send : LiveApi.SendHttp =
            fun _ ->
                async {
                    return new System.Net.Http.HttpResponseMessage(
                        System.Net.HttpStatusCode.InternalServerError)
                }
        match DiscoveryRefresh.runDiscovery send fixtureSearch |> Async.RunSynchronously with
        | Error (UpstreamUnavailable _) -> ()
        | other -> failwithf "expected Error, got %A" other
