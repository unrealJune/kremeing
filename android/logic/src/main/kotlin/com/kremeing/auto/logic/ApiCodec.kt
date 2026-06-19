package com.kremeing.auto.logic

import kotlinx.serialization.json.Json

/**
 * The single JSON configuration the app uses to talk to the Kremeing backend,
 * plus thin typed encode/decode helpers. Kept in `:logic` (not the Android
 * `:app` module) so the wire contract with the F# API is unit-tested on the
 * JVM rather than only exercised on a device.
 *
 * `ignoreUnknownKeys` makes the client resilient to the backend adding fields.
 */
object ApiCodec {

    val json: Json = Json {
        ignoreUnknownKeys = true
    }

    /** Decode a `GET /stores/nearby` (or `/stores/search`) response body. */
    fun decodeNearby(body: String): NearbyResponse =
        json.decodeFromString(NearbyResponse.serializer(), body)

    /** Encode a `POST /device-subscriptions` request body. */
    fun encodeSubscribe(request: DeviceSubscribeRequest): String =
        json.encodeToString(DeviceSubscribeRequest.serializer(), request)

    /** Decode a `POST /device-subscriptions` response body. */
    fun decodeSubscribe(body: String): DeviceSubscribeResponse =
        json.decodeFromString(DeviceSubscribeResponse.serializer(), body)

    /** Encode a `DELETE /device-subscriptions` request body. */
    fun encodeUnsubscribe(request: DeviceUnsubscribeRequest): String =
        json.encodeToString(DeviceUnsubscribeRequest.serializer(), request)
}
