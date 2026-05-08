module Kremeing.Core.Tests.UptimeTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core

let private at (h: int) (m: int) =
    DateTimeOffset(2026, 5, 8, h, m, 0, TimeSpan.Zero)

let private flip storeId status t =
    { StoreId = StoreId storeId; Status = status; ObservedAt = t }

let private hour = TimeSpan.FromHours 1.0

module BasicShapes =

    [<Fact>]
    let ``no flips → every bucket is unobserved`` () =
        let buckets =
            Uptime.bucketize [] (at 12 0) (at 10 0) (at 13 0) hour
        buckets |> List.length |> should equal 3
        buckets |> List.forall (fun b -> b.ObservedSeconds = 0.0) |> should equal true
        buckets |> List.forall (fun b -> b.FractionOn = 0.0) |> should equal true

    [<Fact>]
    let ``inverted range → empty bucket list`` () =
        Uptime.bucketize [] (at 10 0) (at 12 0) (at 11 0) hour
        |> should equal ([] : Uptime.Bucket list)

    [<Fact>]
    let ``zero or negative bucket size → empty bucket list`` () =
        Uptime.bucketize [] (at 12 0) (at 10 0) (at 13 0) TimeSpan.Zero
        |> should equal ([] : Uptime.Bucket list)

    [<Fact>]
    let ``trailing partial bucket truncates at until rather than overflowing`` () =
        // Range = 90 minutes, bucket = 1 hour → first full hour, then 30 min.
        let buckets =
            Uptime.bucketize [] (at 12 0) (at 10 0) (at 11 30) hour
        buckets |> List.length |> should equal 2
        let last = List.last buckets
        last.TotalSeconds |> should equal 1800.0   // 30 min

module SteadyState =

    [<Fact>]
    let ``single On flip + later poll → bucket reads 100% on for the observed window`` () =
        // Flip at 10:00, last poll at 11:00. Bucket [10:00, 11:00) = fully On.
        let flips = [ flip 899 On (at 10 0) ]
        let buckets =
            Uptime.bucketize flips (at 11 0) (at 10 0) (at 11 0) hour
        buckets |> List.length |> should equal 1
        let b = List.head buckets
        b.OnSeconds |> should equal 3600.0
        b.OffSeconds |> should equal 0.0
        b.FractionOn |> should equal 1.0
        b.ObservedSeconds |> should equal 3600.0

    [<Fact>]
    let ``On→Off mid-bucket → fractionOn matches the time-on share`` () =
        // 10:00 On, 10:15 Off. Last poll 11:00. Bucket [10:00, 11:00):
        //   on for 15 min, off for 45 min → fractionOn = 0.25.
        let flips = [
            flip 899 On (at 10 0)
            flip 899 Off (at 10 15)
        ]
        let buckets =
            Uptime.bucketize flips (at 11 0) (at 10 0) (at 11 0) hour
        let b = List.head buckets
        b.OnSeconds |> should equal 900.0       // 15 min
        b.OffSeconds |> should equal 2700.0     // 45 min
        b.FractionOn |> should (equalWithin 0.0001) 0.25

module GapHandling =

    [<Fact>]
    let ``time before the first flip is unobserved, not assumed off`` () =
        // We never saw the store before 10:30, so [10:00, 10:30) is unknown.
        let flips = [ flip 899 On (at 10 30) ]
        let buckets =
            Uptime.bucketize flips (at 11 0) (at 10 0) (at 11 0) hour
        let b = List.head buckets
        b.ObservedSeconds |> should equal 1800.0   // only the [10:30, 11:00) half
        b.FractionOn |> should equal 1.0           // still 100% of *observed* time

    [<Fact>]
    let ``time after lastPolledAt is unobserved`` () =
        // Last poll at 10:30; bucket spans 10:00–11:00.
        // Observed window in that bucket: [10:00, 10:30) = 30 min.
        let flips = [ flip 899 On (at 9 0) ]
        let buckets =
            Uptime.bucketize flips (at 10 30) (at 10 0) (at 11 0) hour
        let b = List.head buckets
        b.ObservedSeconds |> should equal 1800.0
        b.OnSeconds |> should equal 1800.0
        b.TotalSeconds |> should equal 3600.0

    [<Fact>]
    let ``a bucket entirely after lastPolledAt has zero observed seconds`` () =
        let flips = [ flip 899 On (at 9 0) ]
        let buckets =
            Uptime.bucketize flips (at 10 0) (at 11 0) (at 13 0) hour
        // Buckets: [11,12) and [12,13). lastPolledAt = 10 → both unobserved.
        buckets |> List.iter (fun b -> b.ObservedSeconds |> should equal 0.0)

module BoundaryConditions =

    [<Fact>]
    let ``flip exactly on bucket boundary attributes to the new bucket`` () =
        // 10:00 On, 11:00 Off, last poll 12:00.
        // Bucket [10:00, 11:00): fully On
        // Bucket [11:00, 12:00): fully Off
        let flips = [
            flip 899 On (at 10 0)
            flip 899 Off (at 11 0)
        ]
        let buckets =
            Uptime.bucketize flips (at 12 0) (at 10 0) (at 12 0) hour
        let first = buckets |> List.head
        let second = buckets |> List.last
        first.FractionOn |> should equal 1.0
        second.FractionOn |> should equal 0.0
        second.OffSeconds |> should equal 3600.0

    [<Fact>]
    let ``every bucket's TotalSeconds matches its EndUtc - StartUtc`` () =
        // Property: total seconds is purely a function of bucket size and
        // truncation at `until`. Easy invariant to assert across all buckets.
        let flips = [ flip 899 On (at 10 0) ]
        let buckets =
            Uptime.bucketize flips (at 12 0) (at 10 0) (at 12 30) hour
        for b in buckets do
            b.TotalSeconds |> should equal (b.EndUtc - b.StartUtc).TotalSeconds

    [<Fact>]
    let ``OnSeconds + OffSeconds = ObservedSeconds, always`` () =
        let flips = [
            flip 899 On (at 10 0)
            flip 899 Off (at 10 20)
            flip 899 On (at 10 45)
        ]
        let buckets =
            Uptime.bucketize flips (at 12 0) (at 9 0) (at 13 0) hour
        for b in buckets do
            (b.OnSeconds + b.OffSeconds) |> should (equalWithin 0.0001) b.ObservedSeconds
