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

    let getHotLight (getStatus: Ports.GetHotLightStatus) (rawId: string)
                    : HttpHandler =
        fun next ctx ->
            task {
                match Validation.parseStoreId rawId with
                | Error err -> return! writeError err next ctx
                | Ok storeId ->
                    let! result = getStatus storeId
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

    let getNearby
            (search: SearchNearby)
            (storeStatus: Ports.GetStoreStatus)
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

                match lat, lng with
                | None, _ ->
                    return! writeBadRequest "missing_query_param"
                                "lat is required and must be a number"
                                next ctx
                | _, None ->
                    return! writeBadRequest "missing_query_param"
                                "lng is required and must be a number"
                                next ctx
                | Some la, Some ln ->
                    let! result = search (la, ln)
                    match result with
                    | Error err -> return! writeError err next ctx
                    | Ok shops ->
                        let trimmed = shops |> List.truncate limit
                        let! dtos =
                            trimmed
                            |> List.map (nearbyStoreToDto storeStatus)
                            |> System.Threading.Tasks.Task.WhenAll
                        let response = {
                            query = { latitude = la; longitude = ln; limit = limit }
                            stores = dtos
                        }
                        return! json response next ctx
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

    // ──── webApp ────────────────────────────────────────────────────────

    /// All HTTP dependencies the webApp needs, bundled. Building this
    /// is the composition root's job — tests build their own.
    type Deps = {
        GetHotLightStatus: Ports.GetHotLightStatus
        SearchNearby: SearchNearby
        History: Ports.GetHistory
        Status: Ports.GetStoreStatus
        Now: unit -> DateTimeOffset
    }

    let webApp (deps: Deps) : HttpHandler =
        choose [
            GET >=> route "/health" >=> getHealth
            GET >=> route "/openapi.yaml" >=> getOpenApi
            GET >=> route "/docs" >=> getDocs
            GET >=> route "/stores/nearby" >=> getNearby deps.SearchNearby deps.Status
            GET >=> routef "/stores/%s/hot-light" (getHotLight deps.GetHotLightStatus)
            GET >=> routef "/stores/%s/history"
                        (getHistory deps.History deps.Now)
            GET >=> routef "/stores/%s/uptime"
                        (getUptime deps.History deps.Status deps.Now)
            setStatusCode 404
                >=> json { error = "not_found"; message = "Route not found." }
        ]
