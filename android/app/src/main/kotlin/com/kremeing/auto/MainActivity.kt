package com.kremeing.auto

import android.Manifest
import android.annotation.SuppressLint
import android.content.pm.PackageManager
import android.location.LocationManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.google.firebase.messaging.FirebaseMessaging
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.messaging.HotLightNotifier
import com.kremeing.auto.prefs.SubscriptionPrefs
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * Phone companion screen. Its job is small but real: request the permissions
 * the app needs (notifications + location), grab the device's FCM token, and
 * register a location-based push subscription with the backend so the user
 * gets hot-light alerts. The actual driving UX lives in the Car App service.
 */
class MainActivity : AppCompatActivity() {

    private val prefs by lazy { SubscriptionPrefs(this) }
    private lateinit var statusView: TextView

    // Injectable seams so the Robolectric/Espresso suites can drive the
    // permission -> token -> subscribe flow without Firebase or the network.
    // Production code uses the defaults below.
    internal var executor: Executor = Executors.newSingleThreadExecutor()
    internal var apiClientFactory: (String) -> KremeingApiClient = { KremeingApiClient(it) }
    // Whether push/notifications are wired up. Defaults to the build flag so a
    // notificationless APK (-PkremeingPushEnabled=false) skips the token fetch
    // and subscription instead of failing with "Couldn't get a push token".
    internal var pushEnabled: Boolean = BuildConfig.PUSH_ENABLED
    internal var fetchToken: ((String?) -> Unit) -> Unit = { callback ->
        // Tolerate a missing google-services.json (the app compiles and runs
        // without it): if Firebase can't initialize, report no token rather
        // than crashing the activity.
        try {
            FirebaseMessaging.getInstance().token.addOnCompleteListener { task ->
                callback(if (task.isSuccessful) task.result else null)
            }
        } catch (e: Exception) {
            Log.w(TAG, "FCM token unavailable", e)
            callback(null)
        }
    }

    private val permissionLauncher =
        registerForActivityResult(
            androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions(),
        ) { subscribe() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (pushEnabled) HotLightNotifier.ensureChannel(this)

        statusView = TextView(this).apply { textSize = 16f }

        // Configurable card radius (miles) — the Android Auto card lists lit
        // stores within this straight-line distance. Persisted to prefs and read
        // live by HotLightScreen on each refresh.
        val radiusLabel = TextView(this).apply { textSize = 16f }
        val radiusSlider = SeekBar(this).apply {
            max = MAX_RADIUS_MILES - MIN_RADIUS_MILES
            progress = (prefs.radiusMiles.toInt() - MIN_RADIUS_MILES).coerceIn(0, max)
        }
        fun renderRadiusLabel(miles: Int) {
            radiusLabel.text = getString(R.string.radius_label, miles)
        }
        renderRadiusLabel(MIN_RADIUS_MILES + radiusSlider.progress)
        radiusSlider.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                val miles = MIN_RADIUS_MILES + progress
                renderRadiusLabel(miles)
                prefs.radiusMiles = miles.toDouble()
            }

            override fun onStartTrackingTouch(seekBar: SeekBar?) {}
            override fun onStopTrackingTouch(seekBar: SeekBar?) {}
        })

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 48, 48, 48)
            addView(TextView(this@MainActivity).apply {
                text = getString(R.string.app_name)
                textSize = 22f
            })
            addView(statusView)
            addView(radiusLabel)
            addView(radiusSlider)
        }
        setContentView(root)

        requestPermissionsThenSubscribe()
    }

    private fun requestPermissionsThenSubscribe() {
        val needed = buildList {
            add(Manifest.permission.ACCESS_COARSE_LOCATION)
            if (pushEnabled && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isEmpty()) subscribe() else permissionLauncher.launch(needed.toTypedArray())
    }

    private fun subscribe() {
        val location = lastKnownLocation()
        if (location != null) prefs.lastLocation = location

        // Notificationless build: don't try to fetch an FCM token or subscribe.
        // The Android Auto UX (which polls the backend) keeps working.
        if (!pushEnabled) {
            setStatus("Push notifications are disabled in this build.")
            return
        }

        fetchToken { token ->
            if (token == null) {
                setStatus("Couldn't get a push token")
                return@fetchToken
            }
            prefs.token = token
            val (lat, lng) = prefs.lastLocation ?: DEFAULT_LOCATION
            executor.execute {
                try {
                    apiClientFactory(BuildConfig.KREMEING_BASE_URL)
                        .subscribeDevice(token, lat, lng, prefs.radiusMiles)
                    runOnUiThread {
                        setStatus("Subscribed — you'll be alerted when a nearby hot light turns on.")
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "subscribe failed", e)
                    runOnUiThread {
                        setStatus("Subscription failed: ${e.message}")
                        Toast.makeText(this, "Subscription failed", Toast.LENGTH_SHORT).show()
                    }
                }
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun lastKnownLocation(): Pair<Double, Double>? {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_COARSE_LOCATION)
            != PackageManager.PERMISSION_GRANTED
        ) {
            return null
        }
        val lm = getSystemService(LOCATION_SERVICE) as? LocationManager ?: return null
        val providers = listOf(LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER)
        for (provider in providers) {
            val loc = try {
                lm.getLastKnownLocation(provider)
            } catch (_: SecurityException) {
                null
            }
            if (loc != null) return loc.latitude to loc.longitude
        }
        return null
    }

    private fun setStatus(text: String) {
        statusView.text = text
    }

    private companion object {
        private const val TAG = "KremeingMain"
        val DEFAULT_LOCATION = 47.6062 to -122.3321

        // Card radius slider bounds (miles).
        const val MIN_RADIUS_MILES = 1
        const val MAX_RADIUS_MILES = 50
    }
}
