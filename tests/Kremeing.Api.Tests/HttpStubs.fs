module Kremeing.Api.Tests.HttpStubs

open System
open System.IO
open System.Net
open System.Net.Http
open Kremeing.Api

/// Builds a `LiveApi.SendHttp` lambda that returns a fixed response.
/// Captures the last request received so tests can assert URL/headers.
type RecordedSend(handler: HttpRequestMessage -> HttpResponseMessage) =
    let mutable lastRequest : HttpRequestMessage option = None
    member _.LastRequest = lastRequest
    member _.Send : LiveApi.SendHttp =
        fun req ->
            lastRequest <- Some req
            async { return handler req }

let respondWithJson (statusCode: HttpStatusCode) (body: string) : LiveApi.SendHttp =
    fun _ ->
        async {
            let r = new HttpResponseMessage(statusCode)
            r.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            return r
        }

let alwaysThrows (ex: exn) : LiveApi.SendHttp =
    fun _ ->
        async { return raise ex }

let private fixturesDir =
    Path.Combine(AppContext.BaseDirectory, "fixtures")

let readFixture (name: string) : string =
    File.ReadAllText (Path.Combine(fixturesDir, name))

/// Routes requests by URL substring → fixture body. Useful for tests that
/// exercise a multi-page scrape without spinning up a server.
let routeByPath (routes: (string * string) list) : LiveApi.SendHttp =
    fun req ->
        async {
            let url = req.RequestUri.OriginalString
            let body =
                routes
                |> List.tryFind (fun (pat, _) -> url.Contains pat)
                |> Option.map snd
            let r =
                match body with
                | Some b ->
                    new HttpResponseMessage(HttpStatusCode.OK,
                        Content = new StringContent(b, System.Text.Encoding.UTF8, "text/html"))
                | None ->
                    new HttpResponseMessage(HttpStatusCode.NotFound)
            return r
        }
