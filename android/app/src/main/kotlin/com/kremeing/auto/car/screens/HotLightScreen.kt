package com.kremeing.auto.car.screens

import android.net.Uri
import androidx.car.app.CarContext
import androidx.car.app.Screen
import androidx.car.app.model.Action
import androidx.car.app.model.ItemList
import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.model.Template
import androidx.car.app.versioning.CarAppApiLevels
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.car.LocationSource
import com.kremeing.auto.car.FusedLocationSource
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.logic.CardFormatter
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.LitStoreFilter
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.prefs.SubscriptionPrefs
import java.util.concurrent.Executor
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/**
 * The Android Auto screen: a list of nearby stores (lit *and* off), lit ones
 * first then nearest, each rendered as a tappable card. Tapping a card opens the
 * store detail (status + heat bar); the radius pref is for notifications only,
 * not this list, so there's always something to browse even with no lights on.
 *
 * The ordering lives in `:logic` ([LitStoreFilter.nearbyAll]); this class only
 * binds the result into Car App templates.
 *
 * The [client], [prefs], [executor] and [locationSource] are injectable so the
 * `:app` Robolectric suite can drive the screen with a network-free fake client,
 * a synchronous executor and a fixed location (see HotLightScreenTest).
 * Production code uses the defaults.
 */
class HotLightScreen @JvmOverloads constructor(
    carContext: CarContext,
    private val client: KremeingApiClient = KremeingApiClient(BuildConfig.KREMEING_BASE_URL),
    private val prefs: SubscriptionPrefs = SubscriptionPrefs(carContext),
    private val executor: Executor = Executors.newSingleThreadExecutor(),
    private val locationSource: LocationSource = FusedLocationSource(carContext),
    private val autoRefreshSeconds: Long = DEFAULT_AUTO_REFRESH_SECONDS,
    private val scheduler: ScheduledExecutorService =
        Executors.newSingleThreadScheduledExecutor { r ->
            Thread(r, "kremeing-card-refresh").apply { isDaemon = true }
        },
) : Screen(carContext), DefaultLifecycleObserver {

    @Volatile private var stores: List<NearbyStore> = emptyList()
    @Volatile private var loading: Boolean = true
    @Volatile private var errorText: String? = null
    @Volatile private var refreshHandle: ScheduledFuture<*>? = null

    init {
        // Auto-refresh only while the screen is in the foreground in the car;
        // the observer starts/stops the periodic poll on resume/pause.
        lifecycle.addObserver(this)
        refresh()
    }

    /** Begin periodic refreshes once the card is visible to the driver. */
    override fun onResume(owner: LifecycleOwner) {
        if (refreshHandle != null || autoRefreshSeconds <= 0) return
        refreshHandle = scheduler.scheduleWithFixedDelay(
            { refresh() },
            autoRefreshSeconds,
            autoRefreshSeconds,
            TimeUnit.SECONDS,
        )
    }

    /** Stop polling when the card leaves the foreground. */
    override fun onPause(owner: LifecycleOwner) = stopAutoRefresh()

    override fun onDestroy(owner: LifecycleOwner) {
        stopAutoRefresh()
        scheduler.shutdownNow()
    }

    private fun stopAutoRefresh() {
        refreshHandle?.cancel(false)
        refreshHandle = null
    }

    internal fun refresh() {
        loading = true
        errorText = null
        invalidate()
        executor.execute {
            try {
                // Prefer a live fix so the list tracks the driver; persist it for
                // the (future) subscription flow, and fall back to the last known
                // or a default location when no fix is available.
                val (lat, lng) = locationSource.current()?.also { prefs.lastLocation = it }
                    ?: prefs.lastLocation
                    ?: DEFAULT_LOCATION
                val nearby = client.nearbyStores(lat, lng)
                // Show all nearby stores; stores within the notification radius
                // are pinned to the top (even if off), then the rest. The radius
                // pref governs ranking here, not filtering.
                stores = LitStoreFilter.nearbyRanked(nearby, prefs.radiusMiles)
                errorText = null
            } catch (e: Exception) {
                stores = emptyList()
                errorText = "Couldn't load nearby stores"
            } finally {
                loading = false
                invalidate()
            }
        }
    }

    override fun onGetTemplate(): Template {
        val listBuilder = ItemList.Builder()

        when {
            loading -> listBuilder.setNoItemsMessage("Looking for nearby stores…")
            errorText != null -> listBuilder.setNoItemsMessage(errorText!!)
            stores.isEmpty() -> listBuilder.setNoItemsMessage("No stores nearby")
            else -> stores.forEach { store -> listBuilder.addItem(buildRow(store)) }
        }

        return ListTemplate.Builder()
            .setTitle("Kremeing Near You")
            .setHeaderAction(Action.APP_ICON)
            .setActionStrip(
                androidx.car.app.model.ActionStrip.Builder()
                    .addAction(
                        Action.Builder()
                            .setTitle("Refresh")
                            .setOnClickListener { refresh() }
                            .build(),
                    )
                    .build(),
            )
            .setSingleList(listBuilder.build())
            .build()
    }

    private fun buildRow(store: NearbyStore): Row {
        val statusPrefix = when (store.status) {
            HotLightStatus.ON -> "🔥 ON · "
            HotLightStatus.OFF -> "Off · "
            HotLightStatus.UNKNOWN -> ""
        }
        return Row.Builder()
            .setTitle(CardFormatter.title(store))
            .addText(statusPrefix + CardFormatter.subtitle(store))
            .setOnClickListener { openDetail(store) }
            .setBrowsable(true)
            .build()
    }

    /**
     * Open the store detail. On hosts that support car API 7 (MapWithContentTemplate)
     * push the surface dashboard (big heat bar drawn on the canvas); on older
     * hosts fall back to the template [StoreDetailScreen] (Pane + 480dp image).
     */
    internal fun openDetail(store: NearbyStore) {
        val screen = if (carContext.carAppApiLevel >= CarAppApiLevels.LEVEL_7) {
            StoreSurfaceScreen(carContext, store, client, executor)
        } else {
            StoreDetailScreen(carContext, store, client, executor)
        }
        screenManager.push(screen)
    }

    internal fun navigateTo(store: NearbyStore) {
        val uri = Uri.parse(NavigationIntent.geoUri(store))
        val intent = android.content.Intent(CarContext.ACTION_NAVIGATE, uri)
        carContext.startCarApp(intent)
    }

    private companion object {
        // Fallback when the user hasn't set a location yet (downtown Seattle).
        val DEFAULT_LOCATION = 47.6062 to -122.3321

        // How often the card re-polls the backend while visible in the car, so
        // a store flipping its light on appears without the driver tapping
        // Refresh. 60s balances freshness against backend load.
        const val DEFAULT_AUTO_REFRESH_SECONDS = 60L
    }
}
