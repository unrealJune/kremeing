package com.kremeing.auto.logic

/**
 * Parsed, validated contents of a hot-light push. The backend sends FCM
 * *data* messages (so the app renders the notification consistently in every
 * app state), with every value stringified per FCM's requirement. This module
 * turns that raw `Map<String,String>` back into typed fields and the
 * navigation URI the notification's "Navigate" action fires.
 */
data class HotLightNotification(
    val title: String,
    val body: String,
    val storeId: Int,
    val storeName: String,
    val latitude: Double,
    val longitude: Double,
) {
    /** `geo:` URI for the notification's navigate action. */
    val navigationUri: String
        get() = NavigationIntent.geoUri(latitude, longitude, storeName)
}

object FcmMessage {

    /** Keys in the FCM data payload — must match the backend buildMessageJson. */
    object Keys {
        const val TITLE = "title"
        const val BODY = "body"
        const val STORE_ID = "storeId"
        const val STORE_NAME = "storeName"
        const val LATITUDE = "latitude"
        const val LONGITUDE = "longitude"
    }

    /**
     * Parse an FCM data map into a [HotLightNotification], or return null if a
     * required field is missing or a numeric field is malformed. Returning
     * null (rather than throwing) keeps a corrupt push from crashing the
     * messaging service — the app simply drops the message.
     *
     * `title` and `body` fall back to sensible defaults so a notification can
     * still be shown even if the server omitted the copy, as long as the store
     * identity and coordinates (the actionable parts) are present.
     */
    fun parse(data: Map<String, String>): HotLightNotification? {
        val storeId = data[Keys.STORE_ID]?.trim()?.toIntOrNull() ?: return null
        val storeName = data[Keys.STORE_NAME]?.takeIf { it.isNotBlank() } ?: return null
        val latitude = data[Keys.LATITUDE]?.trim()?.toDoubleOrNull() ?: return null
        val longitude = data[Keys.LONGITUDE]?.trim()?.toDoubleOrNull() ?: return null
        if (!latitude.isFinite() || !longitude.isFinite()) return null

        val title = data[Keys.TITLE]?.takeIf { it.isNotBlank() } ?: "Hot light is on!"
        val body = data[Keys.BODY]?.takeIf { it.isNotBlank() }
            ?: "$storeName is making fresh doughnuts right now."

        return HotLightNotification(
            title = title,
            body = body,
            storeId = storeId,
            storeName = storeName,
            latitude = latitude,
            longitude = longitude,
        )
    }
}
