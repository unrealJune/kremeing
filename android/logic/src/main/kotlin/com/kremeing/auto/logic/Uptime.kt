package com.kremeing.auto.logic

import java.time.DayOfWeek
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.floor

/** A contiguous status segment over `[startMs, endMs)`. */
data class Interval(val startMs: Long, val endMs: Long, val status: HotLightStatus)

/** A single status flip reduced to an epoch-millis instant. */
data class FlipPoint(val status: HotLightStatus, val atMs: Long)

/** Local-day window `[startMs, endMs)`. */
data class DayBounds(val startMs: Long, val endMs: Long)

/** Segments of one local day, clipped to that day. */
data class DaySegments(val dayStartMs: Long, val dayEndMs: Long, val segments: List<Interval>)

/** Result of [Uptime.commonOnProbabilities]: P(on) per slot plus its basis. */
data class OnProbabilities(
    val probabilities: FloatArray,
    val basis: String,
    val sampleDays: Int,
)

/**
 * Everything needed to render a store's heat bar + 90-day grid for "today",
 * derived once from a flip list so the phone and car renderers stay in sync.
 */
class DayHeatBar(
    val todayIntervals: List<Interval>,
    val probabilities: FloatArray?,
    val basisLabel: String?,
    val dayStartMs: Long,
    val dayEndMs: Long,
    val nowMs: Long,
    val daySegments: List<DaySegments>,
)

/**
 * Reconstructs hot-light history from raw flip events — a faithful Kotlin port
 * of `web/uptime-utils.js`, so the phone and car render exactly what the web
 * does. Pure and deterministic (a [ZoneId] is threaded in rather than read from
 * the environment) so it's exhaustively unit-tested off-device.
 *
 * Day boundaries use *local* time (matching the web's `new Date(y,m,d)`), with a
 * constant 24h visual scale so DST never shifts the timeline.
 */
object Uptime {

    /** Half-hour probability slots — fine enough for an overlay ribbon. */
    const val SLOT_COUNT = 48

    /**
     * Reduce a contiguous flip list into on/off/unknown [Interval]s covering
     * exactly `[sinceMs, untilMs)`. The latest flip on/before `sinceMs` anchors
     * the starting status; ranges starting before any flip begin `UNKNOWN`.
     * Adjacent same-status segments are merged.
     */
    fun flipsToIntervals(flips: List<FlipPoint>, sinceMs: Long, untilMs: Long): List<Interval> {
        if (untilMs <= sinceMs) return emptyList()
        val list = flips.sortedBy { it.atMs }

        // Anchor: status of the latest flip at/before sinceMs (else UNKNOWN).
        var curStatus = HotLightStatus.UNKNOWN
        for (f in list) {
            if (f.atMs <= sinceMs) curStatus = f.status else break
        }

        val out = ArrayList<Interval>()
        fun push(start: Long, end: Long, status: HotLightStatus) {
            if (end <= start) return
            val last = out.lastOrNull()
            if (last != null && last.status == status && last.endMs == start) {
                out[out.size - 1] = last.copy(endMs = end)   // merge adjacent
            } else {
                out.add(Interval(start, end, status))
            }
        }

        var cursor = sinceMs
        for (f in list) {
            if (f.atMs <= sinceMs) continue
            if (f.atMs >= untilMs) break
            push(cursor, f.atMs, curStatus)
            cursor = f.atMs
            curStatus = f.status
        }
        push(cursor, untilMs, curStatus)
        return out
    }

    /** `[startMs, startMs+24h)` for the local day containing [epochMs]. */
    fun localDayBounds(epochMs: Long, zone: ZoneId): DayBounds {
        val date = Instant.ofEpochMilli(epochMs).atZone(zone).toLocalDate()
        val startMs = date.atStartOfDay(zone).toInstant().toEpochMilli()
        // Constant 24h (not the snapped next-midnight) so the visual scale is
        // stable across DST — mirrors the web's `start + 24h`.
        return DayBounds(startMs, startMs + DAY_MS)
    }

    /**
     * Slice [intervals] along local-day boundaries, keyed by `yyyy-MM-dd`. Each
     * day's segments are clipped to that day and to `[sinceMs, untilMs)`.
     */
    fun splitIntervalsByLocalDay(
        intervals: List<Interval>,
        sinceMs: Long,
        untilMs: Long,
        zone: ZoneId,
    ): Map<String, DaySegments> {
        val byDay = LinkedHashMap<String, DaySegments>()
        if (intervals.isEmpty()) return byDay

        var dayStart = localDayBounds(sinceMs, zone).startMs
        while (dayStart < untilMs) {
            // +26h then snap to local midnight handles DST cleanly.
            val dayEnd = localDayBounds(dayStart + 26L * 3600 * 1000, zone).startMs
            val key = formatLocalDateKey(dayStart, zone)
            val clipStart = maxOf(dayStart, sinceMs)
            val clipEnd = minOf(dayEnd, untilMs)
            val segs = ArrayList<Interval>()
            for (iv in intervals) {
                if (iv.endMs <= clipStart || iv.startMs >= clipEnd) continue
                segs.add(
                    Interval(
                        maxOf(iv.startMs, clipStart),
                        minOf(iv.endMs, clipEnd),
                        iv.status,
                    ),
                )
            }
            byDay[key] = DaySegments(dayStart, dayEnd, segs)
            dayStart = dayEnd
        }
        return byDay
    }

    /**
     * P(on | slot-of-day) for [targetMs]'s day, using prior days from
     * [intervals]. Prefers same-weekday history (≥4 days), then weekday/weekend
     * class (≥4), then all days (≥[minDays]); returns null when there's too
     * little history (caller hides the ribbon). Output is a 48-slot probability
     * array smoothed with a 3-tap moving average.
     */
    fun commonOnProbabilities(
        intervals: List<Interval>,
        targetMs: Long,
        zone: ZoneId,
        minDays: Int = 14,
    ): OnProbabilities? {
        val targetZdt = Instant.ofEpochMilli(targetMs).atZone(zone)
        val targetDow = targetZdt.dayOfWeek
        val targetIsWeekend = targetDow.isWeekend()

        val todayStart = localDayBounds(targetMs, zone).startMs
        var earliestMs = todayStart
        for (iv in intervals) if (iv.startMs < earliestMs) earliestMs = iv.startMs
        if (earliestMs >= todayStart) return null   // no history before today

        val byDay = splitIntervalsByLocalDay(intervals, earliestMs, todayStart, zone)

        val sameWeekday = ArrayList<DaySegments>()
        val sameClass = ArrayList<DaySegments>()
        val all = ArrayList<DaySegments>()
        for (dayInfo in byDay.values) {
            if (!dayHasObservation(dayInfo.segments)) continue
            val dow = Instant.ofEpochMilli(dayInfo.dayStartMs).atZone(zone).dayOfWeek
            all.add(dayInfo)
            if (dow.isWeekend() == targetIsWeekend) sameClass.add(dayInfo)
            if (dow == targetDow) sameWeekday.add(dayInfo)
        }

        val pool: List<DaySegments>
        val basis: String
        when {
            sameWeekday.size >= 4 -> { pool = sameWeekday; basis = "weekday" }
            sameClass.size >= 4 -> { pool = sameClass; basis = "weekday-class" }
            all.size >= minDays -> { pool = all; basis = "all" }
            else -> return null
        }

        val slotSec = (24.0 * 3600) / SLOT_COUNT
        val onSec = DoubleArray(SLOT_COUNT)
        val obsSec = DoubleArray(SLOT_COUNT)
        for (dayInfo in pool) {
            for (seg in dayInfo.segments) {
                val startS = (seg.startMs - dayInfo.dayStartMs) / 1000.0
                val endS = (seg.endMs - dayInfo.dayStartMs) / 1000.0
                var s = startS
                while (s < endS) {
                    val slot = minOf(SLOT_COUNT - 1, floor(s / slotSec).toInt())
                    val slotEndS = (slot + 1) * slotSec
                    val chunk = minOf(endS, slotEndS) - s
                    if (seg.status != HotLightStatus.UNKNOWN) obsSec[slot] += chunk
                    if (seg.status == HotLightStatus.ON) onSec[slot] += chunk
                    s += chunk
                }
            }
        }

        val raw = FloatArray(SLOT_COUNT) { i ->
            if (obsSec[i] > 0) (onSec[i] / obsSec[i]).toFloat() else 0f
        }
        val smoothed = FloatArray(SLOT_COUNT) { i ->
            val a = raw[(i - 1 + SLOT_COUNT) % SLOT_COUNT]
            val b = raw[i]
            val c = raw[(i + 1) % SLOT_COUNT]
            (a + b + c) / 3f
        }
        return OnProbabilities(smoothed, basis, pool.size)
    }

    /** True if any segment was actually observed (status != unknown). */
    fun dayHasObservation(segments: List<Interval>): Boolean =
        segments.any { it.status != HotLightStatus.UNKNOWN }

    /** A half-open slot range `[startSlot, endSlot)` of the 48 half-hour day slots. */
    data class SlotWindow(val startSlot: Int, val endSlot: Int)

    /**
     * Derive the typical "usually on" windows from a [probabilities] array (48
     * half-hour slots): contiguous runs of slots whose P(on) is at least
     * [threshold], merged across small one-slot gaps so a brief dip doesn't split
     * a window. Returns the runs as half-open [SlotWindow]s, longest-first.
     *
     * Used to turn the heat-bar prediction into big, glanceable text on the car
     * detail screen (e.g. "Usually hot 7:00–9:30 AM").
     */
    fun usualOnWindows(probabilities: FloatArray, threshold: Float = 0.5f): List<SlotWindow> {
        if (probabilities.isEmpty()) return emptyList()
        val on = BooleanArray(probabilities.size) { probabilities[it] >= threshold }
        // Bridge single-slot gaps between two "on" slots.
        for (i in 1 until on.size - 1) {
            if (!on[i] && on[i - 1] && on[i + 1]) on[i] = true
        }
        val windows = ArrayList<SlotWindow>()
        var start = -1
        for (i in on.indices) {
            if (on[i] && start < 0) start = i
            if (!on[i] && start >= 0) {
                windows.add(SlotWindow(start, i)); start = -1
            }
        }
        if (start >= 0) windows.add(SlotWindow(start, on.size))
        return windows.sortedByDescending { it.endSlot - it.startSlot }
    }

    /** Epoch-millis at the start of [slot] (0..48) within the day beginning at [dayStartMs]. */
    fun slotStartMs(dayStartMs: Long, dayEndMs: Long, slot: Int): Long =
        dayStartMs + ((dayEndMs - dayStartMs) * slot) / SLOT_COUNT

    /** Human label for a [commonOnProbabilities] basis (e.g. "Mondays", "weekends"). */
    fun basisLabel(basis: String, targetMs: Long, zone: ZoneId): String = when (basis) {
        "weekday" -> when (Instant.ofEpochMilli(targetMs).atZone(zone).dayOfWeek) {
            DayOfWeek.MONDAY -> "Mondays"
            DayOfWeek.TUESDAY -> "Tuesdays"
            DayOfWeek.WEDNESDAY -> "Wednesdays"
            DayOfWeek.THURSDAY -> "Thursdays"
            DayOfWeek.FRIDAY -> "Fridays"
            DayOfWeek.SATURDAY -> "Saturdays"
            DayOfWeek.SUNDAY -> "Sundays"
        }
        "weekday-class" ->
            if (Instant.ofEpochMilli(targetMs).atZone(zone).dayOfWeek.isWeekend()) "weekends" else "weekdays"
        else -> "all days"
    }

    /**
     * Reduce a store's [flips] into everything a heat-bar renderer needs for the
     * local day containing [nowMs]: today's segments, the "usually on" ribbon and
     * its basis label, the day bounds, and the per-day split for the 90-day grid.
     */
    fun todayHeatBar(flips: List<FlipPoint>, nowMs: Long, zone: ZoneId): DayHeatBar {
        val day = localDayBounds(nowMs, zone)
        val earliest = flips.minOfOrNull { it.atMs } ?: day.startMs
        val historyStart = minOf(earliest, day.startMs)
        val allIntervals = flipsToIntervals(flips, historyStart, day.endMs)
        val todayIntervals = flipsToIntervals(flips, day.startMs, day.endMs)
        val probability = commonOnProbabilities(allIntervals, nowMs, zone)
        val basis = probability?.let { basisLabel(it.basis, nowMs, zone) }
        val byDay = splitIntervalsByLocalDay(allIntervals, historyStart, day.endMs, zone)
        return DayHeatBar(
            todayIntervals = todayIntervals,
            probabilities = probability?.probabilities,
            basisLabel = basis,
            dayStartMs = day.startMs,
            dayEndMs = day.endMs,
            nowMs = nowMs,
            daySegments = byDay.values.toList(),
        )
    }

    /** Convert wire [HotLightFlip]s to [FlipPoint]s, dropping unparseable times. */
    fun parseFlips(flips: List<HotLightFlip>): List<FlipPoint> =
        flips.mapNotNull { f ->
            val ms = parseInstantMs(f.observedAt) ?: return@mapNotNull null
            FlipPoint(HotLightStatus.fromWire(f.status), ms)
        }

    /** Parse an ISO-8601 instant (with offset) to epoch millis, or null. */
    fun parseInstantMs(iso: String): Long? =
        try {
            OffsetDateTime.parse(iso).toInstant().toEpochMilli()
        } catch (_: Exception) {
            try {
                Instant.parse(iso).toEpochMilli()
            } catch (_: Exception) {
                null
            }
        }

    private const val DAY_MS = 24L * 3600 * 1000

    private fun formatLocalDateKey(epochMs: Long, zone: ZoneId): String =
        Instant.ofEpochMilli(epochMs).atZone(zone).toLocalDate().format(DateTimeFormatter.ISO_LOCAL_DATE)

    private fun DayOfWeek.isWeekend(): Boolean =
        this == DayOfWeek.SATURDAY || this == DayOfWeek.SUNDAY
}
