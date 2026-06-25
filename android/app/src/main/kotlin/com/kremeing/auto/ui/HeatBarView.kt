package com.kremeing.auto.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import com.kremeing.auto.logic.HeatBarModel
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.Interval
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * The interactive "DayTimeline" heat bar — a native port of the web app's
 * `HourStrip`. Renders, back-to-front:
 *  1. a "usually on" probability ribbon (red tint, alpha ∝ P(on)),
 *  2. the live on/off/unknown status segments for the day,
 *  3. a "now" marker, and a hover caret while scrubbing.
 *
 * Colours/opacities and the ribbon alpha mapping match the web (`islands.jsx`).
 * All geometry comes from the shared, unit-tested [HeatBarModel]; this view only
 * paints it and translates touches into a scrub readout via [onScrub].
 */
class HeatBarView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : View(context, attrs) {

    /** Invoked with the scrub readout text while the user drags, null on release. */
    var onScrub: ((String?) -> Unit)? = null

    private var intervals: List<Interval> = emptyList()
    private var probabilities: FloatArray? = null
    private var basisLabel: String? = null
    private var dayStartMs: Long = 0
    private var dayEndMs: Long = 0
    private var bar: HeatBarModel.HeatBar? = null
    private var hoverFraction: Float? = null

    private val ribbonPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val segmentPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#F4F2F0") }
    private val nowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#1B1B1B")
        strokeWidth = dp(2f)
    }
    private val hoverPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#C73E1D")
        strokeWidth = dp(1f)
    }
    private val timeFormat = SimpleDateFormat("h:mm a", Locale.getDefault())

    /**
     * Set the bar's data for a single local day.
     *
     * @param intervals      today's on/off/unknown segments (within the day)
     * @param probabilities  per-slot P(on) from `Uptime.commonOnProbabilities`, or null
     * @param basisLabel     friendly basis (e.g. "weekdays"), or null
     */
    fun setData(
        intervals: List<Interval>,
        probabilities: FloatArray?,
        basisLabel: String?,
        dayStartMs: Long,
        dayEndMs: Long,
        nowMs: Long,
    ) {
        this.intervals = intervals
        this.probabilities = probabilities
        this.basisLabel = basisLabel
        this.dayStartMs = dayStartMs
        this.dayEndMs = dayEndMs
        this.bar = HeatBarModel.build(intervals, probabilities, dayStartMs, dayEndMs, nowMs)
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        val radius = dp(6f)
        val rect = RectF(0f, 0f, w, h)
        canvas.drawRoundRect(rect, radius, radius, bgPaint)

        val b = bar ?: return

        canvas.save()
        canvas.clipPath(Path().apply { addRoundRect(rect, radius, radius, Path.Direction.CW) })

        // 1. "usually on" ribbon — red tint, alpha 16..102 (matches the web).
        // 1. predicted "usually on" ribbon — the bar's background. Drawn strong
        //    (alpha up to ~225) so the typical daily schedule is clearly visible
        //    even when the store is off now.
        b.ribbon.forEach { slot ->
            val alpha = (20 + slot.probability * 205).toInt().coerceIn(0, 255)
            ribbonPaint.color = Color.argb(alpha, 0xC7, 0x3E, 0x1D)
            canvas.drawRect((slot.left * w).toFloat(), 0f, ((slot.left + slot.width) * w).toFloat(), h, ribbonPaint)
        }

        // 2. today's live status overlaid: ON solid bold red; OFF/unknown a
        //    light translucent wash so the predicted ribbon shows through.
        b.segments.forEach { seg ->
            segmentPaint.color = colorFor(seg.status)
            segmentPaint.alpha = alphaFor(seg.status)
            canvas.drawRect((seg.left * w).toFloat(), 0f, ((seg.left + seg.width) * w).toFloat(), h, segmentPaint)
        }
        canvas.restore()

        // 3. now marker + hover caret (drawn on top, un-clipped so the 2px line shows).
        b.nowFraction?.let { canvas.drawLine((it * w).toFloat(), 0f, (it * w).toFloat(), h, nowPaint) }
        hoverFraction?.let { canvas.drawLine(it * w, 0f, it * w, h, hoverPaint) }
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                parent?.requestDisallowInterceptTouchEvent(true)
                val fraction = (event.x / width.toFloat()).coerceIn(0f, 1f)
                hoverFraction = fraction
                onScrub?.invoke(readoutAt(fraction))
                invalidate()
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                parent?.requestDisallowInterceptTouchEvent(false)
                hoverFraction = null
                onScrub?.invoke(null)
                invalidate()
            }
        }
        return true
    }

    private fun readoutAt(fraction: Float): String {
        val ms = dayStartMs + (fraction * (dayEndMs - dayStartMs)).toLong()
        val current = intervals.firstOrNull { ms >= it.startMs && ms < it.endMs }
        val statusWord = when (current?.status) {
            HotLightStatus.ON -> "ON"
            HotLightStatus.OFF -> "OFF"
            HotLightStatus.UNKNOWN -> "Unknown"
            null -> "—"
        }
        val out = StringBuilder()
        out.append(timeFormat.format(Date(ms))).append(" · ").append(statusWord)
        if (current != null && current.status != HotLightStatus.UNKNOWN) {
            out.append(" (since ").append(timeFormat.format(Date(current.startMs))).append(")")
        }
        probabilities?.let { p ->
            if (p.isNotEmpty()) {
                val span = (dayEndMs - dayStartMs).toDouble()
                val slot = (((ms - dayStartMs) / span) * p.size).toInt().coerceIn(0, p.size - 1)
                out.append(" · usually on ").append((p[slot] * 100).toInt()).append('%')
                basisLabel?.let { out.append(" of ").append(it) }
            }
        }
        return out.toString()
    }

    private fun colorFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#C73E1D")
        HotLightStatus.OFF -> Color.parseColor("#FBFAF8")   // light wash over the ribbon
        HotLightStatus.UNKNOWN -> Color.parseColor("#CACACA")
    }

    private fun alphaFor(status: HotLightStatus): Int = when (status) {
        // ON: solid bold red so the current/actual-on periods read clearly.
        HotLightStatus.ON -> 255
        // OFF: a faint light wash that mutes — but doesn't hide — the predicted
        // ribbon underneath, so "usually on but off now" still shows the schedule.
        HotLightStatus.OFF -> 90
        HotLightStatus.UNKNOWN -> 70
    }

    private fun dp(value: Float): Float = value * resources.displayMetrics.density
}
