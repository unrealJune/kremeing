package com.kremeing.auto.car.screens

import android.net.Uri
import androidx.car.app.CarContext
import androidx.car.app.Screen
import androidx.car.app.model.Action
import androidx.car.app.model.ItemList
import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.model.Template
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.logic.CardFormatter
import com.kremeing.auto.logic.LitStoreFilter
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.prefs.SubscriptionPrefs
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * The Android Auto screen: a list of nearby stores whose hot light is on, each
 * rendered as a tappable card (title = store, subtitle = distance + address).
 * Tapping a card hands the destination to the active navigation app via a
 * `geo:` intent.
 *
 * All the "what shows and in what order" decisions live in `:logic`
 * ([LitStoreFilter], [CardFormatter], [NavigationIntent]) and are unit-tested
 * there; this class only binds those results into Car App templates.
 *
 * The [client], [prefs] and [executor] are injectable so the `:app` Robolectric
 * suite can drive the screen with a network-free fake client and a synchronous
 * executor (see HotLightScreenTest). Production code uses the defaults.
 */
class HotLightScreen @JvmOverloads constructor(
    carContext: CarContext,
    private val client: KremeingApiClient = KremeingApiClient(BuildConfig.KREMEING_BASE_URL),
    private val prefs: SubscriptionPrefs = SubscriptionPrefs(carContext),
    private val executor: Executor = Executors.newSingleThreadExecutor(),
) : Screen(carContext) {

    @Volatile private var litStores: List<NearbyStore> = emptyList()
    @Volatile private var loading: Boolean = true
    @Volatile private var errorText: String? = null

    init {
        refresh()
    }

    internal fun refresh() {
        loading = true
        errorText = null
        invalidate()
        val (lat, lng) = prefs.lastLocation ?: DEFAULT_LOCATION
        executor.execute {
            try {
                val stores = client.nearbyStores(lat, lng, prefs.radiusMiles)
                litStores = LitStoreFilter.litNearby(stores)
                errorText = null
            } catch (e: Exception) {
                litStores = emptyList()
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
            loading -> listBuilder.setNoItemsMessage("Looking for hot lights nearby…")
            errorText != null -> listBuilder.setNoItemsMessage(errorText!!)
            litStores.isEmpty() -> listBuilder.setNoItemsMessage("No hot lights on right now")
            else -> litStores.forEach { store -> listBuilder.addItem(buildRow(store)) }
        }

        return ListTemplate.Builder()
            .setTitle("Hot Lights Near You")
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

    private fun buildRow(store: NearbyStore): Row =
        Row.Builder()
            .setTitle(CardFormatter.title(store))
            .addText(CardFormatter.subtitle(store))
            .setOnClickListener { navigateTo(store) }
            .setBrowsable(true)
            .build()

    internal fun navigateTo(store: NearbyStore) {
        val uri = Uri.parse(NavigationIntent.geoUri(store))
        val intent = android.content.Intent(CarContext.ACTION_NAVIGATE, uri)
        carContext.startCarApp(intent)
    }

    private companion object {
        // Fallback when the user hasn't set a location yet (downtown Seattle).
        val DEFAULT_LOCATION = 47.6062 to -122.3321
    }
}
