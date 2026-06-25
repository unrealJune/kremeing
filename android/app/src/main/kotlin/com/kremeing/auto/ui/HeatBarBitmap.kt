package com.kremeing.auto.ui

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import com.kremeing.auto.logic.HeatBarModel
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.Interval

/**
 * Renders the heat bar to a static [Bitmap] for Android Auto, where templates
 * can't host a custom interactive view — the car shows this bitmap as a
 * large row image. Same layers/colours as [HeatBarView] (ribbon → segments →
 * now-marker), minus the touch scrubbing.
 */
object HeatBarBitmap {

    fun render(
        intervals: List<Interval>,
        probabilities: FloatArray?,
        dayStartMs: Long,
        dayEndMs: Long,
        nowMs: Long,
        widthPx: Int,
        heightPx: Int,
    ): Bitmap {
        val bar = HeatBarModel.build(intervals, probabilities, dayStartMs, dayEndMs, nowMs)
        val w = widthPx.coerceAtLeast(1)
        val h = heightPx.coerceAtLeast(1)
        val bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        val width = w.toFloat()
        val height = h.toFloat()

        canvas.drawColor(Color.parseColor("#F4F2F0"))
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)

        // Predicted "usually on" ribbon as the background (strong), then today's
        // status overlaid: ON solid red, OFF/unknown a light wash so the ribbon
        // shows through — a single consolidated bar, matching the phone/web.
        bar.ribbon.forEach { slot ->
            val alpha = (20 + slot.probability * 205).toInt().coerceIn(0, 255)
            paint.color = Color.argb(alpha, 0xC7, 0x3E, 0x1D)
            canvas.drawRect((slot.left * width).toFloat(), 0f, ((slot.left + slot.width) * width).toFloat(), height, paint)
        }
        bar.segments.forEach { seg ->
            paint.color = colorFor(seg.status)
            paint.alpha = alphaFor(seg.status)
            canvas.drawRect((seg.left * width).toFloat(), 0f, ((seg.left + seg.width) * width).toFloat(), height, paint)
        }
        bar.nowFraction?.let { fraction ->
            val nowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
                color = Color.parseColor("#1B1B1B")
                strokeWidth = height * 0.06f
            }
            canvas.drawLine((fraction * width).toFloat(), 0f, (fraction * width).toFloat(), height, nowPaint)
        }
        return bitmap
    }

    private fun colorFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#C73E1D")
        HotLightStatus.OFF -> Color.parseColor("#FBFAF8")
        HotLightStatus.UNKNOWN -> Color.parseColor("#CACACA")
    }

    private fun alphaFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> 255
        HotLightStatus.OFF -> 90
        HotLightStatus.UNKNOWN -> 70
    }
}
