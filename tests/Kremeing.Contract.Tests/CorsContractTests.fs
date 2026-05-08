module Kremeing.Contract.Tests.CorsContractTests

open System.Net
open System.Net.Http
open Xunit
open FsUnit.Xunit

// CORS is what lets the web/ frontend (dev-served on a different port)
// call this API directly from the browser. The policy is loose for
// localhost and absent everywhere else — these tests pin both sides.

let private getWithOrigin (origin: string) (path: string) =
    use client = TestHost.start TestHost.Stubs.deps
    let req = new HttpRequestMessage(HttpMethod.Get, path)
    req.Headers.Add("Origin", origin)
    client.SendAsync(req).Result

[<Fact>]
let ``localhost dev origin gets the Access-Control-Allow-Origin header back`` () =
    use response = getWithOrigin "http://localhost:5173" "/health"
    response.StatusCode |> should equal HttpStatusCode.OK
    let allowOrigin =
        response.Headers
        |> Seq.tryFind (fun h -> h.Key.Equals("Access-Control-Allow-Origin",
                                              System.StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun h -> Seq.head h.Value)
    allowOrigin |> should equal (Some "http://localhost:5173")

[<Fact>]
let ``127.0.0.1 origin is also accepted (alt-localhost)`` () =
    use response = getWithOrigin "http://127.0.0.1:8000" "/health"
    let allowOrigin =
        response.Headers
        |> Seq.tryFind (fun h -> h.Key.Equals("Access-Control-Allow-Origin",
                                              System.StringComparison.OrdinalIgnoreCase))
    allowOrigin.IsSome |> should equal true

[<Fact>]
let ``foreign origins do not receive a CORS allow header`` () =
    // Regression guard: if someone widens the policy to AllowAnyOrigin,
    // every random page on the internet could read API responses.
    use response = getWithOrigin "https://evil.example.com" "/health"
    let allowOrigin =
        response.Headers
        |> Seq.tryFind (fun h -> h.Key.Equals("Access-Control-Allow-Origin",
                                              System.StringComparison.OrdinalIgnoreCase))
    allowOrigin.IsNone |> should equal true
