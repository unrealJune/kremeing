module Kremeing.Contract.Tests.HotLightContractTests

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

// Each test asserts a specific clause of the wire contract:
//   - HTTP status code
//   - JSON shape (every field in the DTO is named and typed)
//   - Mapping of domain errors -> HTTP responses
// Tests build their own Deps bag from TestHost.Stubs.deps and override
// only the fields they exercise — no shared in-memory state.

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

let private parse<'T> (response: HttpResponseMessage) : 'T =
    let body = response.Content.ReadAsStringAsync().Result
    JsonSerializer.Deserialize<'T>(body, jsonOptions)

let private withStatus (status: Ports.GetHotLightStatus) =
    { TestHost.Stubs.deps with GetHotLightStatus = status }

[<Fact>]
let ``GET /health returns 200 ok`` () =
    use client = TestHost.start TestHost.Stubs.deps
    let response = client.GetAsync("/health").Result
    response.StatusCode |> should equal HttpStatusCode.OK

[<Fact>]
let ``GET /stores/{id}/hot-light returns 200 with the wire DTO when port returns Ok`` () =
    let observedAt = DateTimeOffset(2026, 5, 7, 14, 0, 0, TimeSpan.Zero)
    let stub = TestHost.Stubs.alwaysReturns (TestHost.Stubs.observation 899 On observedAt)
    use client = TestHost.start (withStatus stub)

    let response = client.GetAsync("/stores/899/hot-light").Result
    response.StatusCode |> should equal HttpStatusCode.OK

    let dto = parse<HotLightStatusDto> response
    dto.storeId |> should equal 899
    dto.status |> should equal "on"
    dto.observedAt |> should equal observedAt

[<Fact>]
let ``unknown store returns 404 with store_not_found error code`` () =
    let stub = TestHost.Stubs.alwaysFails (StoreNotFound (StoreId 999999))
    use client = TestHost.start (withStatus stub)

    let response = client.GetAsync("/stores/999999/hot-light").Result
    response.StatusCode |> should equal HttpStatusCode.NotFound

    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.StoreNotFound
    dto.message |> should haveSubstring "999999"

[<Fact>]
let ``non-numeric store id returns 400 with invalid_store_id error`` () =
    // The path %s captures any segment; Validation.parseStoreId enforces
    // the int-only rule, returning a 400 before any port is consulted.
    let stub = TestHost.Stubs.alwaysFails (UpstreamUnavailable "should not be called")
    use client = TestHost.start (withStatus stub)

    let response = client.GetAsync("/stores/ghost/hot-light").Result
    response.StatusCode |> should equal HttpStatusCode.BadRequest

    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.InvalidStoreId

[<Fact>]
let ``upstream failure returns 502 with upstream_unavailable error code`` () =
    let stub = TestHost.Stubs.alwaysFails (UpstreamUnavailable "krispykreme.com timed out")
    use client = TestHost.start (withStatus stub)

    let response = client.GetAsync("/stores/899/hot-light").Result
    response.StatusCode |> should equal HttpStatusCode.BadGateway

    let dto = parse<ErrorDto> response
    dto.error |> should equal ErrorCodes.UpstreamUnavailable
    dto.message |> should equal "krispykreme.com timed out"

[<Fact>]
let ``status field uses lowercase wire values, never F# discriminator names`` () =
    let stub = TestHost.Stubs.alwaysReturns (TestHost.Stubs.observation 1 Off DateTimeOffset.UtcNow)
    use client = TestHost.start (withStatus stub)

    let body = client.GetStringAsync("/stores/1/hot-light").Result
    body |> should haveSubstring "\"status\":\"off\""
    body |> should not' (haveSubstring "\"Off\"")
