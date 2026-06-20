module Kremeing.Api.Tests.DevicePushDispatchTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open Kremeing.Api

// DevicePushDispatch.buildMessageJson is the pure contract the Android
// client parses out of the FCM data message. These pin the JSON shape:
// FCM v1 envelope, data-only, all values stringified.

let private payload : DevicePushDispatch.Payload = {
    title = "🔥 Krispy Kreme SODO"
    body = "Hot doughnuts ready now"
    storeId = 899
    storeName = "Krispy Kreme SODO"
    latitude = 47.6062
    longitude = -122.3321
}

let private parse (json: string) = JsonDocument.Parse(json).RootElement

[<Fact>]
let ``wraps the payload in an FCM v1 message envelope with the token`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "device-token-xyz" payload)
    let message = root.GetProperty("message")
    message.GetProperty("token").GetString() |> should equal "device-token-xyz"

[<Fact>]
let ``sends a data-only message so the app renders the notification`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let message = root.GetProperty("message")
    // No 'notification' block — the Android app builds the car-extended
    // notification itself from the data fields.
    let hasNotification, _ = message.TryGetProperty("notification")
    hasNotification |> should equal false
    let hasData, _ = message.TryGetProperty("data")
    hasData |> should equal true

[<Fact>]
let ``data fields carry title, body, store id, name`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let data = root.GetProperty("message").GetProperty("data")
    data.GetProperty("title").GetString() |> should equal "🔥 Krispy Kreme SODO"
    data.GetProperty("body").GetString() |> should equal "Hot doughnuts ready now"
    data.GetProperty("storeName").GetString() |> should equal "Krispy Kreme SODO"

[<Fact>]
let ``numeric data values are stringified per the FCM data contract`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let data = root.GetProperty("message").GetProperty("data")
    // All must be JSON strings, not numbers.
    data.GetProperty("storeId").ValueKind |> should equal JsonValueKind.String
    data.GetProperty("latitude").ValueKind |> should equal JsonValueKind.String
    data.GetProperty("longitude").ValueKind |> should equal JsonValueKind.String
    data.GetProperty("storeId").GetString() |> should equal "899"

[<Fact>]
let ``coordinates round-trip back to the original doubles`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let data = root.GetProperty("message").GetProperty("data")
    let lat = System.Double.Parse(data.GetProperty("latitude").GetString(),
                                  System.Globalization.CultureInfo.InvariantCulture)
    let lng = System.Double.Parse(data.GetProperty("longitude").GetString(),
                                  System.Globalization.CultureInfo.InvariantCulture)
    lat |> should (equalWithin 1e-9) 47.6062
    lng |> should (equalWithin 1e-9) -122.3321

[<Fact>]
let ``coordinates use invariant culture (decimal point, not comma)`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let data = root.GetProperty("message").GetProperty("data")
    data.GetProperty("latitude").GetString() |> should haveSubstring "."
    data.GetProperty("latitude").GetString() |> should not' (haveSubstring ",")

[<Fact>]
let ``requests high android priority so flips reach the car promptly`` () =
    let root = parse (DevicePushDispatch.buildMessageJson "t" payload)
    let android = root.GetProperty("message").GetProperty("android")
    android.GetProperty("priority").GetString() |> should equal "high"
