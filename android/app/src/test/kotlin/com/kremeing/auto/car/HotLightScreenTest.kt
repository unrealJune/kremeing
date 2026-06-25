package com.kremeing.auto.car

import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.testing.ScreenController
import androidx.car.app.testing.TestCarContext
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ApplicationProvider
import com.kremeing.auto.car.LocationSource
import com.kremeing.auto.car.screens.HotLightScreen
import com.kremeing.auto.logic.NavigationIntent
import com.kremeing.auto.prefs.SubscriptionPrefs
import com.kremeing.auto.testing.FakeKremeingApiClient
import com.kremeing.auto.testing.store
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.concurrent.Executor

/**
 * Layer 1 (Robolectric) tests of the Android Auto car screen. They drive
 * [HotLightScreen] with a network-free fake client and a synchronous executor,
 * then assert the rendered [ListTemplate] and the navigation intent — the parts
 * of the car UX that aren't already covered by the pure-`:logic` suite.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class HotLightScreenTest {

    /** Runs submitted work inline so refresh() completes before assertions. */
    private val direct = Executor { it.run() }

    /** Fixed location so the screen never touches real location services. */
    private val fixedLocation = LocationSource { 47.6 to -122.3 }

    private lateinit var carContext: TestCarContext
    private lateinit var prefs: SubscriptionPrefs

    @Before
    fun setUp() {
        carContext = TestCarContext.createCarContext(ApplicationProvider.getApplicationContext())
        prefs = SubscriptionPrefs(carContext)
    }

    private fun screen(client: FakeKremeingApiClient, executor: Executor = direct) =
        // autoRefreshSeconds = 0 disables the background poll so tests stay
        // deterministic and don't spin real timers.
        HotLightScreen(carContext, client, prefs, executor, fixedLocation, autoRefreshSeconds = 0L)

    private fun rows(screen: HotLightScreen): List<Row> {
        val template = screen.onGetTemplate() as ListTemplate
        return template.singleList!!.items.filterIsInstance<Row>()
    }

    private fun noItemsMessage(screen: HotLightScreen): String? =
        (screen.onGetTemplate() as ListTemplate).singleList?.noItemsMessage?.toString()

    @Test
    fun `lists all nearby stores, lit first then nearest, with status and address`() {
        val client = FakeKremeingApiClient(
            stores = listOf(
                store(id = 1, name = "Far KK", distanceMiles = 5.0, status = "on"),
                store(id = 2, name = "Near KK", address = "9 Pike St", distanceMiles = 0.4, status = "on"),
                store(id = 3, name = "Dark KK", distanceMiles = 0.1, status = "off"),
            ),
        )
        val rows = rows(screen(client))

        // Lit stores first (nearest-first among them), then off stores by distance.
        assertEquals(listOf("Near KK", "Far KK", "Dark KK"), rows.map { it.title.toString() })
        assertEquals("🔥 ON · 0.4 mi · 9 Pike St", rows.first().texts.first().toString())
    }

    @Test
    fun `radius pref ranks within-radius stores first, even off ones`() {
        prefs.radiusMiles = 5.0
        val client = FakeKremeingApiClient(
            stores = listOf(
                store(id = 1, name = "Far Lit", distanceMiles = 20.0, status = "on"),
                store(id = 2, name = "Near Off", distanceMiles = 3.0, status = "off"),
            ),
        )
        // The off store inside the radius outranks the lit store outside it.
        assertEquals(listOf("Near Off", "Far Lit"), rows(screen(client)).map { it.title.toString() })
    }

    @Test
    fun `shows empty-state message when there are no nearby stores`() {
        val client = FakeKremeingApiClient(stores = emptyList())
        assertTrue(rows(screen(client)).isEmpty())
        assertEquals("No stores nearby", noItemsMessage(screen(client)))
    }

    @Test
    fun `shows error message when the backend call fails`() {
        val client = FakeKremeingApiClient(failNearby = true)
        assertEquals("Couldn't load nearby stores", noItemsMessage(screen(client)))
    }

    @Test
    fun `shows loading message before the query returns`() {
        // An executor that never runs the work leaves the screen in its
        // initial loading state.
        val pending = Executor { /* drop the runnable */ }
        val client = FakeKremeingApiClient()
        assertEquals("Looking for nearby stores…", noItemsMessage(screen(client, pending)))
    }

    @Test
    fun `tapping a store fires an ACTION_NAVIGATE geo intent`() {
        val target = store(id = 2, name = "Near KK", distanceMiles = 0.4, status = "on")
        val client = FakeKremeingApiClient(stores = listOf(target))
        val screen = screen(client)

        screen.navigateTo(target)

        val intent = carContext.startCarAppIntents.last()
        assertEquals(androidx.car.app.CarContext.ACTION_NAVIGATE, intent.action)
        assertEquals(NavigationIntent.geoUri(target), intent.dataString)
    }

    @Test
    fun `refresh re-queries the backend and exposes a Refresh action`() {
        val client = FakeKremeingApiClient(stores = listOf(store(id = 1, status = "on")))
        val screen = screen(client)
        assertEquals(1, client.nearbyCallCount) // initial load in the constructor

        screen.refresh()
        assertEquals(2, client.nearbyCallCount)

        val actions = (screen.onGetTemplate() as ListTemplate).actionStrip!!.actions
        assertTrue(actions.any { it.title?.toString() == "Refresh" })
    }

    @Test
    fun `screen controller drives the screen to a ListTemplate`() {
        val client = FakeKremeingApiClient(stores = listOf(store(id = 1, status = "on")))
        val screen = screen(client)
        val controller = ScreenController(screen)

        controller.moveToState(Lifecycle.State.STARTED)
        // The Car App test host only records a template when invalidate() is
        // called while the screen is at least STARTED (see androidx's own
        // ScreenControllerTest#getReturnedTemplates). The screen's constructor
        // refresh() runs before the controller raises the lifecycle, so its
        // invalidate() is a no-op; request a fresh template now that it has
        // started.
        screen.invalidate()

        assertTrue(controller.templatesReturned.last() is ListTemplate)
    }
}
