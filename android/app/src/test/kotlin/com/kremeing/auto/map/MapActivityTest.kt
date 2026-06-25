package com.kremeing.auto.map

import android.Manifest
import android.app.Application
import android.os.Looper
import androidx.core.widget.NestedScrollView
import androidx.test.core.app.ApplicationProvider
import com.google.android.material.bottomsheet.BottomSheetBehavior
import com.kremeing.auto.R
import com.kremeing.auto.car.LocationSource
import com.kremeing.auto.testing.FakeKremeingApiClient
import com.kremeing.auto.testing.store
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.util.concurrent.Executor

/**
 * Smoke test that the osmdroid-backed home screen survives onCreate (catches
 * construction/lifecycle crashes such as Context-using field initializers) and
 * starts with the store-detail sheet hidden. Network/Play-services seams are
 * injected so nothing touches the device.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class MapActivityTest {

    private val app: Application get() = ApplicationProvider.getApplicationContext()

    @Before
    fun grantPermissions() {
        shadowOf(app).grantPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION,
        )
    }

    @Test
    fun `onCreate inflates the map and starts with the detail sheet hidden`() {
        val controller = Robolectric.buildActivity(MapActivity::class.java)
        controller.get().apply {
            executor = Executor { it.run() }
            apiClient = FakeKremeingApiClient(stores = listOf(store(id = 1, status = "on")))
            locationSource = LocationSource { 47.6 to -122.3 }
        }

        controller.setup()
        shadowOf(Looper.getMainLooper()).idle()

        val sheet = controller.get().findViewById<NestedScrollView>(R.id.sheet)
        assertEquals(BottomSheetBehavior.STATE_HIDDEN, BottomSheetBehavior.from(sheet).state)
    }
}
