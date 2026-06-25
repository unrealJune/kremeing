package com.kremeing.auto.map

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.Manifest
import android.content.pm.PackageManager
import android.graphics.drawable.BitmapDrawable
import android.os.Bundle
import android.util.Log
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updateLayoutParams
import androidx.core.widget.NestedScrollView
import com.google.android.material.bottomsheet.BottomSheetBehavior
import com.google.android.material.button.MaterialButton
import com.google.android.material.floatingactionbutton.FloatingActionButton
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.MainActivity
import com.kremeing.auto.R
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.car.FusedLocationSource
import com.kremeing.auto.car.LocationSource
import com.kremeing.auto.logic.CardFormatter
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.Uptime
import com.kremeing.auto.prefs.SubscriptionPrefs
import com.kremeing.auto.ui.DayGridView
import com.kremeing.auto.ui.HeatBarView
import org.osmdroid.config.Configuration
import org.osmdroid.tileprovider.tilesource.XYTileSource
import org.osmdroid.util.GeoPoint
import org.osmdroid.views.CustomZoomButtonsController
import org.osmdroid.views.MapView
import org.osmdroid.views.overlay.Marker
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * The phone home screen: a native OpenStreetMap map (osmdroid, CARTO "light"
 * tiles) of nearby stores with status-coloured pins — the native counterpart of
 * the web app. Tapping a pin opens a bottom sheet with the store's detail and
 * the interactive heat bar ([HeatBarView]); the top bar searches by ZIP/city.
 *
 * [apiClient], [locationSource] and [executor] are injectable for tests.
 */
class MapActivity : AppCompatActivity() {

    private val prefs by lazy { SubscriptionPrefs(this) }

    private lateinit var mapView: MapView
    private lateinit var sheetBehavior: BottomSheetBehavior<NestedScrollView>
    private lateinit var heatBar: HeatBarView
    private lateinit var heatGrid: DayGridView
    private lateinit var heatLabel: TextView
    private lateinit var storeName: TextView
    private lateinit var storeMeta: TextView
    private lateinit var storeStatus: TextView
    private lateinit var heatReadout: TextView
    private lateinit var searchProgress: ProgressBar

    internal var executor: Executor = Executors.newSingleThreadExecutor()
    internal var apiClient: KremeingApiClient = KremeingApiClient(BuildConfig.KREMEING_BASE_URL)
    internal var locationSource: LocationSource? = null

    @Volatile private var userLocation: Pair<Double, Double>? = null
    @Volatile private var selectedStore: NearbyStore? = null

    private val permissionLauncher =
        registerForActivityResult(
            ActivityResultContracts.RequestMultiplePermissions(),
        ) { centerOnUserAndLoad() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Immersive: draw the map edge-to-edge behind the system bars.
        WindowCompat.setDecorFitsSystemWindows(window, false)

        Configuration.getInstance().apply {
            load(this@MapActivity, getSharedPreferences("osmdroid", Context.MODE_PRIVATE))
            userAgentValue = packageName
        }
        if (locationSource == null) locationSource = FusedLocationSource(this)

        setContentView(R.layout.activity_map)

        mapView = findViewById<MapView>(R.id.map).apply {
            setTileSource(CARTO_LIGHT)
            setMultiTouchControls(true)   // smooth pinch-to-zoom
            setTilesScaledToDpi(true)
            isHorizontalMapRepetitionEnabled = false
            isVerticalMapRepetitionEnabled = false
            // Hide the built-in +/- buttons for an uncluttered, immersive map;
            // pinch-to-zoom is the primary interaction.
            zoomController.setVisibility(CustomZoomButtonsController.Visibility.NEVER)
            minZoomLevel = 4.0
            maxZoomLevel = 19.0
        }

        applyImmersiveInsets()

        val sheet = findViewById<NestedScrollView>(R.id.sheet)
        sheetBehavior = BottomSheetBehavior.from(sheet).apply { state = BottomSheetBehavior.STATE_HIDDEN }
        heatBar = findViewById(R.id.heatBar)
        heatGrid = findViewById(R.id.heatGrid)
        heatLabel = findViewById(R.id.heatLabel)
        storeName = findViewById(R.id.storeName)
        storeMeta = findViewById(R.id.storeMeta)
        storeStatus = findViewById(R.id.storeStatus)
        heatReadout = findViewById(R.id.heatReadout)
        searchProgress = findViewById(R.id.searchProgress)

        findViewById<FloatingActionButton>(R.id.fabNearMe).setOnClickListener { centerOnUserAndLoad() }
        findViewById<FloatingActionButton>(R.id.fabSettings).setOnClickListener {
            startActivity(Intent(this, MainActivity::class.java))
        }

        heatBar.onScrub = { readout -> heatReadout.text = readout.orEmpty() }
        findViewById<MaterialButton>(R.id.navigateButton).setOnClickListener {
            selectedStore?.let { store ->
                startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(NavigationIntent.geoUri(store))))
            }
        }
        findViewById<EditText>(R.id.searchInput).setOnEditorActionListener { input, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                val query = input.text.toString().trim()
                if (query.isNotEmpty()) search(query)
                true
            } else {
                false
            }
        }

        val (lat, lng) = prefs.lastLocation ?: DEFAULT_LOCATION
        mapView.controller.setZoom(DEFAULT_ZOOM)
        mapView.controller.setCenter(GeoPoint(lat, lng))

        requestLocationThenLoad()
    }

    override fun onResume() {
        super.onResume()
        mapView.onResume()
    }

    override fun onPause() {
        super.onPause()
        mapView.onPause()
    }

    /**
     * Edge-to-edge inset handling: keep the floating search bar below the status
     * bar and the FABs / sheet content above the navigation bar, while the map
     * itself stays full-screen behind both.
     */
    private fun applyImmersiveInsets() {
        val searchCard = findViewById<View>(R.id.searchCard)
        val fabNearMe = findViewById<View>(R.id.fabNearMe)
        val fabSettings = findViewById<View>(R.id.fabSettings)
        val sheetContent = findViewById<View>(R.id.sheetContent)

        val baseTop = dp(12)
        val baseNear = dp(20)
        val baseSettings = dp(84)
        val baseSheetPad = dp(20)

        ViewCompat.setOnApplyWindowInsetsListener(searchCard) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.updateLayoutParams<ViewGroup.MarginLayoutParams> { topMargin = baseTop + bars.top }
            insets
        }
        ViewCompat.setOnApplyWindowInsetsListener(fabNearMe) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.updateLayoutParams<ViewGroup.MarginLayoutParams> { bottomMargin = baseNear + bars.bottom }
            insets
        }
        ViewCompat.setOnApplyWindowInsetsListener(fabSettings) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.updateLayoutParams<ViewGroup.MarginLayoutParams> { bottomMargin = baseSettings + bars.bottom }
            insets
        }
        ViewCompat.setOnApplyWindowInsetsListener(sheetContent) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.setPadding(v.paddingLeft, v.paddingTop, v.paddingRight, baseSheetPad + bars.bottom)
            insets
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun requestLocationThenLoad() {
        val granted = ContextCompat.checkSelfPermission(
            this,
            Manifest.permission.ACCESS_COARSE_LOCATION,
        ) == PackageManager.PERMISSION_GRANTED
        if (granted) {
            centerOnUserAndLoad()
        } else {
            permissionLauncher.launch(
                arrayOf(
                    Manifest.permission.ACCESS_FINE_LOCATION,
                    Manifest.permission.ACCESS_COARSE_LOCATION,
                ),
            )
        }
    }

    private fun centerOnUserAndLoad() {
        executor.execute {
            val loc = locationSource?.current() ?: prefs.lastLocation ?: DEFAULT_LOCATION
            userLocation = loc
            prefs.lastLocation = loc
            runOnUiThread {
                mapView.controller.setZoom(DEFAULT_ZOOM)
                mapView.controller.animateTo(GeoPoint(loc.first, loc.second))
            }
            loadStores(loc.first, loc.second)
        }
    }

    private fun loadStores(lat: Double, lng: Double) {
        try {
            val stores = apiClient.nearbyStores(lat, lng, limit = NEARBY_LIMIT)
            runOnUiThread { renderPins(stores) }
        } catch (e: Exception) {
            Log.w(TAG, "nearby load failed", e)
            runOnUiThread {
                Toast.makeText(this, getString(R.string.map_load_failed), Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun search(query: String) {
        searchProgress.visibility = View.VISIBLE
        executor.execute {
            try {
                val stores = apiClient.searchStores(query)
                runOnUiThread {
                    searchProgress.visibility = View.GONE
                    if (stores.isEmpty()) {
                        Toast.makeText(
                            this,
                            getString(R.string.search_no_results, query),
                            Toast.LENGTH_SHORT,
                        ).show()
                    } else {
                        renderPins(stores)
                        val first = stores.first()
                        mapView.controller.animateTo(GeoPoint(first.latitude, first.longitude))
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "search failed", e)
                runOnUiThread {
                    searchProgress.visibility = View.GONE
                    Toast.makeText(this, getString(R.string.map_load_failed), Toast.LENGTH_SHORT).show()
                }
            }
        }
    }

    private fun renderPins(stores: List<NearbyStore>) {
        val density = resources.displayMetrics.density
        mapView.overlays.clear()

        userLocation?.let { (lat, lng) ->
            mapView.overlays.add(
                Marker(mapView).apply {
                    position = GeoPoint(lat, lng)
                    title = getString(R.string.menu_near_me)
                    icon = BitmapDrawable(resources, StorePins.userBitmap(density))
                    setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER)
                },
            )
        }

        stores.forEach { store ->
            mapView.overlays.add(
                Marker(mapView).apply {
                    position = GeoPoint(store.latitude, store.longitude)
                    title = shortName(store.name)
                    icon = BitmapDrawable(resources, StorePins.bitmap(store.status, density))
                    setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER)
                    setOnMarkerClickListener { _, _ ->
                        openDetail(store)
                        true
                    }
                },
            )
        }
        mapView.invalidate()
    }

    /** Show the bottom sheet for [store] and load its heat-bar history. */
    private fun openDetail(store: NearbyStore) {
        selectedStore = store
        storeName.text = shortName(store.name)
        storeMeta.text = CardFormatter.subtitle(store)
        storeStatus.text = when (store.status) {
            HotLightStatus.ON -> getString(R.string.status_on)
            HotLightStatus.OFF -> getString(R.string.status_off)
            HotLightStatus.UNKNOWN -> getString(R.string.status_unknown)
        }
        heatReadout.text = ""
        heatLabel.text = getString(R.string.label_today)
        heatBar.setData(emptyList(), null, null, 0, 0, 0)
        heatGrid.setData(emptyList())
        sheetBehavior.state = BottomSheetBehavior.STATE_EXPANDED
        loadHistory(store)
    }

    private fun loadHistory(store: NearbyStore) {
        executor.execute {
            try {
                // The backend stores timestamps in UTC and (via Npgsql) rejects
                // non-UTC offsets, so format since/until in UTC ("…Z"), not local
                // time. The explicit until (= now) keeps the span at HISTORY_DAYS,
                // safely under the backend's 90-day cap.
                val now = OffsetDateTime.now(ZoneOffset.UTC)
                val since = now.minusDays(HISTORY_DAYS).format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val until = now.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                val history = apiClient.history(store.id, sinceIso = since, untilIso = until)
                val flips = Uptime.parseFlips(history.flips)
                val bar = Uptime.todayHeatBar(flips, System.currentTimeMillis(), ZoneId.systemDefault())
                runOnUiThread {
                    if (selectedStore?.id == store.id) {
                        heatBar.setData(
                            bar.todayIntervals,
                            bar.probabilities,
                            bar.basisLabel,
                            bar.dayStartMs,
                            bar.dayEndMs,
                            bar.nowMs,
                        )
                        heatGrid.setData(bar.daySegments)
                        val probs = bar.probabilities
                        heatLabel.text = if (probs != null && probs.isNotEmpty()) {
                            getString(R.string.label_today_predicted, bar.basisLabel ?: "all days")
                        } else {
                            getString(R.string.label_today)
                        }
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "history load failed", e)
                runOnUiThread {
                    if (selectedStore?.id == store.id) {
                        heatReadout.text = getString(R.string.detail_load_failed)
                    }
                }
            }
        }
    }

    private fun shortName(name: String): String =
        name.removePrefix("Krispy Kreme ").trim().ifEmpty { name }

    private companion object {
        private const val TAG = "KremeingMap"
        val DEFAULT_LOCATION = 47.6062 to -122.3321
        const val DEFAULT_ZOOM = 11.0
        const val NEARBY_LIMIT = 50
        const val HISTORY_DAYS = 89L

        /** CARTO "light" basemap — the clean, low-clutter style the web app uses. */
        val CARTO_LIGHT = XYTileSource(
            "CartoLight",
            0,
            20,
            256,
            ".png",
            arrayOf(
                "https://a.basemaps.cartocdn.com/light_all/",
                "https://b.basemaps.cartocdn.com/light_all/",
                "https://c.basemaps.cartocdn.com/light_all/",
                "https://d.basemaps.cartocdn.com/light_all/",
            ),
            "© OpenStreetMap contributors © CARTO",
        )
    }
}
