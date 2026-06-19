module Kremeing.Core.Tests.DeviceRegistrationValidationTests

open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core

// Validation.validateDeviceRegistration is the single source of truth for
// the /device-subscriptions payload rules. The HTTP handler delegates to
// it, so these tests pin the contract without needing a host.

let private valid () =
    Validation.validateDeviceRegistration "fcm-token-abc" "android" 47.6 -122.3 25.0

[<Fact>]
let ``accepts a well-formed android registration`` () =
    match valid () with
    | Ok reg ->
        reg.Token |> should equal "fcm-token-abc"
        reg.Platform |> should equal Android
        reg.Location.Latitude |> should equal 47.6
        reg.Location.Longitude |> should equal -122.3
        reg.RadiusMiles |> should equal 25.0
    | Error e -> failwithf "expected Ok, got Error %s" e

[<Fact>]
let ``platform parsing is case-insensitive and trims whitespace`` () =
    match Validation.validateDeviceRegistration "t" "  AnDrOiD " 0.0 0.0 5.0 with
    | Ok reg -> reg.Platform |> should equal Android
    | Error e -> failwithf "expected Ok, got Error %s" e

[<Fact>]
let ``trims the token`` () =
    match Validation.validateDeviceRegistration "  tok  " "android" 0.0 0.0 5.0 with
    | Ok reg -> reg.Token |> should equal "tok"
    | Error _ -> failwith "expected Ok"

[<Fact>]
let ``rejects an empty token`` () =
    Validation.validateDeviceRegistration "" "android" 47.6 -122.3 25.0
    |> Result.isError |> should equal true

[<Fact>]
let ``rejects a whitespace-only token`` () =
    Validation.validateDeviceRegistration "   " "android" 47.6 -122.3 25.0
    |> Result.isError |> should equal true

[<Fact>]
let ``rejects an unknown platform`` () =
    match Validation.validateDeviceRegistration "t" "ios" 47.6 -122.3 25.0 with
    | Error msg -> msg |> should haveSubstring "platform"
    | Ok _ -> failwith "expected Error for unsupported platform"

[<Theory>]
[<InlineData(91.0)>]
[<InlineData(-91.0)>]
let ``rejects out-of-range latitude`` (lat: float) =
    Validation.validateDeviceRegistration "t" "android" lat -122.3 25.0
    |> Result.isError |> should equal true

[<Theory>]
[<InlineData(181.0)>]
[<InlineData(-181.0)>]
let ``rejects out-of-range longitude`` (lng: float) =
    Validation.validateDeviceRegistration "t" "android" 47.6 lng 25.0
    |> Result.isError |> should equal true

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(-1.0)>]
let ``rejects a non-positive radius`` (radius: float) =
    Validation.validateDeviceRegistration "t" "android" 47.6 -122.3 radius
    |> Result.isError |> should equal true

[<Fact>]
let ``rejects a radius above the maximum`` () =
    Validation.validateDeviceRegistration "t" "android" 47.6 -122.3 (Validation.MaxDeviceRadiusMiles + 1.0)
    |> Result.isError |> should equal true

[<Fact>]
let ``accepts a radius exactly at the maximum`` () =
    Validation.validateDeviceRegistration "t" "android" 47.6 -122.3 Validation.MaxDeviceRadiusMiles
    |> Result.isOk |> should equal true

[<Fact>]
let ``accepts the coordinate extremes`` () =
    Validation.validateDeviceRegistration "t" "android" 90.0 180.0 1.0 |> Result.isOk |> should equal true
    Validation.validateDeviceRegistration "t" "android" -90.0 -180.0 1.0 |> Result.isOk |> should equal true

[<Fact>]
let ``parseDevicePlatform returns None for blank input`` () =
    Validation.parseDevicePlatform "" |> should equal (None: DevicePlatform option)
    Validation.parseDevicePlatform "  " |> should equal (None: DevicePlatform option)
