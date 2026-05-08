module Kremeing.Contract.Tests.HistoryContractTests

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

let private depsWithObservations
        (observations: InMemoryObservations.Store)
        (now: DateTimeOffset) : HttpHandlers.Deps =
    { TestHost.Stubs.deps with
        History = observations.History
        Status = observations.Status
        Now = fun () -> now }

let private at h m = DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

[<Fact>]
let ``GET /stores/{id}/history returns 400 for non-numeric ids`` () =
    use client = TestHost.start TestHost.Stubs.deps
    let response = client.GetAsync("/stores/ghost/history").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``GET /stores/{id}/history returns 404 for stores we've never observed`` () =
    let observations = InMemoryObservations.create()
    let deps = depsWithObservations observations (at 11 0)
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/899/history").Result
    response.StatusCode |> should equal HttpStatusCode.NotFound
    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.StoreNotFound

[<Fact>]
let ``GET /stores/{id}/history returns flips oldest-first with explicit range`` () =
    let observations = InMemoryObservations.create()
    let _ = observations.Record { StoreId = StoreId 899; Status = On;  ObservedAt = at 10 0 }
            |> Async.RunSynchronously
    let _ = observations.Record { StoreId = StoreId 899; Status = Off; ObservedAt = at 10 30 }
            |> Async.RunSynchronously
    let _ = observations.Record { StoreId = StoreId 899; Status = On;  ObservedAt = at 11 0 }
            |> Async.RunSynchronously

    let deps = depsWithObservations observations (at 12 0)
    use client = TestHost.start deps

    let url = sprintf "/stores/899/history?since=%s&until=%s"
                (Uri.EscapeDataString ((at 9 0).ToString("o")))
                (Uri.EscapeDataString ((at 12 0).ToString("o")))
    let response = client.GetAsync(url).Result
    response.StatusCode |> should equal HttpStatusCode.OK

    let body = parse<HotLightHistoryDto> response
    body.storeId |> should equal 899
    body.flips.Length |> should equal 3
    body.flips.[0].observedAt |> should equal (at 10 0)
    body.flips.[0].status |> should equal "on"
    body.flips.[1].status |> should equal "off"
    body.flips.[2].status |> should equal "on"

[<Fact>]
let ``GET /stores/{id}/history range filter is half-open`` () =
    let observations = InMemoryObservations.create()
    let _ = observations.Record { StoreId = StoreId 899; Status = On;  ObservedAt = at 10 0 }
            |> Async.RunSynchronously
    let _ = observations.Record { StoreId = StoreId 899; Status = Off; ObservedAt = at 10 30 }
            |> Async.RunSynchronously
    let _ = observations.Record { StoreId = StoreId 899; Status = On;  ObservedAt = at 11 0 }
            |> Async.RunSynchronously

    let deps = depsWithObservations observations (at 12 0)
    use client = TestHost.start deps

    // Asking for [10:30, 11:00) — should include the 10:30 flip but not 11:00
    let url = sprintf "/stores/899/history?since=%s&until=%s"
                (Uri.EscapeDataString ((at 10 30).ToString("o")))
                (Uri.EscapeDataString ((at 11 0).ToString("o")))
    let response = client.GetAsync(url).Result
    let body = parse<HotLightHistoryDto> response
    body.flips.Length |> should equal 1
    body.flips.[0].status |> should equal "off"

[<Fact>]
let ``GET /stores/{id}/history defaults to last 7 days when range is omitted`` () =
    let observations = InMemoryObservations.create()
    let now = at 12 0
    let weekAgo = now - TimeSpan.FromDays 7.0
    let _ = observations.Record { StoreId = StoreId 899; Status = On;  ObservedAt = weekAgo - TimeSpan.FromHours 1.0 }
            |> Async.RunSynchronously
    let _ = observations.Record { StoreId = StoreId 899; Status = Off; ObservedAt = now - TimeSpan.FromHours 1.0 }
            |> Async.RunSynchronously

    let deps = depsWithObservations observations now
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/899/history").Result
    let body = parse<HotLightHistoryDto> response
    // Old flip outside the 7-day window is dropped, recent one stays.
    body.flips.Length |> should equal 1
    body.flips.[0].status |> should equal "off"
