namespace Kremeing.Api

open System
open System.Net.Http
open System.Text.RegularExpressions
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Enumerates every Krispy Kreme location in the US via
/// site.krispykreme.com (the public Yext-rendered store directory),
/// then resolves each (city, state) pair to upstream `shopId`s by
/// querying api.krispykreme.com.
///
/// site structure:
///   /                 → links to states or direct stores (single-store states)
///   /{state}          → links to cities or direct stores (single-store cities)
///   /{state}/{city}   → links to individual stores         (skipped — we use the
///                                                           live API instead)
module Discovery =

    /// A scraped reference to a city. Slug values are lowercase URL slugs
    /// (e.g. "seattle"); we keep them human-readable for logs.
    type ScrapedCity = { State: string; City: string }

    /// A direct-to-store link found mid-scrape (single-store states or cities).
    /// We pass these through untouched so the registry resolver can match them.
    type ScrapedStore = { State: string; City: string; Slug: string }

    type ScrapedPage = {
        Cities: ScrapedCity list
        Stores: ScrapedStore list
    }

    // ──── pure parsers ──────────────────────────────────────────────────

    let private storeHrefRegex =
        Regex(
            @"href=""(?:\.\./?)*(?<state>[a-z]{2})/(?<city>[a-z0-9-]+)/(?<slug>[a-z0-9-]+)""",
            RegexOptions.Compiled)

    let private cityOrStateHrefRegex =
        // Captures `../wa` (state-only on root) and `wa/seattle` (city on state
        // page). Excludes URLs with a third segment — those are stores and are
        // handled by storeHrefRegex.
        Regex(
            @"href=""(?:\.\./?)?(?<state>[a-z]{2})(?:/(?<city>[a-z0-9-]+))?""(?!/)",
            RegexOptions.Compiled)

    /// Parses any page returned by site.krispykreme.com and extracts the store
    /// and city links it contains. Same parser handles root, state, and city
    /// pages — we deduplicate by call site rather than enforcing layout here.
    let parsePage (html: string) : ScrapedPage =
        let stores =
            storeHrefRegex.Matches html
            |> Seq.cast<Match>
            |> Seq.map (fun m ->
                { State = m.Groups.["state"].Value
                  City = m.Groups.["city"].Value
                  Slug = m.Groups.["slug"].Value })
            |> Seq.distinct
            |> Seq.toList

        let cities =
            cityOrStateHrefRegex.Matches html
            |> Seq.cast<Match>
            |> Seq.choose (fun m ->
                let state = m.Groups.["state"].Value
                let city = m.Groups.["city"].Value
                if String.IsNullOrEmpty city then None
                else Some { State = state; City = city })
            |> Seq.distinct
            |> Seq.toList

        { Cities = cities; Stores = stores }

    /// Pulls just the state slugs from the root page. Single-store states
    /// expose `../wa/foo/bar` directly — those still imply state="wa", so we
    /// also accumulate from the storeHrefRegex matches.
    let parseRootStates (html: string) : string list =
        let fromStateLinks =
            Regex.Matches(html, @"href=""\.\./(?<state>[a-z]{2})""(?!/)")
            |> Seq.cast<Match>
            |> Seq.map (fun m -> m.Groups.["state"].Value)

        let fromDirectStoreLinks =
            storeHrefRegex.Matches html
            |> Seq.cast<Match>
            |> Seq.map (fun m -> m.Groups.["state"].Value)

        Seq.append fromStateLinks fromDirectStoreLinks
        |> Seq.distinct
        |> Seq.toList

    // ──── IO ────────────────────────────────────────────────────────────

    [<Literal>]
    let private siteHost = "https://site.krispykreme.com"

    let private buildRequest (path: string) =
        let req = new HttpRequestMessage(HttpMethod.Get, siteHost + path)
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Kremeing discovery)") |> ignore
        req

    let private fetchPage (send: LiveApi.SendHttp) (path: string)
        : Async<Result<string, StoreError>> =
        async {
            try
                use req = buildRequest path
                let! response = send req
                use _ = response
                if not response.IsSuccessStatusCode then
                    return Error (UpstreamUnavailable
                        (sprintf "site.krispykreme.com %s returned HTTP %d"
                            path (int response.StatusCode)))
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Ok body
            with
            | ex -> return Error (UpstreamUnavailable ex.Message)
        }

    /// Walks root → state pages and returns every unique (state, city) pair
    /// plus any direct-store links spotted along the way. Multi-store city
    /// pages are NOT fetched: we resolve those via the live API which is
    /// cheaper than another HTML round-trip.
    let enumerateCities (send: LiveApi.SendHttp)
        : Async<Result<ScrapedPage, StoreError>> =
        async {
            match! fetchPage send "/" with
            | Error e -> return Error e
            | Ok rootHtml ->
                let rootPage = parsePage rootHtml
                let states = parseRootStates rootHtml

                let! perState =
                    states
                    |> List.map (fun s ->
                        async {
                            match! fetchPage send (sprintf "/%s" s) with
                            | Ok html ->
                                let p = parsePage html
                                return p
                            | Error _ ->
                                // Drop the state on failure rather than aborting
                                // the whole sweep — one bad state shouldn't
                                // prevent discovering the other 42.
                                return { Cities = []; Stores = [] }
                        })
                    |> Async.Parallel

                let combined =
                    perState
                    |> Array.fold
                        (fun acc p ->
                            { Cities = List.append acc.Cities p.Cities
                              Stores = List.append acc.Stores p.Stores })
                        rootPage

                return Ok {
                    Cities = combined.Cities |> List.distinct
                    Stores = combined.Stores |> List.distinct
                }
        }

    // ──── Registry resolution ───────────────────────────────────────────

    /// One entry in the discovered registry. Carries everything the rest of
    /// the system needs to poll, render, and serve the store.
    type RegistryEntry = {
        ShopId: int
        Name: string
        Address: string
        Location: Coordinates
        ShopUrl: string
        /// "{City}, {State}" — passed back to LiveApi.searchByCityState
        /// when the poller refreshes this store's hot light status.
        SearchKey: string
    }

    let private dtoToRegistry (dto: LiveApi.KrispyShopDto) : RegistryEntry = {
        ShopId = dto.shopId
        Name = sprintf "Krispy Kreme %s" dto.shopName
        Address = sprintf "%s, %s, %s %s" dto.address1 dto.city dto.state dto.zipCode
        Location = { Latitude = dto.latitude; Longitude = dto.longitude }
        ShopUrl = dto.shopUrl
        SearchKey = sprintf "%s, %s" dto.city dto.state
    }

    /// "{city}, {state}" string in the format KK's /shops/search expects.
    /// Title-casing isn't required (their search is permissive), so we keep
    /// the slug shape clients see.
    let private cityKey (c: ScrapedCity) =
        sprintf "%s, %s" c.City (c.State.ToUpperInvariant())

    /// Sweeps the live API for every discovered city, dedupes shops by
    /// shopId, and returns the merged registry. Each search returns up to
    /// 12 nearest shops — overlap across cities is expected and handled by
    /// the dedupe.
    let resolveRegistry
            (search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>)
            (page: ScrapedPage)
            : Async<RegistryEntry list> =
        async {
            // Anchor every (state, city) we want to probe — both city links
            // and state-level direct-store links (the latter give us the city
            // slug too).
            let cities =
                seq {
                    yield! page.Cities
                    yield! page.Stores |> List.map (fun s -> { State = s.State; City = s.City })
                }
                |> Seq.distinct
                |> Seq.toList

            let! perCity =
                cities
                |> List.map (fun c ->
                    async {
                        match! search (cityKey c) with
                        | Ok shops -> return shops
                        | Error _ -> return []   // skip silently; partial result is better than none
                    })
                |> Async.Parallel

            return
                perCity
                |> Array.collect List.toArray
                |> Array.distinctBy (fun s -> s.shopId)
                |> Array.map dtoToRegistry
                |> Array.toList
        }
