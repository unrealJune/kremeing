package com.kremeing.auto.car.screens

import android.content.Intent
import android.net.Uri
import androidx.car.app.CarContext
import androidx.car.app.Screen
import androidx.car.app.model.Action
import androidx.car.app.model.CarIcon
import androidx.car.app.model.Pane
import androidx.car.app.model.PaneTemplate
import androidx.car.app.model.Row
import androidx.car.app.model.Template
import androidx.core.graphics.drawable.IconCompat
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.logic.CardFormatter
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.Uptime
import com.kremeing.auto.ui.HeatBarBitmap
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * Android Auto store detail. Car screens are glanced at while driving, so the
 * prediction is surfaced as **big text** first — the current status and the
 * typical "usually hot" time windows derived from history — with the visual
 * heat bar shown as the large [Pane] side image (480dp box) alongside. A
 * Navigate action hands off to the active nav app. Reached by tapping a row on
 * [HotLightScreen].
 *
 * The [client] and [executor] are injectable so the screen can be driven with a
 * network-free fake in tests.
 */
class StoreDetailScreen @JvmOverloads constructor(
    carContext: CarContext,
    private val store: NearbyStore,
    private val client: KremeingApiClient = KremeingApiClient(BuildConfig.KREMEING_BASE_URL),
    private val executor: Executor = Executors.newSingleThreadExecutor(),
) : Screen(carContext) {

    @Volatile private var barIcon: CarIcon? = null
    // The primary (longest) "usually hot" window, formatted for the big row title.
    @Volatile private var primaryWindow: String? = null
    // Any remaining windows, joined (e.g. "also 5:00–7:30 PM").
    @Volatile private var extraWindows: String? = null
    @Volatile private var basisText: String? = null
    @Volatile private var hasPattern: Boolean = false
    @Volatile private var errorText: String? = null
    @Volatile private var loading: Boolean = true

    private val hourMinFormat = java.text.SimpleDateFormat("h:mm", Locale.getDefault())
    private val meridiemFormat = java.text.SimpleDateFormat("a", Locale.getDefault())

    init {
        load()
    }

    private fun load() {
        executor.execute {
            try {
                // Backend stores timestamps in UTC and rejects non-UTC offsets,
                // so format since/until in UTC ("…Z"). Explicit until (= now)
                // keeps the span at HISTORY_DAYS, under the backend's 90-day cap.
                val now = OffsetDateTime.now(ZoneOffset.UTC)
                val since = now.minusDays(HISTORY_DAYS).format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val until = now.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val history = client.history(store.id, sinceIso = since, untilIso = until)
                val flips = Uptime.parseFlips(history.flips)
                val zone = ZoneId.systemDefault()
                val bar = Uptime.todayHeatBar(flips, System.currentTimeMillis(), zone)

                val probs = bar.probabilities
                val windows = if (probs != null) {
                    Uptime.usualOnWindows(probs).take(MAX_WINDOWS).map { w ->
                        val start = Uptime.slotStartMs(bar.dayStartMs, bar.dayEndMs, w.startSlot)
                        val end = Uptime.slotStartMs(bar.dayStartMs, bar.dayEndMs, w.endSlot)
                        formatRange(start, end, zone)
                    }
                } else {
                    emptyList()
                }
                hasPattern = windows.isNotEmpty()
                primaryWindow = windows.firstOrNull()
                extraWindows = windows.drop(1).takeIf { it.isNotEmpty() }
                    ?.joinToString(", ", prefix = "also ")
                basisText = if (hasPattern) bar.basisLabel?.let { "based on $it" } else null
                errorText = null

                val bitmap = HeatBarBitmap.render(
                    bar.todayIntervals,
                    bar.probabilities,
                    bar.dayStartMs,
                    bar.dayEndMs,
                    bar.nowMs,
                    BAR_WIDTH_PX,
                    BAR_HEIGHT_PX,
                )
                barIcon = CarIcon.Builder(IconCompat.createWithBitmap(bitmap)).build()
            } catch (e: Exception) {
                barIcon = null
                hasPattern = false
                primaryWindow = null
                errorText = "Couldn't load history"
            } finally {
                loading = false
                invalidate()
            }
        }
    }

    override fun onGetTemplate(): Template {
        val pane = Pane.Builder()

        // Row 1 — current status, big and first.
        pane.addRow(
            Row.Builder()
                .setTitle(statusHeadline())
                .addText(CardFormatter.subtitle(store))
                .build(),
        )

        // Row 2 — the typical "usually hot" windows, the primary one in the big title.
        val usualRow = Row.Builder()
            .setTitle(primaryWindow?.let { "Usually hot $it" } ?: "Usually hot")
        when {
            loading -> usualRow.addText("Loading hot-light history…")
            errorText != null -> usualRow.addText(errorText!!)
            hasPattern -> {
                extraWindows?.let { usualRow.addText(it) }
                basisText?.let { usualRow.addText(it) }
            }
            else -> usualRow.addText("No usual pattern yet")
        }
        pane.addRow(usualRow.build())

        // The visual bar as the large (480dp) side image, when available.
        barIcon?.let { pane.setImage(it) }

        pane.addAction(
            Action.Builder()
                .setTitle("Navigate")
                .setOnClickListener { navigate() }
                .build(),
        )

        return PaneTemplate.Builder(pane.build())
            .setTitle(CardFormatter.title(store))
            .setHeaderAction(Action.BACK)
            .build()
    }

    private fun navigate() {
        val intent = Intent(CarContext.ACTION_NAVIGATE, Uri.parse(NavigationIntent.geoUri(store)))
        carContext.startCarApp(intent)
    }

    private fun statusHeadline(): String = when (store.status) {
        HotLightStatus.ON -> "🔥 HOT NOW"
        HotLightStatus.OFF -> "Light off"
        HotLightStatus.UNKNOWN -> "Status unknown"
    }

    /** Compact range like "7:00–9:30 AM" (shared meridiem) or "11:00 AM–1:30 PM". */
    private fun formatRange(startMs: Long, endMs: Long, zone: ZoneId): String {
        val tz = java.util.TimeZone.getTimeZone(zone)
        hourMinFormat.timeZone = tz
        meridiemFormat.timeZone = tz
        val startHm = hourMinFormat.format(Date(startMs))
        val endHm = hourMinFormat.format(Date(endMs))
        val startMer = meridiemFormat.format(Date(startMs))
        val endMer = meridiemFormat.format(Date(endMs))
        return if (startMer == endMer) "$startHm–$endHm $endMer" else "$startHm $startMer–$endHm $endMer"
    }

    private companion object {
        const val HISTORY_DAYS = 89L
        const val MAX_WINDOWS = 3
        // Render the heat bar for Pane.setImage, which targets a 480x480dp box —
        // a big half-screen visual, far larger than a row image.
        const val BAR_WIDTH_PX = 1200
        const val BAR_HEIGHT_PX = 480
    }
}
