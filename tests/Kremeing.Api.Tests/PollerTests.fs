module Kremeing.Api.Tests.PollerTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

let private fixedNow = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)
let private now () = fixedNow

let private entry shopId searchKey : Discovery.RegistryEntry = {
    ShopId = shopId
    Name = sprintf "Krispy Kreme %d" shopId
    Address = "irrelevant"
    Location = { Latitude = 0.0; Longitude = 0.0 }
    ShopUrl = ""
    SearchKey = searchKey
}

let private fixtureShops () =
    let body = HttpStubs.readFixture "shops-search-seattle.json"
    match LiveApi.parseShops body with
    | Ok s -> s
    | Error e -> failwithf "fixture parse: %s" e

let private alwaysReturnsFixture () =
    let shops = fixtureShops ()
    let mutable callCount = 0
    let f : string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>> =
        fun _ ->
            async {
                System.Threading.Interlocked.Increment(&callCount) |> ignore
                return Ok shops
            }
    f, (fun () -> callCount)

[<Fact>]
let ``each unique SearchKey triggers exactly one upstream call per tick`` () =
    let registry = [
        entry 899 "Seattle, WA"      // SODO
        entry 898 "Seattle, WA"      // Aurora — same key as SODO
        entry 896 "Issaquah, WA"     // distinct key
    ]
    let search, calls = alwaysReturnsFixture ()
    let observations = InMemoryObservations.create ()
    let _ = Poller.runOnce registry search observations.Record now |> Async.RunSynchronously
    // 2 unique keys → 2 calls, not 3
    calls () |> should equal 2

[<Fact>]
let ``records one observation per registry entry whose shopId is in the response`` () =
    let registry = [
        entry 899 "Seattle, WA"
        entry 898 "Seattle, WA"
        entry 896 "Seattle, WA"
    ]
    let search, _ = alwaysReturnsFixture ()
    let observations = InMemoryObservations.create ()
    let stats =
        Poller.runOnce registry search observations.Record now
        |> Async.RunSynchronously
    stats.Polled |> should equal 3
    stats.Recorded |> should equal 3
    stats.Missing |> should equal 0
    stats.Failed |> should equal 0

[<Fact>]
let ``registry entries whose shopId is missing from upstream count as Missing`` () =
    let registry = [
        entry 899 "Seattle, WA"
        entry 999999 "Seattle, WA"   // not in fixture
    ]
    let search, _ = alwaysReturnsFixture ()
    let observations = InMemoryObservations.create ()
    let stats =
        Poller.runOnce registry search observations.Record now
        |> Async.RunSynchronously
    stats.Recorded |> should equal 1
    stats.Missing |> should equal 1

[<Fact>]
let ``failed search keys are counted but don't abort the tick`` () =
    let registry = [
        entry 899 "Seattle, WA"
        entry 100 "Atlanta, GA"
    ]
    let search : string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>> =
        fun key ->
            async {
                if key = "Atlanta, GA" then
                    return Error (UpstreamUnavailable "boom")
                else
                    return Ok (fixtureShops ())
            }
    let observations = InMemoryObservations.create ()
    let stats =
        Poller.runOnce registry search observations.Record now
        |> Async.RunSynchronously
    stats.Failed |> should equal 1
    // Seattle still recorded despite Atlanta failure
    stats.Recorded |> should equal 1

[<Fact>]
let ``observations are recorded with the tick's single observedAt timestamp`` () =
    // All stores in a tick should share the same ObservedAt — that
    // way bucketize() doesn't see jitter from sequential search calls.
    let registry = [
        entry 899 "Seattle, WA"
        entry 898 "Seattle, WA"
    ]
    let search, _ = alwaysReturnsFixture ()
    let observations = InMemoryObservations.create ()
    let _ = Poller.runOnce registry search observations.Record now |> Async.RunSynchronously

    let s899 =
        match observations.Status (StoreId 899) |> Async.RunSynchronously with
        | Ok s -> s
        | Error e -> failwithf "expected Ok, got %A" e
    let s898 =
        match observations.Status (StoreId 898) |> Async.RunSynchronously with
        | Ok s -> s
        | Error e -> failwithf "expected Ok, got %A" e
    s899.LastPolledAt |> should equal fixedNow
    s898.LastPolledAt |> should equal fixedNow
