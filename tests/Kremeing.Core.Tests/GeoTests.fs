module Kremeing.Core.Tests.GeoTests

open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core

// Geo.distanceMiles is a haversine; the device-push fan-out relies on it
// to decide "is this flipped store within the subscriber's radius?".
// These pin a few known distances and the algebraic properties we lean on.

let private coord lat lng : Coordinates = { Latitude = lat; Longitude = lng }

// Reference points used across tests.
let private seattle  = coord 47.6062 -122.3321
let private portland = coord 45.5152 -122.6784
let private newYork  = coord 40.7128  -74.0060

[<Fact>]
let ``distance between a point and itself is zero`` () =
    Geo.distanceMiles seattle seattle |> should (equalWithin 1e-6) 0.0

[<Fact>]
let ``distance is symmetric`` () =
    let a = Geo.distanceMiles seattle portland
    let b = Geo.distanceMiles portland seattle
    a |> should (equalWithin 1e-9) b

[<Fact>]
let ``Seattle to Portland is about 145 miles`` () =
    // Known great-circle distance ~145 mi; allow a few miles of slack
    // for the spherical-Earth approximation.
    Geo.distanceMiles seattle portland |> should (equalWithin 5.0) 145.0

[<Fact>]
let ``Seattle to New York is about 2400 miles`` () =
    Geo.distanceMiles seattle newYork |> should (equalWithin 40.0) 2400.0

[<Fact>]
let ``one degree of latitude is about 69 miles`` () =
    let a = coord 0.0 0.0
    let b = coord 1.0 0.0
    Geo.distanceMiles a b |> should (equalWithin 0.5) 69.0

[<Fact>]
let ``distance is always non-negative`` () =
    Geo.distanceMiles newYork portland |> should be (greaterThanOrEqualTo 0.0)

[<Fact>]
let ``withinRadius is true for a point inside the radius`` () =
    // Portland is ~145 mi from Seattle; a 200-mi radius includes it.
    Geo.withinRadius seattle 200.0 portland |> should equal true

[<Fact>]
let ``withinRadius is false for a point outside the radius`` () =
    Geo.withinRadius seattle 50.0 portland |> should equal false

[<Fact>]
let ``withinRadius is inclusive at the boundary`` () =
    let d = Geo.distanceMiles seattle portland
    // Exactly the measured distance is considered within range.
    Geo.withinRadius seattle d portland |> should equal true
