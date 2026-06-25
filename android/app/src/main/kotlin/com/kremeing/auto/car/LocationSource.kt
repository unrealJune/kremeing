package com.kremeing.auto.car

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.Priority
import com.google.android.gms.tasks.CancellationTokenSource
import com.google.android.gms.tasks.Tasks
import java.util.concurrent.TimeUnit

/**
 * Supplies the driver's current `(latitude, longitude)`, or `null` when it
 * can't be determined (permission missing, no fix yet). Blocking — call it off
 * the main thread (the car screen already refreshes on a background executor).
 *
 * A `fun interface` so the car-screen test suite can inject a fixed coordinate
 * instead of standing up real location services.
 */
fun interface LocationSource {
    fun current(): Pair<Double, Double>?
}

/**
 * Production [LocationSource] backed by [com.google.android.gms.location.FusedLocationProviderClient].
 * Asks for a fresh balanced-power fix so the nearby list tracks the driver while
 * moving, falling back to the last cached fix; returns `null` on any failure
 * (including a missing location permission) so the caller can degrade
 * gracefully to a stored/default location.
 */
class FusedLocationSource(context: Context) : LocationSource {

    private val appContext = context.applicationContext
    private val client = LocationServices.getFusedLocationProviderClient(appContext)

    @SuppressLint("MissingPermission")
    override fun current(): Pair<Double, Double>? {
        if (!hasLocationPermission()) return null
        return try {
            val cts = CancellationTokenSource()
            val fresh = Tasks.await(
                client.getCurrentLocation(Priority.PRIORITY_BALANCED_POWER_ACCURACY, cts.token),
                FIX_TIMEOUT_SECONDS,
                TimeUnit.SECONDS,
            )
            val loc = fresh ?: Tasks.await(client.lastLocation, FIX_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            loc?.let { it.latitude to it.longitude }
        } catch (_: Exception) {
            null
        }
    }

    private fun hasLocationPermission(): Boolean {
        val fine = ContextCompat.checkSelfPermission(appContext, Manifest.permission.ACCESS_FINE_LOCATION)
        val coarse = ContextCompat.checkSelfPermission(appContext, Manifest.permission.ACCESS_COARSE_LOCATION)
        return fine == PackageManager.PERMISSION_GRANTED || coarse == PackageManager.PERMISSION_GRANTED
    }

    private companion object {
        const val FIX_TIMEOUT_SECONDS = 5L
    }
}
