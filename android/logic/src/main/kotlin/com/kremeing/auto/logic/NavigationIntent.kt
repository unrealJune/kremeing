package com.kremeing.auto.logic

import java.net.URLEncoder
import java.util.Locale

/**
 * Builds the `geo:` URIs Android Auto uses to hand a destination off to the
 * active navigation provider (Google Maps). The car app never draws its own
 * map — per Android Auto policy a non-navigation app triggers turn-by-turn by
 * firing one of these URIs — so getting the URI exactly right is the whole
 * "map to it" feature, and it's pinned by tests here.
 */
object NavigationIntent {

    /**
     * A `geo:` URI that drops a pin at the coordinates and labels it with the
     * store name, e.g. `geo:47.6,-122.3?q=47.6,-122.3(Krispy+Kreme)`.
     *
     * Coordinates are formatted with [Locale.US] so the decimal separator is
     * always a dot, and the label is URL-encoded so commas/spaces/parens in a
     * store name can't corrupt the query.
     */
    fun geoUri(latitude: Double, longitude: Double, label: String): String {
        val lat = formatCoord(latitude)
        val lng = formatCoord(longitude)
        val encodedLabel = encode(label)
        return "geo:$lat,$lng?q=$lat,$lng($encodedLabel)"
    }

    /** Convenience overload taking a [NearbyStore]. */
    fun geoUri(store: NearbyStore): String =
        geoUri(store.latitude, store.longitude, store.name)

    /**
     * A label-free search URI used when only a free-text address is known:
     * `geo:0,0?q=<encoded address>`. Android resolves the address itself.
     */
    fun searchUri(query: String): String = "geo:0,0?q=${encode(query)}"

    private fun formatCoord(value: Double): String =
        String.format(Locale.US, "%.6f", value)

    private fun encode(value: String): String =
        URLEncoder.encode(value, "UTF-8").replace("+", "%20")
}
