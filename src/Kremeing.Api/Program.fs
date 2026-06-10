module Kremeing.Api.Program

open System
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe
open Kremeing.Contracts.Domain

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
/// Reuses DiscoveryRefresh.runDiscovery so startup and the periodic
/// refresh resolve the registry through the exact same path.
let private discoverRegistry
        (logger: ILogger)
        (send: LiveApi.SendHttp)
        (search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>)
        : Discovery.RegistryEntry list =
    logger.LogInformation "Discovery starting…"
    let registry =
        match DiscoveryRefresh.runDiscovery send search |> Async.RunSynchronously with
        | Ok r -> r
        | Error e ->
            logger.LogError("Discovery failed: {error}", sprintf "%A" e)
            []
    logger.LogInformation("Resolved registry: {count} stores", registry.Length)
    registry

/// Picks an Observations adapter based on KREMEING_DATABASE_URL.
/// K8s deploys inject this env var (managed Postgres connection string);
/// local dev runs without it and gets the in-memory store. URL-style
/// `postgres://user:pass@host:port/db` and Npgsql key=value are both
/// accepted — Npgsql normalizes them.
let private buildObservations
        (logger: ILogger)
        : Composition.ObservationsAdapter * string option =
    match Environment.GetEnvironmentVariable "KREMEING_DATABASE_URL" with
    | null | "" ->
        logger.LogWarning(
            "KREMEING_DATABASE_URL not set; using in-memory observations \
             (history will be lost on restart)")
        Composition.inMemoryAdapter (InMemoryObservations.create()), None
    | raw ->
        let cs = Npgsql.NpgsqlConnectionStringBuilder(raw).ToString()
        let csb = Npgsql.NpgsqlConnectionStringBuilder cs
        logger.LogInformation(
            "Using Postgres observations at {host}:{port}/{db}",
            csb.Host, csb.Port, csb.Database)
        Postgres.applySchema cs |> Async.AwaitTask |> Async.RunSynchronously
        Composition.postgresAdapter (Postgres.create cs), Some cs

/// Active pattern matching a non-null, non-blank string. Used by
/// the push-feature config code below to keep the env-var triage
/// readable.
let private (|NotNullOrWhitespace|_|) (s: string) =
    if System.String.IsNullOrWhiteSpace s then None else Some s

/// Push notifications require both a Postgres connection (to store
/// subscriptions) AND a VAPID keypair. Either one missing → push is
/// disabled cleanly; the API endpoints return 503 push_disabled and
/// clients fall back to in-page polling. Generate VAPID keys with
/// `dotnet fsi scripts/generate-vapid.fsx` and set the env vars.
let private buildPushFeature
        (logger: ILogger)
        (postgresCs: string option)
        : Composition.PushFeature option =
    let pub  = Environment.GetEnvironmentVariable "KREMEING_VAPID_PUBLIC_KEY"
    let priv = Environment.GetEnvironmentVariable "KREMEING_VAPID_PRIVATE_KEY"
    let subj =
        match Environment.GetEnvironmentVariable "KREMEING_VAPID_SUBJECT" with
        | NotNullOrWhitespace s -> s
        | _ -> "mailto:hotlight@kremeing.invalid"
    match postgresCs, pub, priv with
    | Some cs, NotNullOrWhitespace pub', NotNullOrWhitespace priv' ->
        logger.LogInformation "Push notifications enabled (Postgres + VAPID present)."
        let vapid : PushDispatch.Vapid =
            { Subject = subj; PublicKey = pub'; PrivateKey = priv' }
        Some {
            Subscriptions = Postgres.createPushSubscriptions cs
            Vapid = vapid
            Dispatch = PushDispatch.create vapid
        }
    | _ ->
        let why =
            match postgresCs, pub, priv with
            | None,    _, _                 -> "Postgres not configured"
            | _, NotNullOrWhitespace _, _   -> "KREMEING_VAPID_PRIVATE_KEY missing"
            | _, _, _                       -> "KREMEING_VAPID_PUBLIC_KEY missing"
        logger.LogWarning(
            "Push notifications disabled ({reason}); /subscriptions endpoints \
             will return 503", why)
        None

/// Resolves the periodic discovery-refresh interval. Defaults to 12h
/// (twice daily); `KREMEING_DISCOVERY_REFRESH_INTERVAL` overrides it with a
/// positive number of hours (e.g. "6" or "0.5"). Invalid values fall back
/// to the default with a warning rather than failing startup.
let private discoveryRefreshConfig (logger: ILogger) : DiscoveryRefresh.Config =
    match Environment.GetEnvironmentVariable "KREMEING_DISCOVERY_REFRESH_INTERVAL" with
    | NotNullOrWhitespace raw ->
        match
            Double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture)
        with
        | true, hours when hours > 0.0 ->
            let interval = TimeSpan.FromHours hours
            logger.LogInformation(
                "Discovery refresh interval set to {interval} (from \
                 KREMEING_DISCOVERY_REFRESH_INTERVAL)", interval)
            { Interval = interval }
        | _ ->
            logger.LogWarning(
                "KREMEING_DISCOVERY_REFRESH_INTERVAL='{raw}' is not a positive number \
                 of hours; using default {default}",
                raw, DiscoveryRefresh.defaultConfig.Interval)
            DiscoveryRefresh.defaultConfig
    | _ -> DiscoveryRefresh.defaultConfig

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

    let observations, postgresCs = buildObservations logger
    let push = buildPushFeature logger postgresCs
    let search key = LiveApi.searchByCityState send key
    let registry = discoverRegistry logger send search
    // Shared, mutable registry: startup seeds it; the periodic refresh
    // swaps it. Both the poller and the read handlers read through the
    // holder so a refresh is visible everywhere at once.
    let registryHolder = Registry.Holder(registry, DateTimeOffset.UtcNow)
    let deps = Composition.build send registryHolder observations push

    // Poller + discovery refresh are gated by role — only one deployment
    // runs them, so multiple API replicas don't multiply our upstream load.
    let runsPoller =
        match activeRole with
        | Poller | All -> true
        | Api -> false

    if runsPoller then
        builder.Services.AddSingleton<Poller.Config>(Poller.defaultConfig) |> ignore
        builder.Services.AddSingleton<Poller.PollerService>(fun sp ->
            let pLogger = sp.GetRequiredService<ILogger<Poller.PollerService>>()
            new Poller.PollerService(
                registryHolder.Get,
                search,
                deps.Record,
                deps.NotifyFlipOn,
                Poller.defaultConfig,
                pLogger))
        |> ignore
        builder.Services.AddHostedService<Poller.PollerService>(fun sp ->
            sp.GetRequiredService<Poller.PollerService>())
        |> ignore

        // Periodic discovery refresh: self-heals the registry and (critically)
        // refuses to replace a good registry with an empty/failed one.
        let refreshConfig = discoveryRefreshConfig logger
        builder.Services.AddHostedService<DiscoveryRefresh.DiscoveryRefreshService>(fun sp ->
            let rLogger =
                sp.GetRequiredService<ILogger<DiscoveryRefresh.DiscoveryRefreshService>>()
            new DiscoveryRefresh.DiscoveryRefreshService(
                registryHolder,
                send,
                search,
                refreshConfig,
                rLogger))
        |> ignore

    let app = builder.Build()
    configureApp deps.Handlers app

    // Even a poller-only pod still serves /health so k8s liveness probes
    // succeed. The HTTP handlers' read-side ports (Status, History) work
    // identically because they share Postgres with the api deployment.
    app.Run()
    0
