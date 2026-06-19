package com.kremeing.auto.api

import com.kremeing.auto.logic.ApiCodec
import com.kremeing.auto.logic.DeviceSubscribeRequest
import com.kremeing.auto.logic.DeviceSubscribeResponse
import com.kremeing.auto.logic.DeviceUnsubscribeRequest
import com.kremeing.auto.logic.NearbyStore
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
 */
class KremeingApiClient(private val baseUrl: String) {

    /** Fetch lit-or-not stores near a coordinate within [radiusMiles]. */
    fun nearbyStores(
        latitude: Double,
        longitude: Double,
        radiusMiles: Double,
    ): List<NearbyStore> {
        val q = "latitude=${enc(latitude)}&longitude=${enc(longitude)}&radiusMiles=${enc(radiusMiles)}"
        val body = get("/stores/nearby?$q")
        return ApiCodec.decodeNearby(body).stores
    }

    /** Register (or refresh) this device's location-based push subscription. */
    fun subscribeDevice(
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
    fun unsubscribeDevice(token: String) {
        send("DELETE", "/device-subscriptions", ApiCodec.encodeUnsubscribe(DeviceUnsubscribeRequest(token)))
    }

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
}

/** Thrown when the backend responds with a non-2xx status. */
class KremeingApiException(val statusCode: Int, val responseBody: String) :
    RuntimeException("Kremeing API returned $statusCode: $responseBody")
