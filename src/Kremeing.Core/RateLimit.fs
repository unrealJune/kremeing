namespace Kremeing.Core

open System
open System.Collections.Concurrent

/// Per-key token-bucket rate limiter. Each key (typically client IP)
/// gets its own bucket of `capacity` tokens that refills at
/// `refillPerSecond` tokens per second. `TryAcquire` returns true if
/// a token was available, false otherwise — callers translate `false`
/// to whatever they need (HTTP 429, queue, drop, etc.).
///
/// Concurrency: per-bucket lock keeps the elapsed-time math
/// race-free without serializing distinct keys.
module RateLimit =

    [<NoEquality; NoComparison>]
    type private Bucket = {
        mutable Tokens: float
        mutable LastRefillUtc: DateTimeOffset
    }

    type Limiter(capacity: float, refillPerSecond: float) =
        do
            if capacity <= 0.0 then
                invalidArg (nameof capacity) "capacity must be positive"
            if refillPerSecond <= 0.0 then
                invalidArg (nameof refillPerSecond) "refillPerSecond must be positive"

        let buckets = ConcurrentDictionary<string, Bucket>()

        member _.Capacity = capacity
        member _.RefillPerSecond = refillPerSecond

        /// Try to consume one token for `key` at instant `now`. Returns
        /// true if a token was available (call permitted), false if the
        /// caller should be rejected. Idempotent under simultaneous
        /// callers for the same key thanks to per-bucket locking.
        member _.TryAcquire(key: string, now: DateTimeOffset) : bool =
            let bucket =
                buckets.GetOrAdd(
                    key,
                    fun _ -> { Tokens = capacity; LastRefillUtc = now })
            lock bucket (fun () ->
                let elapsed = max 0.0 (now - bucket.LastRefillUtc).TotalSeconds
                let refilled = bucket.Tokens + elapsed * refillPerSecond
                bucket.Tokens <- min capacity refilled
                bucket.LastRefillUtc <- now
                if bucket.Tokens >= 1.0 then
                    bucket.Tokens <- bucket.Tokens - 1.0
                    true
                else
                    false)

        /// Read remaining tokens for a key without consuming any —
        /// only used by tests to assert state.
        member _.Tokens(key: string, now: DateTimeOffset) : float =
            match buckets.TryGetValue key with
            | false, _ -> capacity
            | true, b ->
                lock b (fun () ->
                    let elapsed = max 0.0 (now - b.LastRefillUtc).TotalSeconds
                    min capacity (b.Tokens + elapsed * refillPerSecond))
