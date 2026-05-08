namespace Kremeing.Api

open System
open System.Net.Http
open System.Text.Json
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Adapter for Krispy Kreme's public store API (api.krispykreme.com).
/// Stays free of ASP.NET concerns: HTTP is supplied as a function so
/// tests can stub the wire without touching `HttpClient`.
module LiveApi =

    /// Function-typed HTTP seam. Production wires it to HttpClient;
    /// tests substitute a lambda that returns canned responses.
    type SendHttp = HttpRequestMessage -> Async<HttpResponseMessage>

    /// Subset of the JSON Krispy Kreme returns from /shops and
    /// /shops/search. Only fields we actually use are declared —
    /// extras are ignored. Field names match KK's camelCase exactly.
    [<CLIMutable>]
    type KrispyShopDto = {
        shopId: int
        shopName: string
        shopUrl: string
        address1: string
        city: string
        state: string
        zipCode: string
        latitude: float
        longitude: float
        hotLightOn: bool
        hoursDescriptionHotlight: string[]
        distance: float
    }

    let private jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Pure: bytes → list of shops, or a parse error string. IO and
    /// status-code handling live in the executor below.
    let parseShops (json: string) : Result<KrispyShopDto list, string> =
        try
            let arr = JsonSerializer.Deserialize<KrispyShopDto[]>(json, jsonOptions)
            if isNull arr then
                Ok []
            else
                Ok (Array.toList arr)
        with
        | :? JsonException as ex -> Error (sprintf "JSON parse failed: %s" ex.Message)
        | ex -> Error (sprintf "JSON parse failed: %s" ex.Message)

    /// KK shop → our domain Store. Concatenates address parts the way
    /// clients want them rendered; lossy by design (drops fundraising
    /// urls, OLO ids, etc., which are not in our contract).
    let shopToStore (shop: KrispyShopDto) : Store = {
        Id = StoreId shop.shopId
        Name = sprintf "Krispy Kreme %s" shop.shopName
        Address = sprintf "%s, %s, %s %s" shop.address1 shop.city shop.state shop.zipCode
        Location = { Latitude = shop.latitude; Longitude = shop.longitude }
    }

    let shopToObservation (now: DateTimeOffset) (shop: KrispyShopDto)
        : HotLightObservation =
        { StoreId = StoreId shop.shopId
          Status = if shop.hotLightOn then On else Off
          ObservedAt = now }

    [<Literal>]
    let private krispyKremeOrigin = "https://www.krispykreme.com"

    [<Literal>]
    let private apiHost = "https://api.krispykreme.com"

    let private buildRequest (path: string) : HttpRequestMessage =
        let req = new HttpRequestMessage(HttpMethod.Get, apiHost + path)
        req.Headers.TryAddWithoutValidation("Origin", krispyKremeOrigin) |> ignore
        req.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore
        req

    let private executeAsShops (send: SendHttp) (req: HttpRequestMessage)
        : Async<Result<KrispyShopDto list, StoreError>> =
        async {
            try
                let! response = send req
                use _ = response
                if not response.IsSuccessStatusCode then
                    let code = int response.StatusCode
                    return Error (UpstreamUnavailable (sprintf "Krispy Kreme returned HTTP %d" code))
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    match parseShops body with
                    | Ok shops -> return Ok shops
                    | Error msg -> return Error (UpstreamUnavailable msg)
            with
            | ex -> return Error (UpstreamUnavailable ex.Message)
        }

    /// "Seattle, WA" or "98134" — KK accepts both shapes. Up to 12 shops.
    let searchByCityState (send: SendHttp) (cityStateZip: string)
        : Async<Result<KrispyShopDto list, StoreError>> =
        let encoded = Uri.EscapeDataString cityStateZip
        executeAsShops send (buildRequest (sprintf "/shops/search?cityStateZip=%s" encoded))

    /// Up to 12 shops nearest to the given coordinates. Distance field
    /// in each result is populated by upstream.
    let searchByLocation (send: SendHttp) (latitude: float) (longitude: float)
        : Async<Result<KrispyShopDto list, StoreError>> =
        let path =
            // Invariant culture keeps decimal point as '.', not locale comma.
            sprintf "/shops?latitude=%s&longitude=%s"
                (latitude.ToString System.Globalization.CultureInfo.InvariantCulture)
                (longitude.ToString System.Globalization.CultureInfo.InvariantCulture)
        executeAsShops send (buildRequest path)

    /// Adapts the search-based upstream into our per-store port. The
    /// caller supplies `lookupQuery` because only the registry knows
    /// what cityStateZip a given StoreId belongs to. `now` is injected
    /// so tests can assert exact ObservedAt.
    let getHotLightStatus
            (send: SendHttp)
            (lookupQuery: StoreId -> Async<Result<string, StoreError>>)
            (now: unit -> DateTimeOffset)
            : Ports.GetHotLightStatus =
        fun storeId ->
            async {
                let! queryResult = lookupQuery storeId
                match queryResult with
                | Error err -> return Error err
                | Ok query ->
                    let! shopsResult = searchByCityState send query
                    match shopsResult with
                    | Error err -> return Error err
                    | Ok shops ->
                        let (StoreId target) = storeId
                        match shops |> List.tryFind (fun s -> s.shopId = target) with
                        | Some shop -> return Ok (shopToObservation (now()) shop)
                        | None -> return Error (StoreNotFound storeId)
            }
