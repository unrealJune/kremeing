module Kremeing.Api.Program

open System
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe

/// What this process does. K8s splits the workload across two
/// deployments so we can scale HTTP horizontally while keeping the
/// poller a singleton (otherwise N replicas would N× our upstream load).
type Role =
    | Api
    | Poller
    | All

let private parseRole (raw: string) =
    match (if isNull raw then "" else raw.Trim().ToLowerInvariant()) with
    | "" | "all" -> All
    | "api" -> Api
    | "poller" -> Poller
    | other -> failwithf "KREMEING_ROLE='%s' is not one of api | poller | all" other

let role () : Role =
    parseRole (Environment.GetEnvironmentVariable "KREMEING_ROLE")

/// Wires Giraffe with a Deps bag. Exposed so test hosts can call it
/// with their own stubs — `main` below uses production wiring.
///
/// Pipeline order:
///   1. CORS — must run before any handler that could 404 a preflight.
///   2. UseDefaultFiles → "/" rewrites to "/index.html" for static
///      file serving.
///   3. UseStaticFiles — serves files from wwwroot/ (the bundled web
///      client). Static lookups happen first so /index.html, /map.jsx,
///      /stores.js etc. don't fall into Giraffe and 404.
///   4. Giraffe — handles every API path that didn't match a file.
let configureApp (deps: HttpHandlers.Deps) (app: IApplicationBuilder) =
    app.UseCors() |> ignore
    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore
    app.UseGiraffe(HttpHandlers.webApp deps)

let configureServices (services: IServiceCollection) =
    services.AddGiraffe() |> ignore
    // Allow localhost origins so the web/ frontend (served on a different
    // port during dev) can call this API directly from the browser.
    services.AddCors(fun options ->
        options.AddDefaultPolicy(fun policy ->
            policy
                .SetIsOriginAllowed(fun origin ->
                    let u = Uri origin
                    u.Host = "localhost" || u.Host = "127.0.0.1")
                .AllowAnyHeader()
                .AllowAnyMethod()
            |> ignore))
    |> ignore

/// Wraps an HttpClient as our function-typed Send seam.
let private httpClientSend (client: HttpClient) : LiveApi.SendHttp =
    fun req ->
        async {
            let! response = client.SendAsync req |> Async.AwaitTask
            return response
        }

/// Runs Discovery on startup. Synchronous because both API and poller
/// are useless without a registry — better to fail loudly here than
/// serve empty responses for 5 minutes until the first poller tick.
let private discoverRegistry
        (logger: ILogger)
        (send: LiveApi.SendHttp)
        : Discovery.RegistryEntry list =
    logger.LogInformation "Discovery starting…"
    let page =
        match Discovery.enumerateCities send |> Async.RunSynchronously with
        | Ok p -> p
        | Error e ->
            logger.LogError("Discovery failed: {error}", sprintf "%A" e)
            { Cities = []; Stores = [] }
    logger.LogInformation(
        "Discovered {cities} cities, {directStores} direct-store links",
        page.Cities.Length, page.Stores.Length)

    let search key = LiveApi.searchByCityState send key
    let registry =
        Discovery.resolveRegistry search page
        |> Async.RunSynchronously
    logger.LogInformation("Resolved registry: {count} stores", registry.Length)
    registry

/// Picks an Observations adapter based on KREMEING_DATABASE_URL.
/// K8s deploys inject this env var (managed Postgres connection string);
/// local dev runs without it and gets the in-memory store. URL-style
/// `postgres://user:pass@host:port/db` and Npgsql key=value are both
/// accepted — Npgsql normalizes them.
let private buildObservations
        (logger: ILogger)
        : Composition.ObservationsAdapter =
    match Environment.GetEnvironmentVariable "KREMEING_DATABASE_URL" with
    | null | "" ->
        logger.LogWarning(
            "KREMEING_DATABASE_URL not set; using in-memory observations \
             (history will be lost on restart)")
        Composition.inMemoryAdapter (InMemoryObservations.create())
    | raw ->
        let cs = Npgsql.NpgsqlConnectionStringBuilder(raw).ToString()
        let csb = Npgsql.NpgsqlConnectionStringBuilder cs
        logger.LogInformation(
            "Using Postgres observations at {host}:{port}/{db}",
            csb.Host, csb.Port, csb.Database)
        Postgres.applySchema cs |> Async.AwaitTask |> Async.RunSynchronously
        Composition.postgresAdapter (Postgres.create cs)

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    configureServices builder.Services
    builder.Services.AddHttpClient() |> ignore

    let provider = builder.Services.BuildServiceProvider()
    let logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("startup")
    let httpClient = new HttpClient()
    httpClient.Timeout <- TimeSpan.FromSeconds 30.0
    let send = httpClientSend httpClient

    let activeRole = role ()
    logger.LogInformation("Starting in role={role}", string activeRole)

    let observations = buildObservations logger
    let registry = discoverRegistry logger send
    let deps = Composition.build send registry observations

    // Poller registration is gated by role — only one deployment runs
    // it, so multiple API replicas don't multiply our upstream load.
    let runsPoller =
        match activeRole with
        | Poller | All -> true
        | Api -> false

    if runsPoller then
        let pollerSearch key = LiveApi.searchByCityState send key
        builder.Services.AddSingleton<Poller.Config>(Poller.defaultConfig) |> ignore
        builder.Services.AddSingleton<Poller.PollerService>(fun sp ->
            let pLogger = sp.GetRequiredService<ILogger<Poller.PollerService>>()
            Poller.PollerService(
                registry,
                pollerSearch,
                deps.Record,
                Poller.defaultConfig,
                pLogger))
        |> ignore
        builder.Services.AddHostedService<Poller.PollerService>(fun sp ->
            sp.GetRequiredService<Poller.PollerService>())
        |> ignore

    let app = builder.Build()
    configureApp deps.Handlers app

    // Even a poller-only pod still serves /health so k8s liveness probes
    // succeed. The HTTP handlers' read-side ports (Status, History) work
    // identically because they share Postgres with the api deployment.
    app.Run()
    0
