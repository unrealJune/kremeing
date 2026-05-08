module Kremeing.Core.Tests.CacheTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Core

let private at h m = DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

// Equality on Option/Result via FsUnit's NHamcrest matcher misbehaves
// (same dance as ValidationTests). xUnit's structural Assert.Equal
// works correctly on F# DUs.

[<Fact>]
let ``TryGet returns None for unknown keys`` () =
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 30.0, 100)
    Assert.Equal((None : string option), c.TryGet(42, at 10 0))

[<Fact>]
let ``Set then TryGet returns the cached value`` () =
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 30.0, 100)
    c.Set(42, "doughnut", at 10 0)
    Assert.Equal(Some "doughnut", c.TryGet(42, (at 10 0).AddSeconds 5.0))

[<Fact>]
let ``TryGet returns None after the TTL expires`` () =
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 30.0, 100)
    c.Set(42, "doughnut", at 10 0)
    Assert.Equal((None : string option), c.TryGet(42, (at 10 0).AddSeconds 31.0))

[<Fact>]
let ``GetOrAdd hits the factory on miss and caches the Ok result`` () =
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 30.0, 100)
    let mutable calls = 0
    let factory id =
        async {
            calls <- calls + 1
            return Ok (sprintf "val-%d" id)
        }
    let r1 = c.GetOrAdd 42 (at 10 0) factory |> Async.RunSynchronously
    let r2 = c.GetOrAdd 42 ((at 10 0).AddSeconds 5.0) factory |> Async.RunSynchronously
    Assert.Equal((Ok "val-42" : Result<string, string>), r1)
    Assert.Equal((Ok "val-42" : Result<string, string>), r2)
    calls |> should equal 1   // second call served from cache

[<Fact>]
let ``GetOrAdd does NOT cache Error results`` () =
    // Regression guard: the whole reason caching is safe to add to a
    // proxy endpoint is that transient upstream failures don't
    // poison the cache for the remainder of the TTL.
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 30.0, 100)
    let mutable calls = 0
    let factory _ =
        async {
            calls <- calls + 1
            return Error "boom"
        }
    let r1 = c.GetOrAdd 42 (at 10 0) factory |> Async.RunSynchronously
    let r2 = c.GetOrAdd 42 (at 10 0) factory |> Async.RunSynchronously
    Assert.Equal((Error "boom" : Result<string, string>), r1)
    Assert.Equal((Error "boom" : Result<string, string>), r2)
    calls |> should equal 2

[<Fact>]
let ``Cache eventually evicts expired entries when capacity is exceeded`` () =
    let c = Cache.Cache<int, string>(TimeSpan.FromSeconds 1.0, 5)
    // Fill past capacity with entries that will expire by t1
    for i in 1 .. 5 do
        c.Set(i, sprintf "v%d" i, at 10 0)
    // 2 seconds later, all five are expired; adding a sixth triggers cleanup.
    c.Set(99, "v99", (at 10 0).AddSeconds 2.0)
    c.Count |> should be (lessThanOrEqualTo 1)
