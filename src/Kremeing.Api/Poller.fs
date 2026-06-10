namespace Kremeing.Api

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Periodic background poller. One tick:
///   1. Group the registry by SearchKey (one upstream call per unique
///      city, not per store — cuts per-tick traffic ~10x).
///   2. Fan out searches in parallel, dedupe responses by shopId.
///   3. For each registry entry whose shopId came back, append an
///      observation through the Record port.
///   4. When the recorded outcome is a flip from anything to On,
///      invoke the configured `notifyFlipOn` callback (push fan-out
///      lives there; the poller itself is push-agnostic).
///
/// `runOnce` is the testable nucleus; PollerService is just the
/// BackgroundService wrapper that loops on it.
module Poller =

    type Config = {
        Interval: TimeSpan
    }

    let defaultConfig = { Interval = TimeSpan.FromMinutes 5.0 }

    /// Per-tick summary returned to logs and metrics.
    type TickStats = {
        Polled: int      // registry entries we attempted
        Recorded: int    // observations actually appended
        Missing: int     // registry entries whose shopId never came back
        Failed: int      // SearchKey requests that errored
        Notified: int    // off→on flips that triggered notifyFlipOn
    }

    let runOnce
            (registry: Discovery.RegistryEntry list)
            (search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>)
            (record: Ports.RecordObservation)
            (notifyFlipOn: PushNotify.OnHotLightFlipOn)
            (now: unit -> DateTimeOffset)
            : Async<TickStats> =
        async {
            let observedAt = now ()
            let uniqueKeys =
                registry
                |> List.map (fun r -> r.SearchKey)
                |> List.distinct

            let! perKey =
                uniqueKeys
                |> List.map (fun key ->
                    async {
                        let! result = search key
                        return result
                    })
                |> Async.Parallel

            let failed =
                perKey
                |> Array.sumBy (function Error _ -> 1 | _ -> 0)

            // Union successful responses; dedupe by shopId. Same shop
            // can appear under "Seattle, WA" and "Bellevue, WA" — we
            // keep whichever copy arrived first; the boolean field is
            // the same either way.
            let shopMap =
                perKey
                |> Array.choose (function Ok shops -> Some shops | _ -> None)
                |> Array.collect List.toArray
                |> Array.fold
                    (fun (acc: Map<int, LiveApi.KrispyShopDto>) shop ->
                        if Map.containsKey shop.shopId acc then acc
                        else Map.add shop.shopId shop acc)
                    Map.empty

            let mutable polled = 0
            let mutable recorded = 0
            let mutable missing = 0
            let mutable notified = 0
            for entry in registry do
                polled <- polled + 1
                match Map.tryFind entry.ShopId shopMap with
                | Some shop ->
                    let obs = LiveApi.shopToObservation observedAt shop
                    let! outcome = record obs
                    recorded <- recorded + 1
                    // Notify only on real flips TO On. FirstObservation
                    // is excluded by design — otherwise every pod restart
                    // for a currently-on store would re-notify everyone.
                    match outcome with
                    | Ok (Flipped _) when obs.Status = On ->
                        do! notifyFlipOn (StoreId entry.ShopId, entry)
                        notified <- notified + 1
                    | _ -> ()
                | None ->
                    missing <- missing + 1

            return {
                Polled = polled
                Recorded = recorded
                Missing = missing
                Failed = failed
                Notified = notified
            }
        }

    /// Hosted service that runs `runOnce` on a timer until shutdown.
    /// Per-tick exceptions are logged but don't kill the service —
    /// transient upstream blips shouldn't take down the whole API.
    ///
    /// `getRegistry` is read fresh on every tick (not captured once) so a
    /// periodic discovery refresh is picked up without restarting the poller.
    type PollerService
        (
            getRegistry: unit -> Discovery.RegistryEntry list,
            search: string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>>,
            record: Ports.RecordObservation,
            notifyFlipOn: PushNotify.OnHotLightFlipOn,
            config: Config,
            logger: ILogger<PollerService>
        ) =
        inherit BackgroundService()

        override _.ExecuteAsync(stopToken: CancellationToken) =
            task {
                logger.LogInformation(
                    "Poller starting: stores={count}, interval={interval}",
                    (getRegistry ()).Length, config.Interval)

                while not stopToken.IsCancellationRequested do
                    try
                        let! stats =
                            runOnce
                                (getRegistry ()) search record notifyFlipOn
                                (fun () -> DateTimeOffset.UtcNow)
                        logger.LogInformation(
                            "tick: polled={polled} recorded={recorded} missing={missing} \
                             failed={failed} notified={notified}",
                            stats.Polled, stats.Recorded, stats.Missing,
                            stats.Failed, stats.Notified)
                    with
                    | :? OperationCanceledException -> ()   // shutdown
                    | ex -> logger.LogError(ex, "Poller tick failed")

                    try
                        do! Task.Delay(config.Interval, stopToken)
                    with
                    | :? OperationCanceledException -> ()
            }
