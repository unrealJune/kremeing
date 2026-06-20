package com.kremeing.auto

import android.Manifest
import android.app.Application
import android.app.NotificationManager
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import com.kremeing.auto.testing.FakeKremeingApiClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.util.concurrent.Executor

/**
 * Layer 1 (Robolectric) tests of the phone companion activity's
 * permission -> token -> subscribe flow. The Firebase token source, the HTTP
 * client and the executor are injected so the flow runs without Firebase or
 * the network.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class MainActivityTest {

    private val app: Application get() = ApplicationProvider.getApplicationContext()

    private fun grantPermissions() {
        shadowOf(app).grantPermissions(
            Manifest.permission.ACCESS_COARSE_LOCATION,
            Manifest.permission.POST_NOTIFICATIONS,
        )
    }

    @Test
    fun `a granted token registers a device subscription with the backend`() {
        grantPermissions()
        val fake = FakeKremeingApiClient()
        val controller = Robolectric.buildActivity(MainActivity::class.java)
        controller.get().apply {
            executor = Executor { it.run() }
            apiClientFactory = { fake }
            fetchToken = { callback -> callback("fake-token-123") }
        }

        controller.setup()
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(1, fake.subscribeCalls.size)
        val call = fake.subscribeCalls.first()
        assertEquals("fake-token-123", call.token)
        // No location fix in the test -> the activity falls back to its default.
        assertEquals(47.6062, call.latitude, 1e-9)
        assertEquals(-122.3321, call.longitude, 1e-9)
        assertEquals(25.0, call.radiusMiles, 1e-9)
    }

    @Test
    fun `a missing token does not subscribe and never crashes`() {
        grantPermissions()
        val fake = FakeKremeingApiClient()
        val controller = Robolectric.buildActivity(MainActivity::class.java)
        controller.get().apply {
            executor = Executor { it.run() }
            apiClientFactory = { fake }
            fetchToken = { callback -> callback(null) }
        }

        controller.setup()
        shadowOf(Looper.getMainLooper()).idle()

        assertTrue(fake.subscribeCalls.isEmpty())
        // The notification channel is created up-front so alerts can post later.
        val nm = app.getSystemService(NotificationManager::class.java)
        assertTrue(shadowOf(nm).notificationChannels.isNotEmpty())
    }

    @Test
    fun `a notificationless build skips the token and subscription`() {
        grantPermissions()
        val fake = FakeKremeingApiClient()
        var tokenFetched = false
        val controller = Robolectric.buildActivity(MainActivity::class.java)
        controller.get().apply {
            executor = Executor { it.run() }
            apiClientFactory = { fake }
            pushEnabled = false
            fetchToken = { callback ->
                tokenFetched = true
                callback("fake-token-123")
            }
        }

        controller.setup()
        shadowOf(Looper.getMainLooper()).idle()

        // Push is off: no token fetch, no subscription, no notification channel.
        assertTrue(!tokenFetched)
        assertTrue(fake.subscribeCalls.isEmpty())
        val nm = app.getSystemService(NotificationManager::class.java)
        assertTrue(shadowOf(nm).notificationChannels.isEmpty())
    }
}
