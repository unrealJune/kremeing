package com.kremeing.auto.logic

import kotlinx.serialization.Serializable

/**
 * Wire models for the history/uptime endpoints, mirroring the F# API
 * (`openapi.yaml`: HotLightFlip, HotLightHistory, UptimeBucket, UptimeResponse).
 * Field names match the camelCase JSON the backend emits; `ApiCodec.json` is
 * configured with `ignoreUnknownKeys` so additive backend changes don't break
 * decoding. These feed the shared heat-bar reconstruction in [Uptime].
 */

/** One observed status flip (or the first-observation anchor) for a store. */
@Serializable
data class HotLightFlip(
    val storeId: Int? = null,
    val status: String,
    val observedAt: String,
)

/** `GET /stores/{id}/history` — flips within a range, oldest first. */
@Serializable
data class HotLightHistory(
    val storeId: Int,
    val rangeStart: String? = null,
    val rangeEnd: String? = null,
    val flips: List<HotLightFlip> = emptyList(),
)

/** One aggregated uptime bucket (`fractionOn` is on/observed, not on/total). */
@Serializable
data class UptimeBucket(
    val startUtc: String,
    val endUtc: String,
    val onSeconds: Double = 0.0,
    val offSeconds: Double = 0.0,
    val observedSeconds: Double = 0.0,
    val totalSeconds: Double = 0.0,
    val fractionOn: Double = 0.0,
)

/** `GET /stores/{id}/uptime?bucket=hour|day` response. */
@Serializable
data class UptimeResponse(
    val storeId: Int,
    val bucket: String,
    val rangeStart: String? = null,
    val rangeEnd: String? = null,
    val buckets: List<UptimeBucket> = emptyList(),
)
