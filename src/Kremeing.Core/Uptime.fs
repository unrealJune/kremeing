namespace Kremeing.Core

open System
open Kremeing.Contracts.Domain

/// Pure transformations that turn a flip log into bucketed time-series
/// data — what an uptime/heatmap chart needs. No IO; no exceptions.
///
/// Convention: the status observed at flip[i] is assumed to have held
/// continuously over [flip[i].ObservedAt, flip[i+1].ObservedAt). The
/// last flip's status holds over [lastFlip, lastPolledAt]. Time after
/// `lastPolledAt` is treated as *unobserved* — the chart should grey
/// it out, not assume continuity.
module Uptime =

    /// One bucket of the time-series chart. `ObservedSeconds < TotalSeconds`
    /// indicates a poll gap (downtime, etc.) that the client should render
    /// as unknown rather than zeroed-out.
    type Bucket = {
        StartUtc: DateTimeOffset
        EndUtc: DateTimeOffset
        OnSeconds: float
        OffSeconds: float
        ObservedSeconds: float
        TotalSeconds: float
        FractionOn: float
    }

    [<NoEquality; NoComparison>]
    type private Interval = {
        Start: DateTimeOffset
        End: DateTimeOffset
        Status: HotLightStatus
    }

    let private buildIntervals
        (flips: HotLightObservation list)
        (lastPolledAt: DateTimeOffset)
        : Interval list =
        match flips with
        | [] -> []
        | _ ->
            let inBetween =
                flips
                |> List.pairwise
                |> List.map (fun (a, b) ->
                    { Start = a.ObservedAt
                      End = b.ObservedAt
                      Status = a.Status })
            let last = List.last flips
            let trailing =
                if lastPolledAt > last.ObservedAt then
                    [ { Start = last.ObservedAt
                        End = lastPolledAt
                        Status = last.Status } ]
                else
                    []   // last flip happened at or after lastPolledAt — no observed tail
            inBetween @ trailing

    let private overlapSeconds
        (winStart: DateTimeOffset, winEnd: DateTimeOffset)
        (intStart: DateTimeOffset, intEnd: DateTimeOffset) =
        let lo = max winStart intStart
        let hi = min winEnd intEnd
        if hi > lo then (hi - lo).TotalSeconds else 0.0

    let private accumulate
        (winStart: DateTimeOffset)
        (winEnd: DateTimeOffset)
        (intervals: Interval list) =
        intervals
        |> List.fold (fun (on, off) i ->
            let sec = overlapSeconds (winStart, winEnd) (i.Start, i.End)
            match i.Status with
            | On -> (on + sec, off)
            | Off -> (on, off + sec)
            | Unknown -> (on, off))
            (0.0, 0.0)

    /// Bucketize the flip log over [since, until). Bucket size must be a
    /// positive duration; an inverted or empty range yields [].
    let bucketize
        (flips: HotLightObservation list)
        (lastPolledAt: DateTimeOffset)
        (since: DateTimeOffset)
        (until: DateTimeOffset)
        (bucketSize: TimeSpan)
        : Bucket list =
        if until <= since || bucketSize.TotalSeconds <= 0.0 then
            []
        else
            let intervals = buildIntervals flips lastPolledAt

            let rec loop (cursor: DateTimeOffset) (acc: Bucket list) =
                if cursor >= until then
                    List.rev acc
                else
                    let candidateEnd = cursor + bucketSize
                    let bucketEnd = if candidateEnd > until then until else candidateEnd
                    let onSec, offSec = accumulate cursor bucketEnd intervals
                    let observed = onSec + offSec
                    let total = (bucketEnd - cursor).TotalSeconds
                    let fraction = if observed > 0.0 then onSec / observed else 0.0
                    let bucket = {
                        StartUtc = cursor
                        EndUtc = bucketEnd
                        OnSeconds = onSec
                        OffSeconds = offSec
                        ObservedSeconds = observed
                        TotalSeconds = total
                        FractionOn = fraction
                    }
                    loop bucketEnd (bucket :: acc)
            loop since []
