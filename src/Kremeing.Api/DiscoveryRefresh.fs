namespace Kremeing.Api

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Periodic discovery refresh. Re-runs the two-phase scrape + resolve on a
/// timer and swaps the shared registry holder — but only when the new
/// result is trustworthy.
///
/// This directly fixes the failure mode where a single empty discovery
/// (an upstream blip on restart) cached `polled=0` forever: a refresh that
/// resolves 0 stores is *rejected*, the previous good registry is kept, and
/// a warning is logged. The registry can grow or change, but it can never be
/// silently clobbered to empty.
///
/// `decide` is the pure, testable nucleus; DiscoveryRefreshService is just
/// the BackgroundService wrapper that loops on it.
module DiscoveryRefresh =

    type Config = {
        Interval: TimeSpan
    }

    /// Twice a day. Discovery is ~150 upstream calls / ~30s — negligible
    /// next to the 5-minute poll loop, and well within the project's
    /// "polite citizen" cadence (see README "Responsible use").
    let defaultConfig = { Interval = TimeSpan.FromHours 12.0 }

    /// What a refresh attempt resolved to, and what we decided to do.
    type Outcome =
        /// Discovery returned a non-empty registry; adopt it.
        | Replaced of Discovery.RegistryEntry list
        /// Discovery succeeded but returned 0 stores; keep the existing
        /// registry. This is the guard that prevents the silent-empty outage.
        | KeptEmpty
        /// Discovery itself errored; keep the existing registry.
        | KeptError of StoreError

    /// Pure decision: given a fresh discovery result, decide what the
    /// registry should become. The current registry is irrelevant to the
    /// decision itself (we only ever adopt non-empty results), but callers
    /// keep it on `KeptEmpty`/`KeptError`.
    let decide
            (discovered: Result<Discovery.RegistryEntry list, StoreError>)
            : Outcome =
        match discovered with
        | Error e -> KeptError e
        | Ok [] -> KeptEmpty
        | Ok entries -> Replaced entries

    /// One full discovery sweep: enumerate cities via site.krispykreme.com,
    /// then resolve every (city, state) to shopIds via the live API. Returns
    /// Error only when the root enumeration fails; a partial city resolve
    /// degrades to a (possibly empty) list, which `decide` then guards.
    ///
    /// Shared by startup seeding (Program.discoverRegistry) and the periodic
    /// refresh so the two can never drift apart.
    let runDiscovery
            (send: LiveApi.SendHttp)
            (search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>)
            : Async<Result<Discovery.RegistryEntry list, StoreError>> =
        async {
            match! Discovery.enumerateCities send with
            | Error e -> return Error e
            | Ok page ->
                let! registry = Discovery.resolveRegistry search page
                return Ok registry
        }

    /// Hosted service that refreshes the registry holder on a timer until
    /// shutdown. Per-iteration exceptions are logged but don't kill the
    /// service — a transient upstream blip must not take down the process,
    /// and (by design) must not wipe the registry either.
    type DiscoveryRefreshService
        (
            holder: Registry.Holder,
            send: LiveApi.SendHttp,
            search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>,
            config: Config,
            logger: ILogger<DiscoveryRefreshService>
        ) =
        inherit BackgroundService()

        override _.ExecuteAsync(stopToken: CancellationToken) =
            task {
                logger.LogInformation(
                    "Discovery refresh starting: interval={interval}, seeded stores={count}",
                    config.Interval, holder.Count)

                while not stopToken.IsCancellationRequested do
                    // Delay first: startup already seeded the holder, so the
                    // first refresh is one interval out.
                    try
                        do! Task.Delay(config.Interval, stopToken)
                    with :? OperationCanceledException -> ()

                    if not stopToken.IsCancellationRequested then
                        try
                            let! discovered = runDiscovery send search
                            let previous = holder.Count
                            match decide discovered with
                            | Replaced entries ->
                                holder.Set(entries, DateTimeOffset.UtcNow)
                                logger.LogInformation(
                                    "discovery refresh: stores={count} (was {previous})",
                                    entries.Length, previous)
                            | KeptEmpty ->
                                logger.LogWarning(
                                    "discovery refresh resolved 0 stores; keeping existing \
                                     registry of {previous} (guard against silent-empty outage)",
                                    previous)
                            | KeptError e ->
                                logger.LogWarning(
                                    "discovery refresh failed ({error}); keeping existing \
                                     registry of {previous}",
                                    sprintf "%A" e, previous)
                        with
                        | :? OperationCanceledException -> ()   // shutdown
                        | ex -> logger.LogError(ex, "Discovery refresh iteration failed")
            }
