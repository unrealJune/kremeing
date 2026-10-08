package com.kremeing.auto.prefs

import android.content.Context

/**
 * Remembers the last basemap tile URL template the API handed out, so the map
 * has working tiles on the next launch even before (or without) the
 * `/map-config` round-trip. Backed by [android.content.SharedPreferences].
 */
class BasemapPrefs(context: Context) {

    private val prefs =
        context.getSharedPreferences("kremeing_basemap", Context.MODE_PRIVATE)

    var tileUrlTemplate: String?
        get() = prefs.getString(KEY_TEMPLATE, null)
        set(value) = prefs.edit().putString(KEY_TEMPLATE, value).apply()

    /** Whether the retired CARTO tile cache has been purged on this install. */
    var legacyCachePurged: Boolean
        get() = prefs.getBoolean(KEY_LEGACY_PURGED, false)
        set(value) = prefs.edit().putBoolean(KEY_LEGACY_PURGED, value).apply()

    private companion object {
        const val KEY_TEMPLATE = "tile_url_template"
        const val KEY_LEGACY_PURGED = "legacy_carto_cache_purged"
    }
}
