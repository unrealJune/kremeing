package com.kremeing.auto.logic

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Shared fixtures for the logic tests. */
internal object Stores {
    fun store(
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
}

class HotLightStatusTests {
    @Test fun `parses on off unknown case-insensitively`() {
        assertEquals(HotLightStatus.ON, HotLightStatus.fromWire("on"))
        assertEquals(HotLightStatus.ON, HotLightStatus.fromWire("ON"))
        assertEquals(HotLightStatus.OFF, HotLightStatus.fromWire("off"))
        assertEquals(HotLightStatus.UNKNOWN, HotLightStatus.fromWire("unknown"))
    }

    @Test fun `unrecognized and null map to unknown`() {
        assertEquals(HotLightStatus.UNKNOWN, HotLightStatus.fromWire("lit"))
        assertEquals(HotLightStatus.UNKNOWN, HotLightStatus.fromWire(null))
        assertEquals(HotLightStatus.UNKNOWN, HotLightStatus.fromWire(""))
    }

    @Test fun `isLit reflects parsed status`() {
        assertTrue(Stores.store(1, status = "on").isLit)
        assertFalse(Stores.store(1, status = "off").isLit)
        assertFalse(Stores.store(1, status = "unknown").isLit)
    }
}

class GeoTests {
    @Test fun `distance is zero for identical points`() {
        assertEquals(0.0, Geo.distanceMiles(47.6, -122.3, 47.6, -122.3), 1e-9)
    }

    @Test fun `distance matches known Seattle-Portland separation`() {
        // ~145-150 miles; assert within a tolerance.
        val d = Geo.distanceMiles(47.6062, -122.3321, 45.5152, -122.6784)
        assertTrue(d in 140.0..155.0, "expected ~145mi, got $d")
    }

    @Test fun `distance is symmetric`() {
        val a = Geo.distanceMiles(47.6, -122.3, 40.7, -74.0)
        val b = Geo.distanceMiles(40.7, -74.0, 47.6, -122.3)
        assertEquals(a, b, 1e-9)
    }

    @Test fun `withinRadius is inclusive at the boundary`() {
        val d = Geo.distanceMiles(47.6, -122.3, 47.7, -122.3)
        assertTrue(Geo.withinRadius(47.6, -122.3, 47.7, -122.3, d))
        assertFalse(Geo.withinRadius(47.6, -122.3, 47.7, -122.3, d - 0.001))
    }
}

class LitStoreFilterTests {
    @Test fun `keeps only lit stores`() {
        val stores = listOf(
            Stores.store(1, status = "on", distanceMiles = 2.0),
            Stores.store(2, status = "off", distanceMiles = 1.0),
            Stores.store(3, status = "unknown", distanceMiles = 0.5),
        )
        val result = LitStoreFilter.litNearby(stores)
        assertEquals(listOf(1), result.map { it.id })
    }

    @Test fun `sorts lit stores by distance then id`() {
        val stores = listOf(
            Stores.store(5, distanceMiles = 3.0),
            Stores.store(2, distanceMiles = 1.0),
            Stores.store(9, distanceMiles = 1.0),
            Stores.store(1, distanceMiles = 2.0),
        )
        assertEquals(listOf(2, 9, 1, 5), LitStoreFilter.litNearby(stores).map { it.id })
    }

    @Test fun `respects the row limit`() {
        val stores = (1..10).map { Stores.store(it, distanceMiles = it.toDouble()) }
        assertEquals(LitStoreFilter.MAX_ROWS, LitStoreFilter.litNearby(stores).size)
        assertEquals(2, LitStoreFilter.litNearby(stores, limit = 2).size)
    }

    @Test fun `filters out stores beyond the radius (inclusive boundary)`() {
        val stores = listOf(
            Stores.store(1, distanceMiles = 1.0),
            Stores.store(2, distanceMiles = 10.0),
            Stores.store(3, distanceMiles = 10.001),
            Stores.store(4, distanceMiles = 25.0),
        )
        assertEquals(
            listOf(1, 2),
            LitStoreFilter.litNearby(stores, maxDistanceMiles = 10.0).map { it.id },
        )
    }

    @Test fun `null radius keeps all distances`() {
        val stores = (1..3).map { Stores.store(it, distanceMiles = it * 100.0) }
        assertEquals(3, LitStoreFilter.litNearby(stores, maxDistanceMiles = null).size)
    }

    @Test fun `non-positive radius yields empty`() {
        val stores = listOf(Stores.store(1, distanceMiles = 0.0))
        assertTrue(LitStoreFilter.litNearby(stores, maxDistanceMiles = 0.0).isEmpty())
        assertTrue(LitStoreFilter.litNearby(stores, maxDistanceMiles = -5.0).isEmpty())
    }

    @Test fun `non-positive limit yields empty`() {
        val stores = listOf(Stores.store(1))
        assertTrue(LitStoreFilter.litNearby(stores, limit = 0).isEmpty())
        assertTrue(LitStoreFilter.litNearby(stores, limit = -3).isEmpty())
    }

    @Test fun `nearestLit and anyLit`() {
        val stores = listOf(
            Stores.store(1, status = "off", distanceMiles = 0.1),
            Stores.store(2, status = "on", distanceMiles = 2.0),
            Stores.store(3, status = "on", distanceMiles = 0.5),
        )
        assertEquals(3, LitStoreFilter.nearestLit(stores)?.id)
        assertTrue(LitStoreFilter.anyLit(stores))
        assertFalse(LitStoreFilter.anyLit(listOf(Stores.store(1, status = "off"))))
        assertNull(LitStoreFilter.nearestLit(listOf(Stores.store(1, status = "off"))))
    }

    @Test fun `nearbyAll keeps off stores, lit first then nearest`() {
        val stores = listOf(
            Stores.store(1, status = "off", distanceMiles = 0.1),
            Stores.store(2, status = "on", distanceMiles = 2.0),
            Stores.store(3, status = "off", distanceMiles = 0.5),
            Stores.store(4, status = "on", distanceMiles = 1.0),
        )
        // Lit first (4 @1.0, 2 @2.0), then off by distance (1 @0.1, 3 @0.5).
        assertEquals(listOf(4, 2, 1, 3), LitStoreFilter.nearbyAll(stores).map { it.id })
    }

    @Test fun `nearbyRanked pins within-radius stores on top even if off`() {
        val stores = listOf(
            Stores.store(1, status = "on", distanceMiles = 20.0),  // far + lit
            Stores.store(2, status = "off", distanceMiles = 3.0),  // near + off
            Stores.store(3, status = "on", distanceMiles = 1.0),   // near + lit
            Stores.store(4, status = "off", distanceMiles = 15.0), // far + off
        )
        // radius=5: within-radius first (lit 3 @1.0, then off 2 @3.0), then
        // outside (lit 1 @20.0, then off 4 @15.0 — lit leads within its group).
        assertEquals(
            listOf(3, 2, 1, 4),
            LitStoreFilter.nearbyRanked(stores, radiusMiles = 5.0).map { it.id },
        )
    }

    @Test fun `nearbyRanked respects the limit`() {
        val stores = (1..20).map { Stores.store(it, distanceMiles = it.toDouble(), status = "off") }
        assertEquals(LitStoreFilter.MAX_ROWS_ALL, LitStoreFilter.nearbyRanked(stores, 5.0).size)
        assertEquals(3, LitStoreFilter.nearbyRanked(stores, 5.0, limit = 3).size)
        assertTrue(LitStoreFilter.nearbyRanked(stores, 5.0, limit = 0).isEmpty())
    }

    @Test fun `nearbyAll respects the limit and ignores radius`() {
        val stores = (1..20).map { Stores.store(it, distanceMiles = it.toDouble(), status = "off") }
        assertEquals(LitStoreFilter.MAX_ROWS_ALL, LitStoreFilter.nearbyAll(stores).size)
        assertEquals(3, LitStoreFilter.nearbyAll(stores, limit = 3).size)
        assertTrue(LitStoreFilter.nearbyAll(stores, limit = 0).isEmpty())
    }
}

class CardFormatterTests {
    @Test fun `title is the store name`() {
        assertEquals("Krispy Kreme Downtown", CardFormatter.title(Stores.store(1, name = "Krispy Kreme Downtown")))
    }

    @Test fun `subtitle combines distance and address`() {
        val s = Stores.store(1, address = "123 Main St", distanceMiles = 0.4)
        assertEquals("0.4 mi · 123 Main St", CardFormatter.subtitle(s))
    }

    @Test fun `subtitle omits empty address`() {
        val s = Stores.store(1, address = "  ", distanceMiles = 1.0)
        assertEquals("1 mi", CardFormatter.subtitle(s))
    }

    @Test fun `distance under a quarter mile renders in feet`() {
        assertEquals("0 ft", CardFormatter.formatDistance(0.0))
        assertEquals("528 ft", CardFormatter.formatDistance(0.1))
    }

    @Test fun `distance at or above a quarter mile renders in miles`() {
        assertEquals("0.3 mi", CardFormatter.formatDistance(0.25))
        assertEquals("1 mi", CardFormatter.formatDistance(1.0))
        assertEquals("2.5 mi", CardFormatter.formatDistance(2.46))
    }

    @Test fun `negative distance clamps to zero`() {
        assertEquals("0 ft", CardFormatter.formatDistance(-5.0))
    }

    @Test fun `status line text`() {
        assertEquals("Hot light is ON", CardFormatter.statusLine(Stores.store(1, status = "on")))
        assertEquals("Hot light is off", CardFormatter.statusLine(Stores.store(1, status = "off")))
        assertEquals("Hot light status unknown", CardFormatter.statusLine(Stores.store(1, status = "unknown")))
    }
}

class NavigationIntentTests {
    @Test fun `geoUri embeds coordinates and encoded label`() {
        val uri = NavigationIntent.geoUri(47.6062, -122.3321, "Krispy Kreme")
        assertEquals("geo:47.606200,-122.332100?q=47.606200,-122.332100(Krispy%20Kreme)", uri)
    }

    @Test fun `geoUri uses US locale decimal separator`() {
        val uri = NavigationIntent.geoUri(1.5, 2.5, "X")
        assertTrue(uri.contains("1.500000"), uri)
        assertTrue(uri.contains("2.500000"), uri)
    }

    @Test fun `geoUri encodes special characters in the label`() {
        val uri = NavigationIntent.geoUri(0.0, 0.0, "A & B, (Café)")
        assertFalse(uri.substringAfter("?q=").contains(" "))
        assertTrue(uri.contains("%26")) // &
    }

    @Test fun `geoUri overload from store`() {
        val s = Stores.store(1, name = "KK", lat = 10.0, lng = 20.0)
        assertEquals(NavigationIntent.geoUri(10.0, 20.0, "KK"), NavigationIntent.geoUri(s))
    }

    @Test fun `searchUri encodes the query`() {
        assertEquals("geo:0,0?q=123%20Main%20St", NavigationIntent.searchUri("123 Main St"))
    }
}

class FcmMessageTests {
    private fun fullData() = mapOf(
        "title" to "Hot light is on!",
        "body" to "Fresh doughnuts now",
        "storeId" to "42",
        "storeName" to "Krispy Kreme Downtown",
        "latitude" to "47.6062",
        "longitude" to "-122.3321",
    )

    @Test fun `parses a complete payload`() {
        val n = FcmMessage.parse(fullData())!!
        assertEquals("Hot light is on!", n.title)
        assertEquals("Fresh doughnuts now", n.body)
        assertEquals(42, n.storeId)
        assertEquals("Krispy Kreme Downtown", n.storeName)
        assertEquals(47.6062, n.latitude, 1e-9)
        assertEquals(-122.3321, n.longitude, 1e-9)
    }

    @Test fun `navigationUri derives from coordinates and name`() {
        val n = FcmMessage.parse(fullData())!!
        assertEquals(
            NavigationIntent.geoUri(47.6062, -122.3321, "Krispy Kreme Downtown"),
            n.navigationUri,
        )
    }

    @Test fun `falls back to default title and body when absent`() {
        val data = fullData().toMutableMap().apply { remove("title"); remove("body") }
        val n = FcmMessage.parse(data)!!
        assertEquals("Hot light is on!", n.title)
        assertTrue(n.body.contains("Krispy Kreme Downtown"))
    }

    @Test fun `returns null when a required field is missing`() {
        for (key in listOf("storeId", "storeName", "latitude", "longitude")) {
            val data = fullData().toMutableMap().apply { remove(key) }
            assertNull(FcmMessage.parse(data), "expected null when missing $key")
        }
    }

    @Test fun `returns null on malformed numbers`() {
        assertNull(FcmMessage.parse(fullData().toMutableMap().apply { put("storeId", "x") }))
        assertNull(FcmMessage.parse(fullData().toMutableMap().apply { put("latitude", "north") }))
        assertNull(FcmMessage.parse(fullData().toMutableMap().apply { put("longitude", "NaN") }))
    }

    @Test fun `returns null on blank store name`() {
        assertNull(FcmMessage.parse(fullData().toMutableMap().apply { put("storeName", "  ") }))
    }
}

class FlipDetectorTests {
    @Test fun `detects a store that turned on`() {
        val prev = listOf(Stores.store(1, status = "off"))
        val curr = listOf(Stores.store(1, status = "on"))
        assertEquals(listOf(1), FlipDetector.newlyLit(prev, curr).map { it.id })
        assertTrue(FlipDetector.hasNewlyLit(prev, curr))
    }

    @Test fun `ignores stores already lit`() {
        val prev = listOf(Stores.store(1, status = "on"))
        val curr = listOf(Stores.store(1, status = "on"))
        assertTrue(FlipDetector.newlyLit(prev, curr).isEmpty())
        assertFalse(FlipDetector.hasNewlyLit(prev, curr))
    }

    @Test fun `detects a brand-new lit store not seen before`() {
        val prev = emptyList<NearbyStore>()
        val curr = listOf(Stores.store(7, status = "on"))
        assertEquals(listOf(7), FlipDetector.newlyLit(prev, curr).map { it.id })
    }

    @Test fun `orders newly lit by distance`() {
        val prev = emptyList<NearbyStore>()
        val curr = listOf(
            Stores.store(1, status = "on", distanceMiles = 3.0),
            Stores.store(2, status = "on", distanceMiles = 1.0),
        )
        assertEquals(listOf(2, 1), FlipDetector.newlyLit(prev, curr).map { it.id })
    }

    @Test fun `a store turning off is not newly lit`() {
        val prev = listOf(Stores.store(1, status = "on"))
        val curr = listOf(Stores.store(1, status = "off"))
        assertTrue(FlipDetector.newlyLit(prev, curr).isEmpty())
    }
}

class ApiCodecTests {
    @Test fun `decodes a nearby response and parses status`() {
        val body = """
            {"query":{"latitude":47.6,"longitude":-122.3,"radiusMiles":25},
             "stores":[
               {"id":1,"name":"KK One","address":"1 Main","latitude":47.6,"longitude":-122.3,
                "distanceMiles":0.4,"currentStatus":"on","lastFlippedAt":null,"firstObservedAt":null}
             ]}
        """.trimIndent()
        val resp = ApiCodec.decodeNearby(body)
        assertEquals(1, resp.stores.size)
        assertTrue(resp.stores[0].isLit)
        assertEquals("KK One", resp.stores[0].name)
    }

    @Test fun `tolerates unknown fields`() {
        val body = """{"stores":[],"somethingNew":123}"""
        assertTrue(ApiCodec.decodeNearby(body).stores.isEmpty())
    }

    @Test fun `encodes a subscribe request`() {
        val req = DeviceSubscribeRequest("tok", "android", 47.6, -122.3, 25.0)
        val json = ApiCodec.encodeSubscribe(req)
        val round = ApiCodec.json.decodeFromString(DeviceSubscribeRequest.serializer(), json)
        assertEquals(req, round)
        assertTrue(json.contains("\"token\":\"tok\""))
        assertTrue(json.contains("\"platform\":\"android\""))
    }

    @Test fun `decodes a subscribe response`() {
        val resp = ApiCodec.decodeSubscribe("""{"id":99,"radiusMiles":25.0}""")
        assertEquals(99L, resp.id)
        assertEquals(25.0, resp.radiusMiles, 1e-9)
    }

    @Test fun `encodes an unsubscribe request`() {
        assertEquals("""{"token":"abc"}""", ApiCodec.encodeUnsubscribe(DeviceUnsubscribeRequest("abc")))
    }
}
