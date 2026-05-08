module Kremeing.Core.Tests.RateLimitTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Core

let private at h m = DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

[<Fact>]
let ``initial bucket is full — capacity acquires succeed in a row`` () =
    let l = RateLimit.Limiter(capacity = 5.0, refillPerSecond = 1.0)
    for _ in 1 .. 5 do
        l.TryAcquire("ip", at 10 0) |> should equal true

[<Fact>]
let ``the (capacity+1)-th acquire from the same key in the same instant fails`` () =
    let l = RateLimit.Limiter(5.0, 1.0)
    for _ in 1 .. 5 do
        l.TryAcquire("ip", at 10 0) |> ignore
    l.TryAcquire("ip", at 10 0) |> should equal false

[<Fact>]
let ``different keys have independent buckets`` () =
    let l = RateLimit.Limiter(2.0, 1.0)
    l.TryAcquire("ip-a", at 10 0) |> should equal true
    l.TryAcquire("ip-a", at 10 0) |> should equal true
    l.TryAcquire("ip-a", at 10 0) |> should equal false   // a exhausted
    l.TryAcquire("ip-b", at 10 0) |> should equal true    // b unaffected

[<Fact>]
let ``tokens refill at refillPerSecond and cap at capacity`` () =
    // 5 capacity, 1 token / sec. Drain bucket, then advance 3 sec.
    let l = RateLimit.Limiter(5.0, 1.0)
    for _ in 1 .. 5 do
        l.TryAcquire("ip", at 10 0) |> ignore

    // 3 seconds later, ~3 tokens should be available
    let later = (at 10 0).AddSeconds 3.0
    let mutable acquired = 0
    while l.TryAcquire("ip", later) do
        acquired <- acquired + 1
    acquired |> should equal 3

[<Fact>]
let ``refill never overshoots capacity even after a long idle`` () =
    let l = RateLimit.Limiter(5.0, 1.0)
    for _ in 1 .. 5 do l.TryAcquire("ip", at 10 0) |> ignore
    // Hour later — bucket should be at capacity, not 3600
    l.Tokens("ip", (at 10 0).AddHours 1.0)
    |> should (equalWithin 0.0001) 5.0

[<Fact>]
let ``negative or zero capacity is rejected at construction time`` () =
    (fun () -> RateLimit.Limiter(0.0, 1.0) |> ignore)
    |> should throw typeof<ArgumentException>
    (fun () -> RateLimit.Limiter(5.0, 0.0) |> ignore)
    |> should throw typeof<ArgumentException>

[<Fact>]
let ``parallel acquires from one key don't allow more than capacity worth in one tick`` () =
    // Property: lock keeps elapsed-time math race-free. Spam the
    // limiter from many threads at the same instant; the count of
    // successes must equal capacity.
    let l = RateLimit.Limiter(50.0, 1.0)
    let now = at 10 0
    let attempts = 200
    let successes =
        [ for _ in 1 .. attempts ->
            async { return l.TryAcquire("ip", now) } ]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.filter id
        |> Array.length
    successes |> should equal 50
