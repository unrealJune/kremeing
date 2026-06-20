package com.kremeing.auto.logic

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Hot-light status of a store. Mirrors the backend wire values ("on" /
 * "off" / "unknown") exactly so [fromWire] is a total round-trip. Android
 * Auto only ever surfaces [ON] stores, but the other cases must parse
 * cleanly so a malformed/older payload never crashes the car app.
 */
enum class HotLightStatus(val wire: String) {
    ON("on"),
    OFF("off"),
    UNKNOWN("unknown");

    companion object {
        /** Parse a backend status string; anything unrecognized -> [UNKNOWN]. */
        fun fromWire(value: String?): HotLightStatus =
            entries.firstOrNull { it.wire.equals(value, ignoreCase = true) } ?: UNKNOWN
    }
}

/**
 * One store as returned by the backend `GET /stores/nearby` endpoint. Field
 * names match the camelCase JSON the F# API emits (see NearbyStoreDto). The
 * raw `currentStatus` string is kept off the public surface; callers read the
 * parsed [status] instead.
 */
@Serializable
data class NearbyStore(
    val id: Int,
    val name: String,
    val address: String,
    val latitude: Double,
    val longitude: Double,
    val distanceMiles: Double,
    val currentStatus: String,
    val lastFlippedAt: String? = null,
    val firstObservedAt: String? = null,
) {
    /** Parsed hot-light status. */
    val status: HotLightStatus get() = HotLightStatus.fromWire(currentStatus)

    /** True when this store's hot light is currently on. */
    val isLit: Boolean get() = status == HotLightStatus.ON
}

/** Top-level shape of the `GET /stores/nearby` response. */
@Serializable
data class NearbyResponse(
    val stores: List<NearbyStore> = emptyList(),
)

/** Request body for `POST /device-subscriptions` (see DeviceSubscribeRequestDto). */
@Serializable
data class DeviceSubscribeRequest(
    val token: String,
    val platform: String,
    val latitude: Double,
    val longitude: Double,
    val radiusMiles: Double,
)

/** Response body for `POST /device-subscriptions`. */
@Serializable
data class DeviceSubscribeResponse(
    val id: Long,
    val radiusMiles: Double,
)

/** Request body for `DELETE /device-subscriptions`. */
@Serializable
data class DeviceUnsubscribeRequest(
    val token: String,
)
