package com.kremeing.auto

import android.Manifest
import android.annotation.SuppressLint
import android.content.pm.PackageManager
import android.location.LocationManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.google.firebase.messaging.FirebaseMessaging
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.messaging.HotLightNotifier
import com.kremeing.auto.prefs.SubscriptionPrefs
import java.util.concurrent.Executors

/**
 * Phone companion screen. Its job is small but real: request the permissions
 * the app needs (notifications + location), grab the device's FCM token, and
 * register a location-based push subscription with the backend so the user
 * gets hot-light alerts. The actual driving UX lives in the Car App service.
 */
class MainActivity : AppCompatActivity() {

    private val io = Executors.newSingleThreadExecutor()
    private val prefs by lazy { SubscriptionPrefs(this) }
    private lateinit var statusView: TextView

    private val permissionLauncher =
        registerForActivityResult(
            androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions(),
        ) { subscribe() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        HotLightNotifier.ensureChannel(this)

        statusView = TextView(this).apply { textSize = 16f }
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 48, 48, 48)
            addView(TextView(this@MainActivity).apply {
                text = getString(R.string.app_name)
                textSize = 22f
            })
            addView(statusView)
        }
        setContentView(root)

        requestPermissionsThenSubscribe()
    }

    private fun requestPermissionsThenSubscribe() {
        val needed = buildList {
            add(Manifest.permission.ACCESS_COARSE_LOCATION)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
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

        FirebaseMessaging.getInstance().token.addOnCompleteListener { task ->
            if (!task.isSuccessful) {
                setStatus("Couldn't get a push token")
                return@addOnCompleteListener
            }
            val token = task.result
            prefs.token = token
            val (lat, lng) = prefs.lastLocation ?: DEFAULT_LOCATION
            io.execute {
                try {
                    KremeingApiClient(BuildConfig.KREMEING_BASE_URL)
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
    }
}
