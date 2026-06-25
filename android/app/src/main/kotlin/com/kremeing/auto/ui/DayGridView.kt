package com.kremeing.auto.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.View
import com.kremeing.auto.logic.DaySegments
import com.kremeing.auto.logic.HeatBarModel
import com.kremeing.auto.logic.HotLightStatus

/**
 * The "last 90 days" grid — a native port of the web's `NinetyDayGrid`. Renders
 * one thin horizontal pill per local day (most recent last), each showing that
 * day's on/off/unknown pattern across a fixed 24h scale, so weekly rhythms are
 * visible at a glance. Geometry comes from the shared [HeatBarModel].
 */
class DayGridView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : View(context, attrs) {

    private var days: List<HeatBarModel.HeatBar> = emptyList()
    private val segmentPaint = Paint(Paint.ANTI_ALIAS_FLAG)

    private val barHeight: Float get() = dp(7f)
    private val gap: Float get() = dp(1f)

    /** Day-split intervals (oldest first), from `Uptime.splitIntervalsByLocalDay`. */
    fun setData(daySegments: List<DaySegments>) {
        days = daySegments.map { day ->
            HeatBarModel.build(day.segments, null, day.dayStartMs, day.dayStartMs + DAY_MS, 0L)
        }
        requestLayout()
        invalidate()
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = (days.size * (barHeight + gap)).toInt().coerceAtLeast(0)
        setMeasuredDimension(width, height)
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        days.forEachIndexed { index, bar ->
            val top = index * (barHeight + gap)
            bar.segments.forEach { seg ->
                segmentPaint.color = colorFor(seg.status)
                segmentPaint.alpha = alphaFor(seg.status)
                canvas.drawRect(
                    (seg.left * w).toFloat(),
                    top,
                    ((seg.left + seg.width) * w).toFloat(),
                    top + barHeight,
                    segmentPaint,
                )
            }
        }
    }

    private fun colorFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#C73E1D")
        HotLightStatus.OFF -> Color.parseColor("#E8E6E3")
        HotLightStatus.UNKNOWN -> Color.parseColor("#CACACA")
    }

    private fun alphaFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> 255
        HotLightStatus.OFF -> (0.85f * 255).toInt()
        HotLightStatus.UNKNOWN -> (0.55f * 255).toInt()
    }

    private fun dp(value: Float): Float = value * resources.displayMetrics.density

    private companion object {
        const val DAY_MS = 24L * 3600 * 1000
    }
}
