namespace Kremeing.Core

open System
open System.Collections.Concurrent

/// Tiny in-process TTL cache. Bounded by an upper item count — when
/// the cap is exceeded, expired entries are evicted in bulk. Designed
/// for "shield upstream" and "soften read pressure" use cases (proxy
/// endpoints), not for arbitrary memoization.
module Cache =

    [<NoEquality; NoComparison>]
    type private Entry<'V> = {
        Value: 'V
        ExpiresAt: DateTimeOffset
    }

    type Cache<'K, 'V when 'K : equality>(ttl: TimeSpan, capacity: int) =
        let items = ConcurrentDictionary<'K, Entry<'V>>()

        let purgeIfFull (now: DateTimeOffset) =
            // Best-effort cleanup; ConcurrentDictionary.Count is O(N)
            // but only walked when we cross the cap.
            if items.Count > capacity then
                for kvp in items do
                    if kvp.Value.ExpiresAt <= now then
                        items.TryRemove(kvp.Key) |> ignore

        member _.TryGet(key: 'K, now: DateTimeOffset) : 'V option =
            match items.TryGetValue key with
            | true, e when e.ExpiresAt > now -> Some e.Value
            | _ -> None

        member _.Set(key: 'K, value: 'V, now: DateTimeOffset) =
            items.[key] <- { Value = value; ExpiresAt = now + ttl }
            purgeIfFull now

        /// Read-through with a result-typed factory. Errors are NEVER
        /// cached — a 502 from upstream shouldn't be served for the
        /// next 30 seconds. Exceptions thrown by the factory bubble up.
        member this.GetOrAdd
                (key: 'K)
                (now: DateTimeOffset)
                (factory: 'K -> Async<Result<'V, 'E>>)
                : Async<Result<'V, 'E>> =
            async {
                match this.TryGet(key, now) with
                | Some v -> return Ok v
                | None ->
                    let! r = factory key
                    match r with
                    | Ok v ->
                        this.Set(key, v, now)
                        return Ok v
                    | Error _ as e -> return e
            }

        /// Snapshot count, primarily for tests.
        member _.Count = items.Count
