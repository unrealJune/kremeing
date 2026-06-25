package com.kremeing.auto.car

import androidx.car.app.navigation.model.MapWithContentTemplate
import androidx.car.app.testing.TestCarContext
import androidx.test.core.app.ApplicationProvider
import com.kremeing.auto.car.screens.StoreSurfaceScreen
import com.kremeing.auto.logic.HotLightFlip
import com.kremeing.auto.logic.HotLightHistory
import com.kremeing.auto.testing.FakeKremeingApiClient
import com.kremeing.auto.testing.store
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.concurrent.Executor

/**
 * Layer 1 test of the Android Auto surface dashboard screen: it renders a
 * [MapWithContentTemplate] (weather-category host for the custom heat-bar
 * surface) from a network-free fake.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class StoreSurfaceScreenTest {

    private val direct = Executor { it.run() }
    private lateinit var carContext: TestCarContext

    @Before
    fun setUp() {
        carContext = TestCarContext.createCarContext(ApplicationProvider.getApplicationContext())
    }

    @Test
    fun `renders a MapWithContentTemplate`() {
        val target = store(id = 1, name = "Krispy Kreme Seattle", status = "on")
        val client = FakeKremeingApiClient(
            history = HotLightHistory(
                storeId = 1,
                flips = listOf(
                    HotLightFlip(1, "off", "2026-06-24T00:00:00+00:00"),
                    HotLightFlip(1, "on", "2026-06-24T09:00:00+00:00"),
                ),
            ),
        )

        val screen = StoreSurfaceScreen(carContext, target, client, direct)

        assertTrue(screen.onGetTemplate() is MapWithContentTemplate)
    }
}
