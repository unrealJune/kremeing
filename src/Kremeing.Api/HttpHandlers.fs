namespace Kremeing.Api

open System
open Microsoft.AspNetCore.Http
open Giraffe
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Core

/// HTTP layer. Handlers receive their dependencies as plain function
/// values (the port types from Core.Ports), so substituting a stub at
/// test time is just a different lambda — no DI container required.
module HttpHandlers =

    let private writeError (err: StoreError) : HttpHandler =
        let status = Mapping.errorToStatusCode err
        let body = Mapping.errorToDto err
        setStatusCode status >=> json body

    let private writeBadRequest (code: string) (message: string) : HttpHandler =
        setStatusCode 400
        >=> json { error = code; message = message }

    /// Hard ceiling on `until - since`. Anything wider gets rejected
    /// before it touches the database — protects the connection pool
    /// from `since=1900-01-01` style abuse.
    let MaxRangeDays = 90.0

    let private validateRange (since: DateTimeOffset) (until: DateTimeOffset)
        : Result<unit, HttpHandler> =
        if until <= since then
            Error (writeBadRequest "invalid_range" "until must be after since")
        elif (until - since).TotalDays > MaxRangeDays then
            Error (writeBadRequest ErrorCodes.RangeTooWide
                    (sprintf "Range cannot exceed %.0f days" MaxRangeDays))
        else
            Ok ()

    /// Per-IP token-bucket gate. Short-circuits with 429 + Retry-After
    /// when the bucket for `RemoteIpAddress` is empty. `Forwarded-For`
    /// resolution is the host's job (UseForwardedHeaders); this looks
    /// at whatever .NET reports as the connection peer.
    let rateLimit (limiter: RateLimit.Limiter) (now: unit -> DateTimeOffset)
        : HttpHandler =
        fun next ctx ->
            let key =
                match ctx.Connection.RemoteIpAddress with
                | null -> "unknown"
                | ip -> ip.ToString()
            if limiter.TryAcquire(key, now()) then
                next ctx
            else
                (setStatusCode 429
                 >=> setHttpHeader "Retry-After" "60"
                 >=> json
                     { error = ErrorCodes.RateLimited
                       message = "Too many requests; slow down." }) earlyReturn ctx

    // ──── /health ───────────────────────────────────────────────────────

    let getHealth : HttpHandler =
        json {| status = "ok" |}

    // ──── /openapi.yaml + /docs ─────────────────────────────────────────

    /// Spec is embedded as a resource so tests, dev, and prod all serve
    /// the same bytes regardless of the working directory.
    let private openApiSpec : string =
        let asm = System.Reflection.Assembly.GetExecutingAssembly()
        let resName = "openapi.yaml"
        // F# default resource naming differs by SDK; pick whichever matches.
        let candidate =
            asm.GetManifestResourceNames()
            |> Array.tryFind (fun n -> n.EndsWith resName)
        match candidate with
        | None ->
            failwithf "openapi.yaml resource not found in assembly. \
                       Available resources: %A"
                      (asm.GetManifestResourceNames())
        | Some name ->
            use stream = asm.GetManifestResourceStream name
            use reader = new System.IO.StreamReader(stream)
            reader.ReadToEnd()

    let getOpenApi : HttpHandler =
        setContentType "application/yaml; charset=utf-8"
        >=> setBodyFromString openApiSpec

    [<Literal>]
    let private docsHtml = """<!DOCTYPE html>
<html>
<head>
  <title>Kremeing API</title>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <style>body { margin: 0; padding: 0; }</style>
</head>
<body>
  <redoc spec-url="/openapi.yaml"></redoc>
  <script src="https://cdn.jsdelivr.net/npm/redoc@2/bundles/redoc.standalone.js"></script>
</body>
</html>"""

    let getDocs : HttpHandler =
        setContentType "text/html; charset=utf-8"
        >=> setBodyFromString docsHtml


    // ──── /stores/{id}/hot-light ────────────────────────────────────────

    let getHotLight
            (getStatus: Ports.GetHotLightStatus)
            (cache: Cache.Cache<int, HotLightObservation>)
            (now: unit -> DateTimeOffset)
            (rawId: string)
            : HttpHandler =
        fun next ctx ->
            task {
                match Validation.parseStoreId rawId with
                | Error err -> return! writeError err next ctx
                | Ok storeId ->
                    let (StoreId sid) = storeId
                    // Read-through cache: at most one upstream call
                    // per shopId per TTL window across all clients.
                    let! result =
                        cache.GetOrAdd sid (now()) (fun _ -> getStatus storeId)
                    match result with
                    | Ok obs ->
                        return! json (Mapping.observationToDto obs) next ctx
                    | Error err ->
                        return! writeError err next ctx
            }

    // ──── /stores/nearby ────────────────────────────────────────────────

    /// Composition root provides this — production wires it to LiveApi;
    /// tests stub it. Returns up to 12 KK shops nearest to (lat, lng).
    type SearchNearby =
        float * float -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>

    /// Free-text query — KK's API accepts a city/state ("Seattle, WA")
    /// or a zip code ("98109"). Same cap of 12 results.
    type SearchByQuery =
        string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>

    let private nearbyStoreToDto
            (storeStatus: Ports.GetStoreStatus)
            (shop: LiveApi.KrispyShopDto)
            : System.Threading.Tasks.Task<NearbyStoreDto> =
        task {
            // Enrich upstream's snapshot with our temporal context.
            // If we've never observed this store, emit nulls — clients
            // interpret that as "not yet tracked".
            let! cached = storeStatus (StoreId shop.shopId)
            let lastFlipped, firstSeen =
                match cached with
                | Ok s ->
                    let lf =
                        s.LastFlippedAt
                        |> Option.map Nullable
                        |> Option.defaultValue (Nullable())
                    lf, Nullable s.FirstObservedAt
                | Error _ -> Nullable(), Nullable()
            return {
                id = shop.shopId
                name = sprintf "Krispy Kreme %s" shop.shopName
                address = sprintf "%s, %s, %s %s" shop.address1 shop.city shop.state shop.zipCode
                latitude = shop.latitude
                longitude = shop.longitude
                distanceMiles = shop.distance
                currentStatus =
                    if shop.hotLightOn then StatusValues.On else StatusValues.Off
                lastFlippedAt = lastFlipped
                firstObservedAt = firstSeen
            }
        }

    let private parseFloat (raw: string) =
        let ok, v =
            Double.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture)
        if ok then Some v else None

    /// Cap `limit` at a defensive ceiling. Upstream caps at 12 anyway,
    /// so anything above that is functionally a no-op — but rejecting
    /// out-of-range explicitly avoids surprising log entries.
    [<Literal>]
    let MaxNearbyLimit = 50

    /// Quantize coordinates to ~1 km resolution so geographically
    /// close clients hit the same cache key. (1° lat ≈ 111 km, so two
    /// decimal places ≈ 1.1 km — about a city block.)
    let private quantize (lat: float) (lng: float) (limit: int) : int * int * int =
        int (System.Math.Round(lat * 100.0)),
        int (System.Math.Round(lng * 100.0)),
        limit

    let getNearby
            (search: SearchNearby)
            (storeStatus: Ports.GetStoreStatus)
            (cache: Cache.Cache<int * int * int, NearbyResponseDto>)
            (now: unit -> DateTimeOffset)
            : HttpHandler =
        fun next ctx ->
            task {
                let lat = ctx.TryGetQueryStringValue "lat" |> Option.bind parseFloat
                let lng = ctx.TryGetQueryStringValue "lng" |> Option.bind parseFloat
                let limit =
                    ctx.TryGetQueryStringValue "limit"
                    |> Option.bind (fun s ->
                        match Int32.TryParse s with
                        | true, n when n > 0 -> Some n
                        | _ -> None)
                    |> Option.defaultValue 12
                    |> min MaxNearbyLimit

                let validCoord (v: float) (lo: float) (hi: float) =
                    not (System.Double.IsNaN v || System.Double.IsInfinity v)
                    && v >= lo && v <= hi

                match lat, lng with
                | None, _ ->
                    return! writeBadRequest "missing_query_param"
                                "lat is required and must be a number"
                                next ctx
                | _, None ->
                    return! writeBadRequest "missing_query_param"
                                "lng is required and must be a number"
                                next ctx
                | Some la, _ when not (validCoord la -90.0 90.0) ->
                    return! writeBadRequest "invalid_coordinate"
                                "lat must be a finite number in [-90, 90]"
                                next ctx
                | _, Some ln when not (validCoord ln -180.0 180.0) ->
                    return! writeBadRequest "invalid_coordinate"
                                "lng must be a finite number in [-180, 180]"
                                next ctx
                | Some la, Some ln ->
                    let key = quantize la ln limit
                    let n = now()
                    let buildResponse () =
                        task {
                            let! result = search (la, ln)
                            match result with
                            | Error err -> return Error err
                            | Ok shops ->
                                let trimmed = shops |> List.truncate limit
                                let! dtos =
                                    trimmed
                                    |> List.map (nearbyStoreToDto storeStatus)
                                    |> System.Threading.Tasks.Task.WhenAll
                                let response : NearbyResponseDto = {
                                    query = { latitude = la; longitude = ln; limit = limit }
                                    stores = dtos
                                }
                                return Ok response
                        }
                    let! result =
                        cache.GetOrAdd key n (fun _ ->
                            buildResponse () |> Async.AwaitTask)
                    match result with
                    | Ok r -> return! json r next ctx
                    | Error err -> return! writeError err next ctx
            }

    // ──── /stores/search ────────────────────────────────────────────────

    [<Literal>]
    let MaxSearchQueryLen = 100

    let getSearch
            (searchByQuery: SearchByQuery)
            (storeStatus: Ports.GetStoreStatus)
            (cache: Cache.Cache<string * int, SearchResponseDto>)
            (now: unit -> DateTimeOffset)
            : HttpHandler =
        fun next ctx ->
            task {
                let qOpt = ctx.TryGetQueryStringValue "q"
                let limit =
                    ctx.TryGetQueryStringValue "limit"
                    |> Option.bind (fun s ->
                        match Int32.TryParse s with
                        | true, n when n > 0 -> Some n
                        | _ -> None)
                    |> Option.defaultValue 12
                    |> min MaxNearbyLimit

                match qOpt with
                | None ->
                    return! writeBadRequest "missing_query_param"
                                "q is required (zip or 'City, ST')" next ctx
                | Some raw when System.String.IsNullOrWhiteSpace raw ->
                    return! writeBadRequest "missing_query_param"
                                "q is required (zip or 'City, ST')" next ctx
                | Some raw when raw.Length > MaxSearchQueryLen ->
                    return! writeBadRequest "invalid_query"
                                (sprintf "q must be ≤%d chars" MaxSearchQueryLen)
                                next ctx
                | Some raw ->
                    let q = raw.Trim()
                    // Cache key normalizes whitespace + case so
                    // "Seattle, WA" and "seattle, wa" share an entry.
                    let key = (q.ToLowerInvariant(), limit)
                    let n = now()
                    let build () =
                        task {
                            let! result = searchByQuery q
                            match result with
                            | Error err -> return Error err
                            | Ok shops ->
                                let trimmed = shops |> List.truncate limit
                                let! dtos =
                                    trimmed
                                    |> List.map (nearbyStoreToDto storeStatus)
                                    |> System.Threading.Tasks.Task.WhenAll
                                let response : SearchResponseDto = {
                                    query = { q = q; limit = limit }
                                    stores = dtos
                                }
                                return Ok response
                        }
                    let! result = cache.GetOrAdd key n (fun _ -> build () |> Async.AwaitTask)
                    match result with
                    | Ok response -> return! json response next ctx
                    | Error err -> return! writeError err next ctx
            }

    // ──── /stores/{id}/history ──────────────────────────────────────────

    let private flipToDto (obs: HotLightObservation) : HotLightFlipDto =
        let (StoreId sid) = obs.StoreId
        { storeId = sid
          status = Mapping.statusToWire obs.Status
          observedAt = obs.ObservedAt }

    let private parseDateTimeOffset (raw: string) =
        let ok, v =
            DateTimeOffset.TryParse(raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
        if ok then Some v else None

    let getHistory
            (history: Ports.GetHistory)
            (now: unit -> DateTimeOffset)
            (rawId: string)
            : HttpHandler =
        fun next ctx ->
            task {
                match Validation.parseStoreId rawId with
                | Error err -> return! writeError err next ctx
                | Ok storeId ->
                    let until =
                        ctx.TryGetQueryStringValue "until"
                        |> Option.bind parseDateTimeOffset
                        |> Option.defaultValue (now())
                    let since =
                        ctx.TryGetQueryStringValue "since"
                        |> Option.bind parseDateTimeOffset
                        |> Option.defaultValue (until - TimeSpan.FromDays 7.0)
                    match validateRange since until with
                    | Error h -> return! h next ctx
                    | Ok () ->
                    let! result = history (storeId, since, until)
                    match result with
                    | Error err -> return! writeError err next ctx
                    | Ok flips ->
                        let (StoreId sid) = storeId
                        let response : HotLightHistoryDto = {
                            storeId = sid
                            rangeStart = since
                            rangeEnd = until
                            flips = flips |> List.map flipToDto |> List.toArray
                        }
                        return! json response next ctx
            }

    // ──── /stores/{id}/uptime ───────────────────────────────────────────

    let private bucketSpan (label: string) =
        match label with
        | s when s = BucketSizes.Hour -> Some (TimeSpan.FromHours 1.0)
        | s when s = BucketSizes.Day -> Some (TimeSpan.FromDays 1.0)
        | _ -> None

    let private uptimeBucketToDto (b: Uptime.Bucket) : UptimeBucketDto = {
        startUtc = b.StartUtc
        endUtc = b.EndUtc
        onSeconds = b.OnSeconds
        offSeconds = b.OffSeconds
        observedSeconds = b.ObservedSeconds
        totalSeconds = b.TotalSeconds
        fractionOn = b.FractionOn
    }

    let getUptime
            (history: Ports.GetHistory)
            (storeStatus: Ports.GetStoreStatus)
            (now: unit -> DateTimeOffset)
            (rawId: string)
            : HttpHandler =
        fun next ctx ->
            task {
                match Validation.parseStoreId rawId with
                | Error err -> return! writeError err next ctx
                | Ok storeId ->
                    let bucketLabel =
                        ctx.TryGetQueryStringValue "bucket"
                        |> Option.defaultValue BucketSizes.Hour
                    match bucketSpan bucketLabel with
                    | None ->
                        return! writeBadRequest "invalid_bucket"
                                    "bucket must be 'hour' or 'day'"
                                    next ctx
                    | Some span ->
                        let until =
                            ctx.TryGetQueryStringValue "until"
                            |> Option.bind parseDateTimeOffset
                            |> Option.defaultValue (now())
                        let defaultLookback =
                            if bucketLabel = BucketSizes.Hour then TimeSpan.FromHours 24.0
                            else TimeSpan.FromDays 30.0
                        let since =
                            ctx.TryGetQueryStringValue "since"
                            |> Option.bind parseDateTimeOffset
                            |> Option.defaultValue (until - defaultLookback)

                        match validateRange since until with
                        | Error h -> return! h next ctx
                        | Ok () ->

                        // Status before history: if the store is unknown to
                        // us, return 404 so clients can distinguish "no data
                        // yet" from "store doesn't exist".
                        let! statusResult = storeStatus storeId
                        match statusResult with
                        | Error err -> return! writeError err next ctx
                        | Ok status ->
                            let! historyResult = history (storeId, since, until)
                            match historyResult with
                            | Error err -> return! writeError err next ctx
                            | Ok flips ->
                                let buckets =
                                    Uptime.bucketize flips status.LastPolledAt since until span
                                    |> List.map uptimeBucketToDto
                                    |> List.toArray
                                let (StoreId sid) = storeId
                                let response : UptimeResponseDto = {
                                    storeId = sid
                                    bucket = bucketLabel
                                    rangeStart = since
                                    rangeEnd = until
                                    buckets = buckets
                                }
                                return! json response next ctx
            }

    // ──── push subscriptions ────────────────────────────────────────────

    /// Subset of Deps that the three push endpoints need. When
    /// `Deps.Push = None` (no Postgres + VAPID configured), every push
    /// endpoint short-circuits with 503 push_disabled — the web client
    /// then falls back to in-page polling.
    type PushDeps = {
        Subscribe: Ports.SubscribePush
        Unsubscribe: Ports.UnsubscribePush
        VapidPublicKey: string
    }

    let private writePushDisabled : HttpHandler =
        setStatusCode 503
        >=> json
            { error = ErrorCodes.PushDisabled
              message = "Push notifications are not configured on this server." }

    let getVapidPublicKey (push: PushDeps option) : HttpHandler =
        match push with
        | None -> writePushDisabled
        | Some p ->
            json ({ publicKey = p.VapidPublicKey } : VapidPublicKeyResponseDto)

    let postSubscription (push: PushDeps option) : HttpHandler =
        fun next ctx ->
            task {
                match push with
                | None -> return! writePushDisabled next ctx
                | Some p ->
                    let! body = ctx.BindJsonAsync<SubscribeRequestDto>()
                    let bad = isNull (box body)
                              || isNull (box body.subscription)
                              || isNull (box body.subscription.keys)
                              || System.String.IsNullOrWhiteSpace body.subscription.endpoint
                              || System.String.IsNullOrWhiteSpace body.subscription.keys.p256dh
                              || System.String.IsNullOrWhiteSpace body.subscription.keys.auth
                              || body.storeId <= 0
                    if bad then
                        return! writeBadRequest ErrorCodes.InvalidSubscription
                                    "subscription.endpoint, keys.p256dh, keys.auth, and a positive storeId are required"
                                    next ctx
                    else
                        let domainSub = {
                            Endpoint = body.subscription.endpoint
                            P256dh = body.subscription.keys.p256dh
                            Auth = body.subscription.keys.auth
                        }
                        let! result = p.Subscribe (StoreId body.storeId, domainSub)
                        match result with
                        | Error err -> return! writeError err next ctx
                        | Ok (PushSubscriptionId id) ->
                            ctx.SetStatusCode 201
                            let response : SubscribeResponseDto =
                                { id = id; storeId = body.storeId }
                            return! json response next ctx
            }

    let deleteSubscription (push: PushDeps option) : HttpHandler =
        fun next ctx ->
            task {
                match push with
                | None -> return! writePushDisabled next ctx
                | Some p ->
                    let! body = ctx.BindJsonAsync<UnsubscribeRequestDto>()
                    let bad = isNull (box body)
                              || System.String.IsNullOrWhiteSpace body.endpoint
                              || body.storeId <= 0
                    if bad then
                        return! writeBadRequest ErrorCodes.InvalidSubscription
                                    "storeId and endpoint are required"
                                    next ctx
                    else
                        let! result = p.Unsubscribe (StoreId body.storeId, body.endpoint)
                        match result with
                        | Error err -> return! writeError err next ctx
                        | Ok () ->
                            ctx.SetStatusCode 204
                            return! next ctx
            }

    // ──── webApp ────────────────────────────────────────────────────────

    /// All HTTP dependencies the webApp needs, bundled. Building this
    /// is the composition root's job — tests build their own.
    type Deps = {
        GetHotLightStatus: Ports.GetHotLightStatus
        SearchNearby: SearchNearby
        SearchByQuery: SearchByQuery
        History: Ports.GetHistory
        Status: Ports.GetStoreStatus
        Now: unit -> DateTimeOffset
        /// Caches and rate-limit shield the upstream (api.krispykreme.com)
        /// from being hammered by API consumers. Tests use permissive
        /// instances; production wires real ones in Composition.
        HotLightCache: Cache.Cache<int, HotLightObservation>
        NearbyCache: Cache.Cache<int * int * int, NearbyResponseDto>
        SearchCache: Cache.Cache<string * int, SearchResponseDto>
        ProxyRateLimit: RateLimit.Limiter
        /// Push subscription handlers. None when push is disabled
        /// (no Postgres + VAPID config) — endpoints return 503 in
        /// that case and clients fall back to in-page polling.
        Push: PushDeps option
    }

    let webApp (deps: Deps) : HttpHandler =
        // Apply the per-IP rate limiter only to the proxy endpoints
        // (/nearby + /hot-light) — they're the ones that go upstream.
        // Read-only endpoints over our own DB don't need it.
        let limited = rateLimit deps.ProxyRateLimit deps.Now
        choose [
            GET >=> route "/health" >=> getHealth
            GET >=> route "/openapi.yaml" >=> getOpenApi
            GET >=> route "/docs" >=> getDocs
            GET >=> route "/stores/nearby"
                >=> limited
                >=> getNearby deps.SearchNearby deps.Status deps.NearbyCache deps.Now
            GET >=> route "/stores/search"
                >=> limited
                >=> getSearch deps.SearchByQuery deps.Status deps.SearchCache deps.Now
            GET >=> routef "/stores/%s/hot-light"
                        (fun id ->
                            limited >=> getHotLight deps.GetHotLightStatus deps.HotLightCache deps.Now id)
            GET >=> routef "/stores/%s/history"
                        (getHistory deps.History deps.Now)
            GET >=> routef "/stores/%s/uptime"
                        (getUptime deps.History deps.Status deps.Now)
            GET    >=> route "/vapid-public-key" >=> getVapidPublicKey deps.Push
            POST   >=> route "/subscriptions"    >=> postSubscription deps.Push
            DELETE >=> route "/subscriptions"    >=> deleteSubscription deps.Push
            setStatusCode 404
                >=> json { error = "not_found"; message = "Route not found." }
        ]
