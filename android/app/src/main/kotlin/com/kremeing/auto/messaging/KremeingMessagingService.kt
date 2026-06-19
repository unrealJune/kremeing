package com.kremeing.auto.messaging

import android.util.Log
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.kremeing.auto.BuildConfig
import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.logic.FcmMessage
import com.kremeing.auto.prefs.SubscriptionPrefs
import java.util.concurrent.Executors

/**
 * Receives FCM messages from the backend. Hot-light pushes are sent as *data*
 * messages, so [onMessageReceived] fires in every app state and we render the
 * notification ourselves via [HotLightNotifier]. New registration tokens are
 * forwarded to the backend so the device keeps receiving alerts.
 */
class KremeingMessagingService : FirebaseMessagingService() {

    private val io = Executors.newSingleThreadExecutor()

    override fun onMessageReceived(message: RemoteMessage) {
        val parsed = FcmMessage.parse(message.data)
        if (parsed == null) {
            Log.w(TAG, "Dropping FCM message with unrecognized data payload")
            return
        }
        HotLightNotifier.show(applicationContext, parsed)
    }

    override fun onNewToken(token: String) {
        val prefs = SubscriptionPrefs(applicationContext)
        val location = prefs.lastLocation ?: return
        prefs.token = token
        io.execute {
            try {
                KremeingApiClient(BuildConfig.KREMEING_BASE_URL)
                    .subscribeDevice(token, location.first, location.second, prefs.radiusMiles)
            } catch (e: Exception) {
                Log.w(TAG, "Failed to refresh device subscription", e)
            }
        }
    }

    companion object {
        private const val TAG = "KremeingFcm"
    }
}
