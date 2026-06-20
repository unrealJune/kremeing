module Kremeing.Contract.Tests.DocsContractTests

open System.Net
open System.Net.Http
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``GET / serves the bundled web client, not a JSON 404`` () =
    // Regression guard: bare-domain visitors should land on the map UX
    // (web/index.html), not see {"error":"not_found"}. Static files are
    // wired in Program.configureApp via UseDefaultFiles + UseStaticFiles,
    // and the web/ source is linked into wwwroot/ at build time.
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/").Result
    response.StatusCode |> should equal HttpStatusCode.OK
    let ct = response.Content.Headers.ContentType.MediaType
    ct |> should equal "text/html"
    let body = response.Content.ReadAsStringAsync().Result
    // Markers in web/index.html that won't churn often.
    body |> should haveSubstring "<!DOCTYPE html>"
    body |> should haveSubstring "Krispy Kreme"

[<Fact>]
let ``GET /openapi.yaml returns 200 with the OpenAPI 3.1 spec`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/openapi.yaml").Result
    response.StatusCode |> should equal HttpStatusCode.OK
    let contentType = response.Content.Headers.ContentType.MediaType
    contentType |> should equal "application/yaml"

    let body = response.Content.ReadAsStringAsync().Result
    body |> should haveSubstring "openapi: 3.1.0"
    body |> should haveSubstring "Kremeing"

[<Fact>]
let ``OpenAPI spec documents every endpoint we ship`` () =
    // Regression guard: if you add a route without documenting it, this
    // test fails. The string match is intentionally loose so reorganizing
    // the YAML doesn't break the test.
    use client = TestHost.start (TestHost.Stubs.deps())
    let body = client.GetStringAsync("/openapi.yaml").Result
    for path in [
        "/health"
        "/stores/nearby"
        "/stores/search"
        "/stores/{id}/hot-light"
        "/stores/{id}/history"
        "/stores/{id}/uptime"
        "/vapid-public-key"
        "/subscriptions"
        "/device-subscriptions"
    ] do
        body |> should haveSubstring path

[<Fact>]
let ``OpenAPI spec documents every error code we emit`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let body = client.GetStringAsync("/openapi.yaml").Result
    for code in [
        "store_not_found"
        "upstream_unavailable"
        "invalid_store_id"
        "missing_query_param"
        "invalid_bucket"
        "invalid_coordinate"
        "range_too_wide"
        "rate_limited"
        "push_disabled"
        "invalid_subscription"
        "invalid_device_subscription"
    ] do
        body |> should haveSubstring code

[<Fact>]
let ``GET /docs returns 200 with a Redoc-rendered HTML page`` () =
    use client = TestHost.start (TestHost.Stubs.deps())
    let response = client.GetAsync("/docs").Result
    response.StatusCode |> should equal HttpStatusCode.OK
    let contentType = response.Content.Headers.ContentType.MediaType
    contentType |> should equal "text/html"

    let body = response.Content.ReadAsStringAsync().Result
    body |> should haveSubstring "<redoc"
    body |> should haveSubstring "/openapi.yaml"
