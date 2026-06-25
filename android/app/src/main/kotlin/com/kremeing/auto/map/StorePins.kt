package com.kremeing.auto.map

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import com.kremeing.auto.logic.HotLightStatus

/**
 * Draws (and caches) the circular map pin for each hot-light status, in the
 * web app's colours so the native map reads the same as the website:
 *  - on/hot  → primary red `#C73E1D`
 *  - off     → green `#1F6B3F` with a white centre dot
 *  - unknown → grey `#797979` with a white centre dot
 * Each pin has a white ring so it stays legible over map tiles.
 */
object StorePins {

    private val cache = HashMap<HotLightStatus, Bitmap>()

    /** A pin bitmap for [status], sized for the screen [density]. Cached. */
    fun bitmap(status: HotLightStatus, density: Float): Bitmap = cache.getOrPut(status) {
        val size = (26f * density).toInt().coerceAtLeast(24)
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bmp)
        val r = size / 2f

        val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
        canvas.drawCircle(r, r, r, ring)

        val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = colorFor(status) }
        canvas.drawCircle(r, r, r - 2f * density, fill)

        // A white centre dot echoes the web's "off"/"unknown" pins; the hot pin
        // stays a solid disc so lit stores pop.
        if (status != HotLightStatus.ON) {
            val dot = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
            canvas.drawCircle(r, r, 3f * density, dot)
        }
        bmp
    }

    /** The blue "you are here" dot. */
    fun userBitmap(density: Float): Bitmap {
        val size = (18f * density).toInt().coerceAtLeast(16)
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bmp)
        val r = size / 2f
        canvas.drawCircle(r, r, r, Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE })
        canvas.drawCircle(
            r,
            r,
            r - 2f * density,
            Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#3478F6") },
        )
        return bmp
    }

    private fun colorFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#C73E1D")
        HotLightStatus.OFF -> Color.parseColor("#1F6B3F")
        HotLightStatus.UNKNOWN -> Color.parseColor("#797979")
    }
}
