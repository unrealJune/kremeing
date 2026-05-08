module Kremeing.Api.Tests.PollerNotifyTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Api

// These tests pin which Record outcomes trigger the notify callback
// inside Poller.runOnce. The flip-only invariant lives in Observations,
// so the conditions are: callback fires iff the recorded outcome is
// Flipped AND the new status is On — never on FirstObservation, never
// on flip-to-Off, never on Unchanged.

let private fixedNow = DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)
let private now () = fixedNow

let private entry shopId : Discovery.RegistryEntry = {
    ShopId = shopId
    Name = sprintf "Krispy Kreme %d" shopId
    Address = ""
    Location = { Latitude = 47.6; Longitude = -122.3 }
    ShopUrl = ""
    SearchKey = "Seattle, WA"
}

/// Builds a search stub that returns the requested status for the
/// SODO shop (id 899) and nothing else.
let private searchReturning (status: bool) : string -> Async<Result<LiveApi.KrispyShopDto list, StoreError>> =
    fun _ ->
        async {
            let shop : LiveApi.KrispyShopDto = {
                shopId = 899
                shopName = "Seattle - 1st Ave South"
                shopUrl = ""
                address1 = "1900 1st Ave S"
                city = "Seattle"
                state = "WA"
                zipCode = "98134"
                latitude = 47.585
                longitude = -122.334
                hotLightOn = status
                hoursDescriptionHotlight = [||]
                distance = 0.0
            }
            return Ok [ shop ]
        }

let private recordingNotifier () =
    let calls = ResizeArray<int>()
    let notify : PushNotify.OnHotLightFlipOn =
        fun (StoreId sid, _entry) ->
            async { calls.Add sid }
    notify, calls

[<Fact>]
let ``FirstObservation never triggers notify, even when status is On`` () =
    // Otherwise every cold poller boot would notify every subscriber for
    // every currently-on store — a notification storm.
    let registry = [ entry 899 ]
    let search = searchReturning true
    let observations = InMemoryObservations.create()
    let notify, calls = recordingNotifier ()

    let stats =
        Poller.runOnce registry search observations.Record notify now
        |> Async.RunSynchronously

    calls.Count |> should equal 0
    stats.Notified |> should equal 0

[<Fact>]
let ``Off → On flip triggers notify exactly once`` () =
    let registry = [ entry 899 ]
    let observations = InMemoryObservations.create()
    let notify, calls = recordingNotifier ()

    // Tick 1: status Off → first observation, no notify
    let _ =
        Poller.runOnce registry (searchReturning false) observations.Record notify now
        |> Async.RunSynchronously
    calls.Count |> should equal 0

    // Tick 2: status On → flip, fires
    let stats =
        Poller.runOnce registry (searchReturning true) observations.Record notify now
        |> Async.RunSynchronously
    calls.Count |> should equal 1
    calls.[0] |> should equal 899
    stats.Notified |> should equal 1

[<Fact>]
let ``On → Off flip does NOT trigger notify`` () =
    // Going dark isn't a flip we notify on — no one signs up to hear the
    // hot light went OFF.
    let registry = [ entry 899 ]
    let observations = InMemoryObservations.create()
    let notify, calls = recordingNotifier ()

    let _ =
        Poller.runOnce registry (searchReturning true) observations.Record notify now
        |> Async.RunSynchronously
    calls.Count |> should equal 0     // first obs

    let stats =
        Poller.runOnce registry (searchReturning false) observations.Record notify now
        |> Async.RunSynchronously
    calls.Count |> should equal 0     // flip-to-off, still silent
    stats.Notified |> should equal 0

[<Fact>]
let ``Unchanged status doesn't trigger notify`` () =
    let registry = [ entry 899 ]
    let observations = InMemoryObservations.create()
    let notify, calls = recordingNotifier ()

    // Three On polls in a row: one FirstObservation, two Unchanged.
    for _ in 1 .. 3 do
        Poller.runOnce registry (searchReturning true) observations.Record notify now
        |> Async.RunSynchronously
        |> ignore

    calls.Count |> should equal 0
