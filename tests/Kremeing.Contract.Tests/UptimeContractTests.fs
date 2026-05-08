module Kremeing.Contract.Tests.UptimeContractTests

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
let ``GET /stores/{id}/uptime returns 400 for non-numeric ids`` () =
    use client = TestHost.start TestHost.Stubs.deps
    let response = client.GetAsync("/stores/ghost/uptime").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest

[<Fact>]
let ``GET /stores/{id}/uptime?bucket=invalid returns 400`` () =
    let observations = InMemoryObservations.create()
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = at 10 0 }
        |> Async.RunSynchronously
    let deps = depsWithObservations observations (at 11 0)
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/899/uptime?bucket=fortnight").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest
    let dto = parse<ErrorDto> response
    dto.error |> should equal "invalid_bucket"

[<Fact>]
let ``GET /stores/{id}/uptime returns 404 for stores we've never observed`` () =
    let observations = InMemoryObservations.create()
    let deps = depsWithObservations observations (at 11 0)
    use client = TestHost.start deps

    let response = client.GetAsync("/stores/899/uptime").Result
    response.StatusCode |> should equal HttpStatusCode.NotFound
    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.StoreNotFound

[<Fact>]
let ``GET /stores/{id}/uptime returns hourly buckets covering the requested range`` () =
    let observations = InMemoryObservations.create()
    // First flip at 10:00, then a follow-up "still On" poll at 12:00 to
    // advance LastPolledAt — that's what the production poller does
    // every tick. Without it bucketize sees a zero-length trailing
    // interval and reports zero observed seconds.
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = at 10 0 }
        |> Async.RunSynchronously
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = at 12 0 }
        |> Async.RunSynchronously
    let deps = depsWithObservations observations (at 12 0)
    use client = TestHost.start deps

    let url = sprintf "/stores/899/uptime?bucket=hour&since=%s&until=%s"
                (Uri.EscapeDataString ((at 10 0).ToString("o")))
                (Uri.EscapeDataString ((at 12 0).ToString("o")))
    let response = client.GetAsync(url).Result
    response.StatusCode |> should equal HttpStatusCode.OK

    let body = parse<UptimeResponseDto> response
    body.storeId |> should equal 899
    body.bucket |> should equal "hour"
    body.buckets.Length |> should equal 2
    body.buckets.[0].fractionOn |> should equal 1.0
    body.buckets.[0].onSeconds |> should equal 3600.0

[<Fact>]
let ``GET /stores/{id}/uptime?bucket=day uses 1-day spans`` () =
    let observations = InMemoryObservations.create()
    let day1 = DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero)
    let day3 = DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero)
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = day1 }
        |> Async.RunSynchronously
    let _ =
        observations.Record { StoreId = StoreId 899; Status = On; ObservedAt = day3 }
        |> Async.RunSynchronously
    let deps = depsWithObservations observations day3
    use client = TestHost.start deps

    let url = sprintf "/stores/899/uptime?bucket=day&since=%s&until=%s"
                (Uri.EscapeDataString (day1.ToString("o")))
                (Uri.EscapeDataString (day3.ToString("o")))
    let response = client.GetAsync(url).Result
    let body = parse<UptimeResponseDto> response

    body.bucket |> should equal "day"
    body.buckets.Length |> should equal 3
    for b in body.buckets do
        b.totalSeconds |> should equal 86400.0
