package com.kremeing.auto.messaging

import android.app.NotificationManager
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.google.firebase.messaging.RemoteMessage
import com.kremeing.auto.logic.FcmMessage
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/**
 * Layer 1 (Robolectric) test of the FCM entry point: a well-formed data push
 * is turned into a notification, while a malformed one is dropped silently
 * (so a corrupt server payload can never crash the messaging service).
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class KremeingMessagingServiceTest {

    private val context: Context get() = ApplicationProvider.getApplicationContext()

    private fun service(): KremeingMessagingService =
        Robolectric.buildService(KremeingMessagingService::class.java).create().get()

    private fun message(data: Map<String, String>): RemoteMessage =
        RemoteMessage.Builder("kremeing@fcm").setData(data).build()

    private val validPayload = mapOf(
        FcmMessage.Keys.TITLE to "Hot light is on!",
        FcmMessage.Keys.BODY to "Fresh doughnuts now.",
        FcmMessage.Keys.STORE_ID to "42",
        FcmMessage.Keys.STORE_NAME to "Krispy Kreme #42",
        FcmMessage.Keys.LATITUDE to "47.6062",
        FcmMessage.Keys.LONGITUDE to "-122.3321",
    )

    @Test
    fun `valid data push posts a notification`() {
        service().onMessageReceived(message(validPayload))

        val shadow = shadowOf(context.getSystemService(NotificationManager::class.java))
        assertEquals(1, shadow.size())
        assertEquals("Hot light is on!", shadowOf(shadow.allNotifications.first()).contentTitle.toString())
    }

    @Test
    fun `malformed data push is dropped`() {
        // Missing storeId — FcmMessage.parse returns null and nothing is shown.
        service().onMessageReceived(message(validPayload - FcmMessage.Keys.STORE_ID))

        val shadow = shadowOf(context.getSystemService(NotificationManager::class.java))
        assertEquals(0, shadow.size())
    }
}
