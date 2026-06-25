package com.kremeing.auto.car.screens

import android.content.Intent
import android.net.Uri
import androidx.car.app.AppManager
import androidx.car.app.CarContext
import androidx.car.app.Screen
import androidx.car.app.model.Action
import androidx.car.app.model.Pane
import androidx.car.app.model.PaneTemplate
import androidx.car.app.model.Row
import androidx.car.app.model.Template
import androidx.car.app.navigation.model.MapWithContentTemplate
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.car.CarSurfaceRenderer
import com.kremeing.auto.logic.CardFormatter
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.Uptime
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * Android Auto store detail rendered on the custom **Surface** (Canvas) instead
 * of being limited to template rows, so the hot-light info can be drawn far
 * bigger and more present than a Pane allows: a large title, a glanceable
 * status, a giant heat bar and the "usually hot" time windows (see
 * [CarSurfaceRenderer]).
 *
 * Uses a [MapWithContentTemplate] (car API 7) as the surface host: the map area
 * is our drawn dashboard, and a small content [Pane] carries the Navigate
 * action. The app declares the **weather** category and the `MAP_TEMPLATES` /
 * `ACCESS_SURFACE` permissions — so it draws its own canvas **without** claiming
 * the navigation surface or being a navigation app.
 *
 * The [client] and [executor] are injectable for network-free tests.
 */
class StoreSurfaceScreen @JvmOverloads constructor(
    carContext: CarContext,
    private val store: NearbyStore,
    private val client: KremeingApiClient = KremeingApiClient(BuildConfig.KREMEING_BASE_URL),
    private val executor: Executor = Executors.newSingleThreadExecutor(),
) : Screen(carContext), DefaultLifecycleObserver {

    private val renderer = CarSurfaceRenderer()
    private val hourFormat = java.text.SimpleDateFormat("h:mm a", Locale.getDefault())

    init {
        lifecycle.addObserver(this)
        // Paint something immediately (title + status + empty bar) so the surface
        // is visibly alive while the history request is in flight.
        renderer.setDashboard(loadingDashboard())
        load()
    }

    /** The host only provides a surface while this screen is on top; bind on resume. */
    override fun onResume(owner: LifecycleOwner) {
        carContext.getCarService(AppManager::class.java).setSurfaceCallback(renderer)
    }

    override fun onPause(owner: LifecycleOwner) {
        carContext.getCarService(AppManager::class.java).setSurfaceCallback(null)
    }

    override fun onGetTemplate(): Template {
        // The big visuals (title, status, heat bar, usually-hot) are drawn on the
        // surface; the content Pane is the side panel that carries the actions.
        val pane = Pane.Builder()
            .addRow(
                Row.Builder()
                    .setTitle(CardFormatter.title(store))
                    .addText(CardFormatter.subtitle(store))
                    .build(),
            )
            .addAction(
                Action.Builder()
                    .setTitle("Navigate")
                    .setFlags(Action.FLAG_PRIMARY)
                    .setOnClickListener { navigate() }
                    .build(),
            )
            .build()

        val content = PaneTemplate.Builder(pane)
            .setHeaderAction(Action.BACK)
            .build()

        return MapWithContentTemplate.Builder()
            .setContentTemplate(content)
            .build()
    }

    private fun load() {
        executor.execute {
            try {
                // Backend stores timestamps in UTC and rejects non-UTC offsets, so
                // format since/until in UTC ("…Z"); explicit until keeps the span
                // at HISTORY_DAYS, under the backend's 90-day cap.
                val now = OffsetDateTime.now(ZoneOffset.UTC)
                val since = now.minusDays(HISTORY_DAYS).format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val until = now.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val history = client.history(store.id, sinceIso = since, untilIso = until)
                val flips = Uptime.parseFlips(history.flips)
                val zone = ZoneId.systemDefault()
                val bar = Uptime.todayHeatBar(flips, System.currentTimeMillis(), zone)

                val probs = bar.probabilities
                val usualLine: String
                val basisLine: String?
                if (probs != null) {
                    val windows = Uptime.usualOnWindows(probs).take(MAX_WINDOWS).map { w ->
                        val s = Uptime.slotStartMs(bar.dayStartMs, bar.dayEndMs, w.startSlot)
                        val e = Uptime.slotStartMs(bar.dayStartMs, bar.dayEndMs, w.endSlot)
                        "${formatHour(s, zone)}–${formatHour(e, zone)}"
                    }
                    usualLine = if (windows.isEmpty()) "No usual pattern yet"
                    else "Usually hot " + windows.joinToString(", ")
                    basisLine = if (windows.isEmpty()) null else bar.basisLabel?.let { "based on $it" }
                } else {
                    usualLine = "Not enough history yet"
                    basisLine = null
                }

                renderer.setDashboard(
                    CarSurfaceRenderer.Dashboard(
                        title = CardFormatter.title(store),
                        status = store.status,
                        statusHeadline = statusHeadline(),
                        statusSub = CardFormatter.subtitle(store),
                        usualLine = usualLine,
                        basisLine = basisLine,
                        intervals = bar.todayIntervals,
                        probabilities = bar.probabilities,
                        dayStartMs = bar.dayStartMs,
                        dayEndMs = bar.dayEndMs,
                        nowMs = bar.nowMs,
                    ),
                )
            } catch (e: Exception) {
                renderer.setDashboard(loadingDashboard(usualLine = "Couldn't load history"))
            }
        }
    }

    private fun navigate() {
        val intent = Intent(CarContext.ACTION_NAVIGATE, Uri.parse(NavigationIntent.geoUri(store)))
        carContext.startCarApp(intent)
    }

    private fun statusHeadline(): String = when (store.status) {
        HotLightStatus.ON -> "\uD83D\uDD25 HOT NOW"
        HotLightStatus.OFF -> "LIGHT OFF"
        HotLightStatus.UNKNOWN -> "STATUS UNKNOWN"
    }

    /** Placeholder shown before (or instead of) a successful history load. */
    private fun loadingDashboard(usualLine: String = "Loading hot-light history…"): CarSurfaceRenderer.Dashboard {
        val zone = ZoneId.systemDefault()
        val nowMs = System.currentTimeMillis()
        val startOfDay = Instant.ofEpochMilli(nowMs).atZone(zone).toLocalDate()
            .atStartOfDay(zone).toInstant().toEpochMilli()
        val endOfDay = Instant.ofEpochMilli(nowMs).atZone(zone).toLocalDate().plusDays(1)
            .atStartOfDay(zone).toInstant().toEpochMilli()
        return CarSurfaceRenderer.Dashboard(
            title = CardFormatter.title(store),
            status = store.status,
            statusHeadline = statusHeadline(),
            statusSub = CardFormatter.subtitle(store),
            usualLine = usualLine,
            basisLine = null,
            intervals = emptyList(),
            probabilities = null,
            dayStartMs = startOfDay,
            dayEndMs = endOfDay,
            nowMs = nowMs,
        )
    }

    private fun formatHour(ms: Long, zone: ZoneId): String {
        hourFormat.timeZone = java.util.TimeZone.getTimeZone(zone)
        return hourFormat.format(Date(ms))
    }

    private companion object {
        const val HISTORY_DAYS = 89L
        const val MAX_WINDOWS = 3
    }
}
