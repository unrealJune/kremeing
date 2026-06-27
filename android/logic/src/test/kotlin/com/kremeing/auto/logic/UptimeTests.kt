package com.kremeing.auto.logic

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

/**
 * Kotlin port of `web/uptime-utils.test.mjs`. Pinned to UTC (the web tests rely
 * on the host's local zone) so the same assertions hold deterministically in CI.
 */
class FlipsToIntervalsTests {
    private fun fp(status: String, atMs: Long) = FlipPoint(HotLightStatus.fromWire(status), atMs)
    private fun iv(start: Long, end: Long, status: String) =
        Interval(start, end, HotLightStatus.fromWire(status))

    @Test fun `empty input is a single unknown segment over the range`() {
        assertEquals(listOf(iv(0, 1000, "unknown")), Uptime.flipsToIntervals(emptyList(), 0, 1000))
    }

    @Test fun `empty range returns empty`() {
        assertEquals(emptyList<Interval>(), Uptime.flipsToIntervals(listOf(fp("on", 5)), 10, 10))
        assertEquals(emptyList<Interval>(), Uptime.flipsToIntervals(emptyList(), 10, 5))
    }

    @Test fun `anchor flip at or before since defines the starting status`() {
        val flips = listOf(fp("off", 0), fp("on", 2000))
        assertEquals(
            listOf(iv(1000, 2000, "off"), iv(2000, 3000, "on")),
            Uptime.flipsToIntervals(flips, 1000, 3000),
        )
    }

    @Test fun `no flip before since starts unknown`() {
        val flips = listOf(fp("on", 500), fp("off", 800))
        assertEquals(
            listOf(iv(0, 500, "unknown"), iv(500, 800, "on"), iv(800, 1000, "off")),
            Uptime.flipsToIntervals(flips, 0, 1000),
        )
    }

    @Test fun `flips outside the range are ignored but the last anchor applies`() {
        val flips = listOf(fp("on", 100), fp("off", 200), fp("on", 5000))
        assertEquals(listOf(iv(500, 1000, "off")), Uptime.flipsToIntervals(flips, 500, 1000))
    }

    @Test fun `adjacent same-status flips are merged`() {
        val flips = listOf(fp("on", 0), fp("on", 100), fp("on", 200))
        assertEquals(listOf(iv(0, 300, "on")), Uptime.flipsToIntervals(flips, 0, 300))
    }

    @Test fun `unsorted input still produces correct segments`() {
        val flips = listOf(fp("on", 2000), fp("off", 0), fp("off", 3000))
        assertEquals(
            listOf(iv(0, 2000, "off"), iv(2000, 3000, "on"), iv(3000, 4000, "off")),
            Uptime.flipsToIntervals(flips, 0, 4000),
        )
    }

    @Test fun `parseFlips reads ISO instants with offset`() {
        val flips = listOf(
            HotLightFlip(899, "off", "2026-06-24T00:00:00+00:00"),
            HotLightFlip(899, "on", "2026-06-24T09:00:00.537424+00:00"),
        )
        val pts = Uptime.parseFlips(flips)
        assertEquals(2, pts.size)
        assertEquals(HotLightStatus.OFF, pts[0].status)
        assertEquals(HotLightStatus.ON, pts[1].status)
        assertTrue(pts[1].atMs > pts[0].atMs)
    }
}

class CommonOnProbabilitiesTests {
    private val utc = ZoneOffset.UTC
    private val day = 86_400_000L

    private fun atUtc(date: LocalDate, hour: Int) =
        date.atTime(hour, 0).toInstant(utc).toEpochMilli()

    private fun dayStart(ms: Long) = Uptime.localDayBounds(ms, utc).startMs

    @Test fun `localDayBounds is local midnight spanning 24h`() {
        val noon = atUtc(LocalDate.of(2024, 6, 15), 12)
        val b = Uptime.localDayBounds(noon, utc)
        assertEquals(0, Instant.ofEpochMilli(b.startMs).atZone(utc).hour)
        assertEquals(day, b.endMs - b.startMs)
    }

    @Test fun `returns null when there is too little history`() {
        val today = atUtc(LocalDate.of(2024, 6, 15), 9)
        val yesterday = today - day
        val intervals = listOf(Interval(yesterday, today, HotLightStatus.ON))
        assertNull(Uptime.commonOnProbabilities(intervals, today, utc))
    }

    @Test fun `prefers same-weekday basis with at least four weeks`() {
        val target = atUtc(LocalDate.of(2024, 6, 18), 12)   // a Tuesday
        val intervals = ArrayList<Interval>()
        for (d in 1..56) {
            val ds = dayStart(target - d * day)
            val de = ds + day
            val dow = Instant.ofEpochMilli(ds).atZone(utc).dayOfWeek
            if (dow == DayOfWeek.TUESDAY) {
                val onStart = ds + 9 * 3_600_000L
                val onEnd = ds + 10 * 3_600_000L
                intervals += Interval(ds, onStart, HotLightStatus.OFF)
                intervals += Interval(onStart, onEnd, HotLightStatus.ON)
                intervals += Interval(onEnd, de, HotLightStatus.OFF)
            } else {
                intervals += Interval(ds, de, HotLightStatus.OFF)
            }
        }
        val r = Uptime.commonOnProbabilities(intervals, target, utc)
        assertTrue(r != null)
        assertEquals("weekday", r!!.basis)
        assertTrue(r.sampleDays >= 8)
        assertTrue(r.probabilities[19] > 0.5f) { "9:30am slot was ${r.probabilities[19]}" }
        assertTrue(r.probabilities[6] < 0.1f) { "3am slot was ${r.probabilities[6]}" }
    }

    @Test fun `falls back to weekday-class when same-weekday is thin`() {
        val target = atUtc(LocalDate.of(2024, 6, 18), 12)   // Tuesday
        val intervals = ArrayList<Interval>()
        for (d in 1..21) {
            val ds = dayStart(target - d * day)
            val dow = Instant.ofEpochMilli(ds).atZone(utc).dayOfWeek
            if (dow == DayOfWeek.TUESDAY && d > 7) continue
            intervals += Interval(ds, ds + day, HotLightStatus.OFF)
        }
        val r = Uptime.commonOnProbabilities(intervals, target, utc)
        assertTrue(r != null)
        assertTrue(r!!.basis == "weekday-class" || r.basis == "all") { "basis was ${r.basis}" }
    }

    @Test fun `basisLabel maps to friendly text`() {
        val tuesday = atUtc(LocalDate.of(2024, 6, 18), 12)
        assertEquals("Tuesdays", Uptime.basisLabel("weekday", tuesday, utc))
        assertEquals("weekdays", Uptime.basisLabel("weekday-class", tuesday, utc))
        assertEquals("all days", Uptime.basisLabel("all", tuesday, utc))
        val sunday = atUtc(LocalDate.of(2024, 6, 16), 12)
        assertEquals("weekends", Uptime.basisLabel("weekday-class", sunday, utc))
    }

    @Test fun `todayHeatBar yields today's segments and day bounds`() {
        val now = atUtc(LocalDate.of(2024, 6, 18), 12)
        val day = Uptime.localDayBounds(now, utc)
        val flips = listOf(
            FlipPoint(HotLightStatus.OFF, day.startMs),
            FlipPoint(HotLightStatus.ON, day.startMs + 9 * 3_600_000L),
        )
        val bar = Uptime.todayHeatBar(flips, now, utc)
        assertEquals(day.startMs, bar.dayStartMs)
        assertEquals(day.endMs, bar.dayEndMs)
        assertEquals(now, bar.nowMs)
        assertTrue(bar.todayIntervals.isNotEmpty())
        // The light flipped on at 9am, so the live bar is currently ON.
        assertEquals(HotLightStatus.ON, bar.todayIntervals.last().status)
        // ...but the live bar must stop at "now" (noon), not run to end of day —
        // the predictive ribbon, not the live segment, covers the rest of the day.
        assertEquals(now, bar.todayIntervals.last().endMs)
    }

    @Test fun `usualOnWindows finds contiguous high-probability runs longest first`() {
        // 48 slots (half-hours). On 7:00-9:00am (slots 14-17) and a longer
        // 5:00-8:00pm (slots 34-39).
        val probs = FloatArray(48) { 0.1f }
        for (i in 14..17) probs[i] = 0.8f   // 4 slots = 2h
        for (i in 34..39) probs[i] = 0.9f   // 6 slots = 3h
        val windows = Uptime.usualOnWindows(probs, threshold = 0.5f)
        assertEquals(2, windows.size)
        // Longest first: the evening run.
        assertEquals(Uptime.SlotWindow(34, 40), windows[0])
        assertEquals(Uptime.SlotWindow(14, 18), windows[1])
    }

    @Test fun `usualOnWindows bridges a single-slot dip`() {
        val probs = FloatArray(48) { 0.1f }
        for (i in 14..19) probs[i] = 0.8f
        probs[17] = 0.2f   // brief dip mid-window
        val windows = Uptime.usualOnWindows(probs, threshold = 0.5f)
        assertEquals(listOf(Uptime.SlotWindow(14, 20)), windows)
    }

    @Test fun `usualOnWindows is empty when nothing clears the threshold`() {
        assertTrue(Uptime.usualOnWindows(FloatArray(48) { 0.2f }).isEmpty())
        assertTrue(Uptime.usualOnWindows(FloatArray(0)).isEmpty())
    }

    @Test fun `slotStartMs maps slot index to time of day`() {
        val day = Uptime.localDayBounds(atUtc(LocalDate.of(2024, 6, 18), 12), utc)
        // Slot 14 = 7:00am (14 * 30min).
        assertEquals(day.startMs + 7 * 3_600_000L, Uptime.slotStartMs(day.startMs, day.endMs, 14))
        assertEquals(day.startMs, Uptime.slotStartMs(day.startMs, day.endMs, 0))
        assertEquals(day.endMs, Uptime.slotStartMs(day.startMs, day.endMs, 48))
    }
}

class HeatBarModelTests {
    @Test fun `segments are day fractions and now-marker is set`() {
        val intervals = listOf(
            Interval(0, 250, HotLightStatus.OFF),
            Interval(250, 750, HotLightStatus.ON),
            Interval(750, 1000, HotLightStatus.OFF),
        )
        val bar = HeatBarModel.build(intervals, null, 0, 1000, nowMs = 500)
        assertEquals(3, bar.segments.size)
        assertEquals(0.25, bar.segments[0].width, 1e-9)
        assertEquals(0.25, bar.segments[1].left, 1e-9)
        assertEquals(0.5, bar.segments[1].width, 1e-9)
        assertEquals(HotLightStatus.ON, bar.segments[1].status)
        assertEquals(0.5, bar.nowFraction!!, 1e-9)
        assertTrue(bar.ribbon.isEmpty())
    }

    @Test fun `clamps out-of-range and drops zero-width segments`() {
        val bar = HeatBarModel.build(
            listOf(
                Interval(-100, 0, HotLightStatus.ON),    // fully before day -> dropped
                Interval(0, 0, HotLightStatus.ON),       // zero width -> dropped
                Interval(500, 2000, HotLightStatus.ON),  // clamped to 0.5..1.0
            ),
            probabilities = null,
            dayStartMs = 0,
            dayEndMs = 1000,
            nowMs = -5,
        )
        assertEquals(1, bar.segments.size)
        assertEquals(0.5, bar.segments[0].left, 1e-9)
        assertEquals(0.5, bar.segments[0].width, 1e-9)
        assertNull(bar.nowFraction)
    }

    @Test fun `ribbon slots tile the bar`() {
        val probs = FloatArray(Uptime.SLOT_COUNT) { it / Uptime.SLOT_COUNT.toFloat() }
        val bar = HeatBarModel.build(emptyList(), probs, 0, 1000, nowMs = 0)
        assertEquals(Uptime.SLOT_COUNT, bar.ribbon.size)
        assertEquals(0.0, bar.ribbon[0].left, 1e-9)
        assertEquals(1.0 / Uptime.SLOT_COUNT, bar.ribbon[0].width, 1e-9)
        assertEquals(47f / Uptime.SLOT_COUNT, bar.ribbon[47].probability, 1e-6f)
    }
}

class UptimeCodecTests {
    @Test fun `decodes history flips`() {
        val body = """
            {"storeId":899,"rangeStart":"2026-06-17T00:00:00+00:00",
             "rangeEnd":"2026-06-24T00:00:00+00:00",
             "flips":[{"storeId":899,"status":"on","observedAt":"2026-06-24T16:30:03.537424+00:00"}]}
        """.trimIndent()
        val h = ApiCodec.decodeHistory(body)
        assertEquals(899, h.storeId)
        assertEquals(1, h.flips.size)
        assertEquals("on", h.flips[0].status)
        assertTrue((Uptime.parseInstantMs(h.flips[0].observedAt) ?: 0) > 0)
    }

    @Test fun `decodes uptime buckets ignoring unknown keys`() {
        val body = """
            {"storeId":1,"bucket":"hour","buckets":[
              {"startUtc":"2026-06-24T00:00:00+00:00","endUtc":"2026-06-24T01:00:00+00:00",
               "onSeconds":1800,"offSeconds":1800,"observedSeconds":3600,"totalSeconds":3600,
               "fractionOn":0.5,"futureField":"ignored"}]}
        """.trimIndent()
        val u = ApiCodec.decodeUptime(body)
        assertEquals("hour", u.bucket)
        assertEquals(1, u.buckets.size)
        assertEquals(0.5, u.buckets[0].fractionOn, 1e-9)
    }
}
