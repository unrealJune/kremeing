package com.kremeing.auto.testing

import com.kremeing.auto.api.KremeingApiClient
import com.kremeing.auto.logic.DeviceSubscribeResponse
import com.kremeing.auto.logic.NearbyStore

/**
 * A network-free [KremeingApiClient] for the `:app` Robolectric suite. It
 * returns canned [nearbyStores] and records every call so tests can assert the
 * car screen / activity drove the client as expected — no HTTP server needed.
 */
class FakeKremeingApiClient(
    private val stores: List<NearbyStore> = emptyList(),
    private val failNearby: Boolean = false,
) : KremeingApiClient("https://fake.invalid") {

    data class SubscribeCall(
        val token: String,
        val latitude: Double,
        val longitude: Double,
        val radiusMiles: Double,
    )

    var nearbyCallCount: Int = 0
        private set

    val subscribeCalls: MutableList<SubscribeCall> = mutableListOf()

    override fun nearbyStores(
        latitude: Double,
        longitude: Double,
        radiusMiles: Double,
    ): List<NearbyStore> {
        nearbyCallCount++
        if (failNearby) throw RuntimeException("boom")
        return stores
    }

    override fun subscribeDevice(
        token: String,
        latitude: Double,
        longitude: Double,
        radiusMiles: Double,
    ): DeviceSubscribeResponse {
        subscribeCalls += SubscribeCall(token, latitude, longitude, radiusMiles)
        return DeviceSubscribeResponse(id = 1L, radiusMiles = radiusMiles)
    }

    override fun unsubscribeDevice(token: String) {
        // no-op for tests
    }
}

/** Shared store fixture mirroring the `:logic` test fixtures. */
internal fun store(
    id: Int,
    name: String = "Krispy Kreme #$id",
    address: String = "$id Main St",
    lat: Double = 47.6,
    lng: Double = -122.3,
    distanceMiles: Double = 1.0,
    status: String = "on",
): NearbyStore = NearbyStore(
    id = id,
    name = name,
    address = address,
    latitude = lat,
    longitude = lng,
    distanceMiles = distanceMiles,
    currentStatus = status,
)
