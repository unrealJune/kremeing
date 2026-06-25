package com.kremeing.auto.car

import androidx.car.app.model.PaneTemplate
import androidx.car.app.testing.TestCarContext
import androidx.test.core.app.ApplicationProvider
import com.kremeing.auto.car.screens.StoreDetailScreen
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
 * Layer 1 test of the Android Auto store-detail screen: it renders a
 * [PaneTemplate] (status + heat-bar bitmap + Navigate) from a network-free fake.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class StoreDetailScreenTest {

    private val direct = Executor { it.run() }
    private lateinit var carContext: TestCarContext

    @Before
    fun setUp() {
        carContext = TestCarContext.createCarContext(ApplicationProvider.getApplicationContext())
    }

    @Test
    fun `renders a PaneTemplate with the heat bar`() {
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

        val screen = StoreDetailScreen(carContext, target, client, direct)

        assertTrue(screen.onGetTemplate() is PaneTemplate)
    }
}
