package com.kremeing.auto.logic

/**
 * The renderer-agnostic shape of the "DayTimeline" heat bar — the shared seam
 * the interactive phone [android.view.View] and the static car bitmap both
 * consume, so they stay pixel-faithful to the web's `HourStrip`.
 *
 * All positions are fractions in `[0, 1]` of the bar's width; renderers scale by
 * their own pixel width. Status -> colour and probability -> alpha mapping live
 * in the renderers (matching `web/islands.jsx`), not here, so this stays pure
 * and unit-testable.
 */
object HeatBarModel {

    /** A live status block: `[left, left+width)` of the bar, with its status. */
    data class Segment(val left: Double, val width: Double, val status: HotLightStatus)

    /** One "usually on" ribbon slot; [probability] is 0..1 (renderer -> alpha). */
    data class RibbonSlot(val left: Double, val width: Double, val probability: Float)

    /** The full bar: ribbon (behind), live segments (front), and the now-marker. */
    data class HeatBar(
        val segments: List<Segment>,
        val ribbon: List<RibbonSlot>,
        /** Fraction `[0,1]` of "now" within the day, or null if now is outside it. */
        val nowFraction: Double?,
    )

    /**
     * Build the bar for the local day `[dayStartMs, dayEndMs)` from reconstructed
     * [intervals] and optional per-slot [probabilities] (from
     * [Uptime.commonOnProbabilities]). Segments are clamped to the day and
     * zero-width pieces dropped; the now-marker is null when [nowMs] falls
     * outside the day.
     */
    fun build(
        intervals: List<Interval>,
        probabilities: FloatArray?,
        dayStartMs: Long,
        dayEndMs: Long,
        nowMs: Long,
    ): HeatBar {
        val dayLen = (dayEndMs - dayStartMs).toDouble()
        if (dayLen <= 0.0) return HeatBar(emptyList(), emptyList(), null)

        fun frac(ms: Long): Double = ((ms - dayStartMs) / dayLen).coerceIn(0.0, 1.0)

        val segments = intervals.mapNotNull { iv ->
            val left = frac(iv.startMs)
            val right = frac(iv.endMs)
            val width = right - left
            if (width <= 0.0) null else Segment(left, width, iv.status)
        }

        val ribbon = if (probabilities != null && probabilities.isNotEmpty()) {
            val slotW = 1.0 / probabilities.size
            probabilities.mapIndexed { i, p -> RibbonSlot(i * slotW, slotW, p) }
        } else {
            emptyList()
        }

        val nowFraction = if (nowMs in dayStartMs..dayEndMs) frac(nowMs) else null
        return HeatBar(segments, ribbon, nowFraction)
    }
}
