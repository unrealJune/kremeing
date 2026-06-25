package com.kremeing.auto.prefs

import android.content.Context

/**
 * Persists the device's push-subscription settings: the last FCM token, the
 * location the user subscribed around, and their chosen notify radius. Backed
 * by [android.content.SharedPreferences].
 */
class SubscriptionPrefs(context: Context) {

    private val prefs =
        context.getSharedPreferences("kremeing_subscription", Context.MODE_PRIVATE)

    var token: String?
        get() = prefs.getString(KEY_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_TOKEN, value).apply()

    /** Notify radius in miles; defaults to [DEFAULT_RADIUS_MILES]. */
    var radiusMiles: Double
        get() = java.lang.Double.longBitsToDouble(
            prefs.getLong(KEY_RADIUS, java.lang.Double.doubleToRawLongBits(DEFAULT_RADIUS_MILES)),
        )
        set(value) = prefs.edit()
            .putLong(KEY_RADIUS, java.lang.Double.doubleToRawLongBits(value))
            .apply()

    /** Last subscribed (latitude, longitude), or null if never set. */
    var lastLocation: Pair<Double, Double>?
        get() {
            if (!prefs.contains(KEY_LAT) || !prefs.contains(KEY_LNG)) return null
            val lat = java.lang.Double.longBitsToDouble(prefs.getLong(KEY_LAT, 0))
            val lng = java.lang.Double.longBitsToDouble(prefs.getLong(KEY_LNG, 0))
            return lat to lng
        }
        set(value) {
            val editor = prefs.edit()
            if (value == null) {
                editor.remove(KEY_LAT).remove(KEY_LNG)
            } else {
                editor.putLong(KEY_LAT, java.lang.Double.doubleToRawLongBits(value.first))
                editor.putLong(KEY_LNG, java.lang.Double.doubleToRawLongBits(value.second))
            }
            editor.apply()
        }

    companion object {
        const val DEFAULT_RADIUS_MILES = 10.0
        private const val KEY_TOKEN = "fcm_token"
        private const val KEY_RADIUS = "radius_miles"
        private const val KEY_LAT = "last_lat"
        private const val KEY_LNG = "last_lng"
    }
}
