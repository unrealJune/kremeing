namespace Kremeing.Core

open Kremeing.Contracts.Domain

module Validation =

    let parseStoreId (raw: string) : Result<StoreId, StoreError> =
        if isNull raw then
            Error (InvalidStoreId "")
        else
            let trimmed = raw.Trim()
            match System.Int32.TryParse trimmed with
            | true, n when n > 0 -> Ok (StoreId n)
            | _ -> Error (InvalidStoreId raw)

    /// Upper bound on a device-push subscription radius. Wide enough for
    /// "stores in my metro" yet narrow enough that a single registration
    /// can't turn into a notify-the-whole-country firehose.
    [<Literal>]
    let MaxDeviceRadiusMiles = 100.0

    /// Parse the wire `platform` string into the domain DU. Case- and
    /// whitespace-insensitive. `None` for anything we don't support.
    let parseDevicePlatform (raw: string) : DevicePlatform option =
        if System.String.IsNullOrWhiteSpace raw then None
        else
            match raw.Trim().ToLowerInvariant() with
            | "android" -> Some Android
            | _ -> None

    /// Validate a device-push registration payload from the wire,
    /// returning the domain value or a human-readable message describing
    /// the first problem found. Kept in Core (not the HTTP layer) so the
    /// same rules are unit-testable without spinning up a host.
    let validateDeviceRegistration
            (token: string)
            (platform: string)
            (lat: float)
            (lng: float)
            (radiusMiles: float)
            : Result<DevicePushRegistration, string> =
        if System.String.IsNullOrWhiteSpace token then
            Error "token is required"
        elif System.Double.IsNaN lat || lat < -90.0 || lat > 90.0 then
            Error "latitude must be between -90 and 90"
        elif System.Double.IsNaN lng || lng < -180.0 || lng > 180.0 then
            Error "longitude must be between -180 and 180"
        elif System.Double.IsNaN radiusMiles
             || radiusMiles <= 0.0
             || radiusMiles > MaxDeviceRadiusMiles then
            Error (sprintf "radiusMiles must be greater than 0 and at most %.0f" MaxDeviceRadiusMiles)
        else
            match parseDevicePlatform platform with
            | None -> Error "platform must be 'android'"
            | Some p ->
                Ok {
                    Token = token.Trim()
                    Platform = p
                    Location = { Latitude = lat; Longitude = lng }
                    RadiusMiles = radiusMiles
                }
