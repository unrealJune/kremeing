namespace Kremeing.Core

open System
open Kremeing.Contracts.Domain

/// Great-circle distance helpers. The `/stores/nearby` proxy gets a
/// `distance` straight from upstream, but the device-push fan-out needs
/// to compute "is this flipped store within the subscriber's radius?"
/// locally, against coordinates we already hold — hence a self-contained
/// haversine with no external dependency.
module Geo =

    /// Mean Earth radius in miles. Good to a few tenths of a percent for
    /// the "within N miles" proximity checks we use it for.
    [<Literal>]
    let EarthRadiusMiles = 3958.7613

    let private toRadians (degrees: float) = degrees * Math.PI / 180.0

    /// Haversine great-circle distance between two coordinates, in miles.
    /// Symmetric and always non-negative; returns 0 for identical points.
    let distanceMiles (a: Coordinates) (b: Coordinates) : float =
        let dLat = toRadians (b.Latitude - a.Latitude)
        let dLng = toRadians (b.Longitude - a.Longitude)
        let lat1 = toRadians a.Latitude
        let lat2 = toRadians b.Latitude
        let h =
            Math.Sin(dLat / 2.0) ** 2.0
            + Math.Cos lat1 * Math.Cos lat2 * Math.Sin(dLng / 2.0) ** 2.0
        // Clamp to guard against tiny FP overshoot past 1.0 feeding asin.
        2.0 * EarthRadiusMiles * Math.Asin(min 1.0 (sqrt h))

    /// True when `point` is within (inclusive) `radiusMiles` of `center`.
    let withinRadius (center: Coordinates) (radiusMiles: float) (point: Coordinates) : bool =
        distanceMiles center point <= radiusMiles
