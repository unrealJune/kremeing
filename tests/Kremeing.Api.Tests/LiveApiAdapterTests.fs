module Kremeing.Api.Tests.LiveApiAdapterTests

open System
open System.Net
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// These tests exercise the IO boundary of LiveApi: the request that's
// actually built, the status-code → StoreError mapping, and the way
// the per-store adapter composes lookup + search + filter.

let private fixedNow = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)

module SearchByCityState =

    [<Fact>]
    let ``returns Ok shop list on a 200 response`` () =
        let body = HttpStubs.readFixture "shops-search-seattle.json"
        let send = HttpStubs.respondWithJson HttpStatusCode.OK body
        let result = LiveApi.searchByCityState send "Seattle, WA" |> Async.RunSynchronously
        match result with
        | Ok shops -> shops |> should not' (be Empty)
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``targets the /shops/search endpoint with cityStateZip query param`` () =
        let recorder = HttpStubs.RecordedSend(fun _ ->
            new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK,
                Content = new System.Net.Http.StringContent("[]", System.Text.Encoding.UTF8, "application/json")))
        let _ = LiveApi.searchByCityState recorder.Send "Seattle, WA" |> Async.RunSynchronously
        let req = recorder.LastRequest |> Option.defaultWith (fun () -> failwith "no request captured")
        let url = req.RequestUri.OriginalString
        url |> should haveSubstring "api.krispykreme.com/shops/search"
        url |> should haveSubstring "cityStateZip="
        // Both comma and space have to be percent-encoded — comma is reserved
        // in URI grammar, space is unsafe. .NET's normalized `ToString` may
        // collapse `%20` back to a literal space, so we read OriginalString.
        url |> should haveSubstring "Seattle%2C%20WA"

    [<Fact>]
    let ``sends Origin header so upstream WAF accepts the request`` () =
        let recorder = HttpStubs.RecordedSend(fun _ ->
            new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK,
                Content = new System.Net.Http.StringContent("[]")))
        let _ = LiveApi.searchByCityState recorder.Send "x" |> Async.RunSynchronously
        let req = recorder.LastRequest |> Option.defaultWith (fun () -> failwith "no request")
        let origin = req.Headers.GetValues("Origin") |> Seq.head
        origin |> should equal "https://www.krispykreme.com"

    [<Fact>]
    let ``5xx response becomes UpstreamUnavailable carrying the status code`` () =
        let send = HttpStubs.respondWithJson (enum<HttpStatusCode> 503) "<html>down</html>"
        match LiveApi.searchByCityState send "Seattle, WA" |> Async.RunSynchronously with
        | Error (UpstreamUnavailable msg) -> msg |> should haveSubstring "503"
        | other -> failwithf "expected UpstreamUnavailable, got %A" other

    [<Fact>]
    let ``malformed body becomes UpstreamUnavailable with parse hint`` () =
        let send = HttpStubs.respondWithJson HttpStatusCode.OK "not actually json"
        match LiveApi.searchByCityState send "x" |> Async.RunSynchronously with
        | Error (UpstreamUnavailable msg) -> msg |> should haveSubstring "JSON parse failed"
        | other -> failwithf "expected UpstreamUnavailable, got %A" other

    [<Fact>]
    let ``send-side exception becomes UpstreamUnavailable, not a crash`` () =
        let send = HttpStubs.alwaysThrows (System.Net.Http.HttpRequestException("boom"))
        match LiveApi.searchByCityState send "x" |> Async.RunSynchronously with
        | Error (UpstreamUnavailable msg) -> msg |> should haveSubstring "boom"
        | other -> failwithf "expected UpstreamUnavailable, got %A" other

module SearchByLocation =

    [<Fact>]
    let ``targets the /shops endpoint with latitude+longitude query params`` () =
        let recorder = HttpStubs.RecordedSend(fun _ ->
            new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK,
                Content = new System.Net.Http.StringContent("[]")))
        let _ = LiveApi.searchByLocation recorder.Send 47.6062 -122.3321 |> Async.RunSynchronously
        let url = recorder.LastRequest.Value.RequestUri.ToString()
        url |> should haveSubstring "api.krispykreme.com/shops?"
        url |> should haveSubstring "latitude=47.6062"
        url |> should haveSubstring "longitude=-122.3321"

    [<Fact>]
    let ``coordinates serialize with invariant culture decimal point`` () =
        // Regression guard: a server that picks up de-DE locale would
        // emit "47,6062" and KK's WAF would reject it with HTTP 400.
        let recorder = HttpStubs.RecordedSend(fun _ ->
            new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK,
                Content = new System.Net.Http.StringContent("[]")))
        let _ = LiveApi.searchByLocation recorder.Send 47.5 -122.5 |> Async.RunSynchronously
        let url = recorder.LastRequest.Value.RequestUri.ToString()
        url |> should haveSubstring "47.5"
        url |> should not' (haveSubstring "47,5")

module GetHotLightStatus =

    let private always (q: string) : StoreId -> Async<Result<string, StoreError>> =
        fun _ -> async { return Ok q }

    let private alwaysFails (err: StoreError) : StoreId -> Async<Result<string, StoreError>> =
        fun _ -> async { return Error err }

    [<Fact>]
    let ``returns Ok observation when shopId is in the response`` () =
        let body = HttpStubs.readFixture "shops-search-seattle.json"
        let send = HttpStubs.respondWithJson HttpStatusCode.OK body
        let port = LiveApi.getHotLightStatus send (always "Seattle, WA") (fun () -> fixedNow)
        match port (StoreId 899) |> Async.RunSynchronously with
        | Ok obs ->
            obs.StoreId |> should equal (StoreId 899)
            obs.ObservedAt |> should equal fixedNow
            // SODO at fixture-capture time was On
            obs.Status |> should equal On
        | Error e -> failwithf "expected Ok, got %A" e

    [<Fact>]
    let ``returns StoreNotFound when shopId is missing from upstream response`` () =
        let body = HttpStubs.readFixture "shops-search-seattle.json"
        let send = HttpStubs.respondWithJson HttpStatusCode.OK body
        let port = LiveApi.getHotLightStatus send (always "Seattle, WA") (fun () -> fixedNow)
        match port (StoreId 999999) |> Async.RunSynchronously with
        | Error (StoreNotFound (StoreId 999999)) -> ()
        | other -> failwithf "expected StoreNotFound 999999, got %A" other

    [<Fact>]
    let ``propagates lookupQuery errors without calling upstream`` () =
        // We assert on the propagation by giving send a body that
        // would fail to parse — if the adapter mistakenly called it,
        // the error would shape-shift to UpstreamUnavailable.
        let send = HttpStubs.respondWithJson HttpStatusCode.OK "garbage"
        let lookup = alwaysFails (InvalidStoreId "no such id in registry")
        let port = LiveApi.getHotLightStatus send lookup (fun () -> fixedNow)
        match port (StoreId 12345) |> Async.RunSynchronously with
        | Error (InvalidStoreId "no such id in registry") -> ()
        | other -> failwithf "expected InvalidStoreId pass-through, got %A" other

    [<Fact>]
    let ``propagates upstream HTTP errors`` () =
        let send = HttpStubs.respondWithJson (enum<HttpStatusCode> 502) "bad gateway"
        let port = LiveApi.getHotLightStatus send (always "Seattle, WA") (fun () -> fixedNow)
        match port (StoreId 899) |> Async.RunSynchronously with
        | Error (UpstreamUnavailable msg) -> msg |> should haveSubstring "502"
        | other -> failwithf "expected UpstreamUnavailable, got %A" other
