package com.kremeing.auto.messaging

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.kremeing.auto.logic.HotLightNotification
import com.kremeing.auto.logic.NavigationIntent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/**
 * Layer 1 (Robolectric) tests of the hot-light notification: it must land on a
 * high-importance channel and its tap action must carry the `geo:` navigation
 * URI computed in `:logic`.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class HotLightNotifierTest {

    private val context: Context get() = ApplicationProvider.getApplicationContext()

    private fun notification() = HotLightNotification(
        title = "Hot light is on!",
        body = "Krispy Kreme #7 is making fresh doughnuts right now.",
        storeId = 7,
        storeName = "Krispy Kreme #7",
        latitude = 47.6062,
        longitude = -122.3321,
    )

    @Test
    fun `posts a high-priority notification on the hot-light channel`() {
        val notif = notification()
        HotLightNotifier.show(context, notif)

        val nm = context.getSystemService(NotificationManager::class.java)
        val shadow = shadowOf(nm)

        assertEquals(1, shadow.size())
        val posted = shadow.getNotification(null, notif.storeId)
        assertNotNull(posted)
        assertEquals(notif.title, shadowOf(posted).contentTitle.toString())
        assertEquals(notif.body, shadowOf(posted).contentText.toString())

        val channel = shadow.getNotificationChannel(HotLightNotifier.CHANNEL_ID) as NotificationChannel
        assertEquals(NotificationManager.IMPORTANCE_HIGH, channel.importance)
    }

    @Test
    fun `notification tap action navigates to the store via a geo URI`() {
        val notif = notification()
        HotLightNotifier.show(context, notif)

        val posted = shadowOf(context.getSystemService(NotificationManager::class.java))
            .getNotification(null, notif.storeId)

        val expectedUri = NavigationIntent.geoUri(notif.latitude, notif.longitude, notif.storeName)
        assertEquals(expectedUri, shadowOf(posted.contentIntent).savedIntent.dataString)

        val actionTitles = posted.actions.orEmpty().map { it.title.toString() }
        assertTrue(actionTitles.contains("Navigate"))
    }
}
