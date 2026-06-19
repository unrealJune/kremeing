package com.kremeing.auto.logic

import kotlin.math.asin
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Great-circle distance helpers. Kept in parity with the backend's
 * `Geo` module (same Earth radius in miles, same haversine formula) so the
 * app can rank/filter by distance identically to the server when it only has
 * raw coordinates (e.g. from an FCM payload) rather than a precomputed
 * `distanceMiles`.
 */
object Geo {
    /** Earth's mean radius in miles — must match the backend constant. */
    const val EARTH_RADIUS_MILES: Double = 3958.7613

    /** Haversine distance in miles between two lat/lng points. */
    fun distanceMiles(
        lat1: Double,
        lng1: Double,
        lat2: Double,
        lng2: Double,
    ): Double {
        val dLat = Math.toRadians(lat2 - lat1)
        val dLng = Math.toRadians(lng2 - lng1)
        val rLat1 = Math.toRadians(lat1)
        val rLat2 = Math.toRadians(lat2)
        val a = sin(dLat / 2) * sin(dLat / 2) +
            cos(rLat1) * cos(rLat2) * sin(dLng / 2) * sin(dLng / 2)
        return 2 * EARTH_RADIUS_MILES * asin(sqrt(a))
    }

    /** Inclusive radius test, mirroring the backend `Geo.withinRadius`. */
    fun withinRadius(
        lat1: Double,
        lng1: Double,
        lat2: Double,
        lng2: Double,
        radiusMiles: Double,
    ): Boolean = distanceMiles(lat1, lng1, lat2, lng2) <= radiusMiles
}
