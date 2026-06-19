package com.kremeing.auto.logic

import kotlin.math.roundToInt

/**
 * Formats the text shown on each Android Auto place card. Centralizing this
 * keeps the car-screen code declarative (it just binds these strings into a
 * Row/Place template) and makes the exact driver-facing copy unit-testable.
 *
 * Distance is rendered the way a glanceable driving UI wants it: whole feet
 * under a quarter mile, otherwise one decimal of a mile. All formatting is
 * locale-invariant so tests are stable regardless of the host locale.
 */
object CardFormatter {

    private const val FEET_PER_MILE = 5280.0
    private const val FEET_THRESHOLD_MILES = 0.25

    /** Card title: the store name. */
    fun title(store: NearbyStore): String = store.name

    /**
     * Card subtitle: human distance + street address, e.g.
     * "0.4 mi · 123 Main St". The address is trimmed; if absent only the
     * distance is shown.
     */
    fun subtitle(store: NearbyStore): String {
        val dist = formatDistance(store.distanceMiles)
        val addr = store.address.trim()
        return if (addr.isEmpty()) dist else "$dist · $addr"
    }

    /** A short status line for detail views, e.g. "Hot light is ON". */
    fun statusLine(store: NearbyStore): String =
        when (store.status) {
            HotLightStatus.ON -> "Hot light is ON"
            HotLightStatus.OFF -> "Hot light is off"
            HotLightStatus.UNKNOWN -> "Hot light status unknown"
        }

    /**
     * Glanceable distance string. Negative inputs are clamped to zero so a
     * bad value can never render as "-1.0 mi".
     */
    fun formatDistance(distanceMiles: Double): String {
        val miles = if (distanceMiles < 0) 0.0 else distanceMiles
        return if (miles < FEET_THRESHOLD_MILES) {
            val feet = (miles * FEET_PER_MILE).roundToInt()
            "$feet ft"
        } else {
            val rounded = (miles * 10).roundToInt() / 10.0
            "${trimTrailingZero(rounded)} mi"
        }
    }

    private fun trimTrailingZero(value: Double): String {
        val whole = value.toLong()
        return if (value == whole.toDouble()) {
            whole.toString()
        } else {
            // One decimal place, locale-invariant (avoids "0,4" in some locales).
            val tenths = (value * 10).roundToInt()
            "${tenths / 10}.${tenths % 10}"
        }
    }
}
