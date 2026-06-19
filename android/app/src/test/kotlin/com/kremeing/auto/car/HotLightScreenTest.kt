package com.kremeing.auto.car

import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.testing.ScreenController
import androidx.car.app.testing.TestCarContext
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ApplicationProvider
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

    private lateinit var carContext: TestCarContext
    private lateinit var prefs: SubscriptionPrefs

    @Before
    fun setUp() {
        carContext = TestCarContext.createCarContext(ApplicationProvider.getApplicationContext())
        prefs = SubscriptionPrefs(carContext)
    }

    private fun screen(client: FakeKremeingApiClient, executor: Executor = direct) =
        HotLightScreen(carContext, client, prefs, executor)

    private fun rows(screen: HotLightScreen): List<Row> {
        val template = screen.onGetTemplate() as ListTemplate
        return template.singleList!!.items.filterIsInstance<Row>()
    }

    private fun noItemsMessage(screen: HotLightScreen): String? =
        (screen.onGetTemplate() as ListTemplate).singleList?.noItemsMessage?.toString()

    @Test
    fun `lists only lit stores, nearest first, with formatted cards`() {
        val client = FakeKremeingApiClient(
            stores = listOf(
                store(id = 1, name = "Far KK", distanceMiles = 5.0, status = "on"),
                store(id = 2, name = "Near KK", address = "9 Pike St", distanceMiles = 0.4, status = "on"),
                store(id = 3, name = "Dark KK", distanceMiles = 0.1, status = "off"),
            ),
        )
        val rows = rows(screen(client))

        assertEquals(listOf("Near KK", "Far KK"), rows.map { it.title.toString() })
        assertEquals("0.4 mi · 9 Pike St", rows.first().texts.first().toString())
    }

    @Test
    fun `shows empty-state message when nothing is lit`() {
        val client = FakeKremeingApiClient(stores = listOf(store(id = 1, status = "off")))
        assertTrue(rows(screen(client)).isEmpty())
        assertEquals("No hot lights on right now", noItemsMessage(screen(client)))
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
        assertEquals("Looking for hot lights nearby…", noItemsMessage(screen(client, pending)))
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
        val controller = ScreenController(screen(client))

        controller.moveToState(Lifecycle.State.STARTED)

        assertTrue(controller.templatesReturned.last() is ListTemplate)
    }
}
