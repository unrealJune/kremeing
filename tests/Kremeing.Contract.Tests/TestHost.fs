module Kremeing.Contract.Tests.TestHost

open System
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Kremeing.Contracts.Domain
open Kremeing.Api
open Kremeing.Core

/// Spins up an in-process Giraffe server with the given Deps, returns
/// an HttpClient bound to it. Each test gets its own host so stubs
/// and state don't leak between tests.
let start (deps: HttpHandlers.Deps) : HttpClient =
    let builder =
        Host.CreateDefaultBuilder()
            .ConfigureWebHost(fun web ->
                web.UseTestServer()
                   .ConfigureServices(fun s ->
                       Program.configureServices s)
                   .Configure(fun app ->
                       Program.configureApp deps app)
                |> ignore)
    let host = builder.Start()
    host.GetTestClient()

/// Stub builders. Tests describe behavior at the port boundary —
/// what the upstream "knows" — and the contract layer is exercised
/// end to end against that fixed reality.
module Stubs =

    let observation (id: int) (status: HotLightStatus) (at: DateTimeOffset) =
        { StoreId = StoreId id; Status = status; ObservedAt = at }

    let alwaysReturns (obs: HotLightObservation) : Ports.GetHotLightStatus =
        fun _ -> async { return Ok obs }

    let alwaysFails (err: StoreError) : Ports.GetHotLightStatus =
        fun _ -> async { return Error err }

    let private notUsedNearby : HttpHandlers.SearchNearby =
        fun _ -> async { return Error (UpstreamUnavailable "stub: search not configured") }

    let private notUsedSearchByQuery : HttpHandlers.SearchByQuery =
        fun _ -> async { return Error (UpstreamUnavailable "stub: search-by-query not configured") }

    let private notUsedHistory : Ports.GetHistory =
        fun _ -> async { return Error (UpstreamUnavailable "stub: history not configured") }

    let private notUsedStatus : Ports.GetStoreStatus =
        fun id -> async { return Error (StoreNotFound id) }

    let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

    /// All-stubbed Deps. Each access returns a *fresh* record (with
    /// fresh caches and a fresh limiter), because module-level singletons
    /// would leak state between tests — a successful call in test A
    /// would warm the cache and serve that response back in test B.
    /// The rate-limit capacity is intentionally astronomical so existing
    /// tests don't hit it; the dedicated rate-limit contract test
    /// instantiates a *small* limiter for its own assertion.
    let deps () : HttpHandlers.Deps =
        {
            GetHotLightStatus = alwaysFails (UpstreamUnavailable "stub: not configured")
            SearchNearby = notUsedNearby
            SearchByQuery = notUsedSearchByQuery
            History = notUsedHistory
            Status = notUsedStatus
            Now = fun () -> epoch
            HotLightCache =
                Cache.Cache<int, Kremeing.Contracts.Domain.HotLightObservation>(
                    System.TimeSpan.FromMinutes 5.0, 64)
            NearbyCache =
                Cache.Cache<int * int * int, Kremeing.Contracts.Api.NearbyResponseDto>(
                    System.TimeSpan.FromMinutes 5.0, 64)
            SearchCache =
                Cache.Cache<string * int, Kremeing.Contracts.Api.SearchResponseDto>(
                    System.TimeSpan.FromMinutes 5.0, 64)
            ProxyRateLimit =
                RateLimit.Limiter(capacity = 1_000_000.0, refillPerSecond = 1_000_000.0)
            // Push defaults to disabled in tests — the dedicated push
            // contract tests build their own deps with stubbed ports.
            Push = None
            Health =
                fun () ->
                    { Stores = 0
                      LastDiscoveryRefresh = epoch }
        }
