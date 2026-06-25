package com.kremeing.auto.api

import com.kremeing.auto.logic.ApiCodec
import com.kremeing.auto.logic.DeviceSubscribeRequest
import com.kremeing.auto.logic.DeviceSubscribeResponse
import com.kremeing.auto.logic.DeviceUnsubscribeRequest
import com.kremeing.auto.logic.HotLightHistory
import com.kremeing.auto.logic.NearbyStore
import com.kremeing.auto.logic.UptimeResponse
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * Minimal HTTP client for the Kremeing backend. Uses [HttpURLConnection] (no
 * networking dependency) and delegates all (de)serialization to the shared
 * [ApiCodec] in `:logic`, so the wire contract stays unit-tested off-device.
 *
 * All calls are blocking and must be invoked off the main thread (the car
 * screen and messaging service already run them on background executors).
 *
 * The class and its endpoint methods are `open` so tests (notably the `:app`
 * Robolectric suite) can subclass it with a network-free fake instead of
 * standing up a real HTTP server.
 */
open class KremeingApiClient(private val baseUrl: String) {

    /**
     * Fetch the [limit] nearest stores (lit-or-not) to a coordinate.
     *
     * Query params mirror the backend contract (see openapi.yaml `/stores/nearby`):
     * `lat`, `lng` and an optional `limit` — NOT `latitude`/`longitude`/`radiusMiles`.
     * The endpoint has no radius filter; it returns the nearest stores and the
     * caller narrows to lit ones (see LitStoreFilter).
     */
    open fun nearbyStores(
        latitude: Double,
        longitude: Double,
        limit: Int = DEFAULT_NEARBY_LIMIT,
    ): List<NearbyStore> {
        val q = "lat=${enc(latitude)}&lng=${enc(longitude)}&limit=$limit"
        val body = get("/stores/nearby?$q")
        return ApiCodec.decodeNearby(body).stores
    }

    /** Register (or refresh) this device's location-based push subscription. */
    open fun subscribeDevice(
        token: String,
        latitude: Double,
        longitude: Double,
        radiusMiles: Double,
    ): DeviceSubscribeResponse {
        val request = DeviceSubscribeRequest(
            token = token,
            platform = "android",
            latitude = latitude,
            longitude = longitude,
            radiusMiles = radiusMiles,
        )
        val body = send("POST", "/device-subscriptions", ApiCodec.encodeSubscribe(request))
        return ApiCodec.decodeSubscribe(body)
    }

    /** Remove this device's push subscription (e.g. on token rotation/logout). */
    open fun unsubscribeDevice(token: String) {
        send("DELETE", "/device-subscriptions", ApiCodec.encodeUnsubscribe(DeviceUnsubscribeRequest(token)))
    }

    /** Free-text store search by ZIP or "City, ST" (same shape as nearby). */
    open fun searchStores(query: String, limit: Int = DEFAULT_NEARBY_LIMIT): List<NearbyStore> {
        val body = get("/stores/search?q=${encStr(query)}&limit=$limit")
        return ApiCodec.decodeNearby(body).stores
    }

    /**
     * Flip history for one store. [sinceIso]/[untilIso] are ISO-8601 instants;
     * omitting both defaults to the backend's last-7-days window.
     */
    open fun history(
        storeId: Int,
        sinceIso: String? = null,
        untilIso: String? = null,
    ): HotLightHistory {
        val params = buildList {
            if (sinceIso != null) add("since=${encStr(sinceIso)}")
            if (untilIso != null) add("until=${encStr(untilIso)}")
        }
        val suffix = if (params.isEmpty()) "" else "?" + params.joinToString("&")
        return ApiCodec.decodeHistory(get("/stores/$storeId/history$suffix"))
    }

    /** Aggregated uptime buckets for one store ([bucket] is "hour" or "day"). */
    open fun uptime(storeId: Int, bucket: String): UptimeResponse =
        ApiCodec.decodeUptime(get("/stores/$storeId/uptime?bucket=${encStr(bucket)}"))

    private fun get(path: String): String = request("GET", path, null)

    private fun send(method: String, path: String, json: String): String =
        request(method, path, json)

    private fun request(method: String, path: String, json: String?): String {
        val conn = (URL(baseUrl.trimEnd('/') + path).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 10_000
            readTimeout = 10_000
            setRequestProperty("Accept", "application/json")
            if (json != null) {
                doOutput = true
                setRequestProperty("Content-Type", "application/json")
            }
        }
        try {
            if (json != null) {
                conn.outputStream.use { it.write(json.toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader()?.use(BufferedReader::readText).orEmpty()
            if (code !in 200..299) {
                throw KremeingApiException(code, text)
            }
            return text
        } finally {
            conn.disconnect()
        }
    }

    private fun enc(value: Double): String =
        URLEncoder.encode(value.toString(), "UTF-8")

    private fun encStr(value: String): String =
        URLEncoder.encode(value, "UTF-8")

    private companion object {
        /** Backend's own default page size for `/stores/nearby` (upstream caps at 12). */
        const val DEFAULT_NEARBY_LIMIT = 12
    }
}

/** Thrown when the backend responds with a non-2xx status. */
class KremeingApiException(val statusCode: Int, val responseBody: String) :
    RuntimeException("Kremeing API returned $statusCode: $responseBody")
