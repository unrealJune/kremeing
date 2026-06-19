package com.kremeing.auto.messaging

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.kremeing.auto.R
import com.kremeing.auto.logic.HotLightNotification

/**
 * Builds and posts the hot-light notification. The notification's tap action
 * fires the `geo:` navigation URI (computed in `:logic`), so dismissing it from
 * the car or phone launches turn-by-turn directions to the lit store.
 */
object HotLightNotifier {

    const val CHANNEL_ID = "hot_light_alerts"

    fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                context.getString(R.string.notification_channel_name),
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = context.getString(R.string.notification_channel_description)
            }
            context.getSystemService(NotificationManager::class.java)
                ?.createNotificationChannel(channel)
        }
    }

    fun show(context: Context, notification: HotLightNotification) {
        ensureChannel(context)

        val navIntent = Intent(Intent.ACTION_VIEW, Uri.parse(notification.navigationUri))
        val pending = PendingIntent.getActivity(
            context,
            notification.storeId,
            navIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        val built = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_hot_light)
            .setContentTitle(notification.title)
            .setContentText(notification.body)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_NAVIGATION)
            .setAutoCancel(true)
            .setContentIntent(pending)
            .addAction(R.drawable.ic_hot_light, "Navigate", pending)
            .build()

        // storeId as the notification id collapses repeat alerts for one store.
        if (NotificationManagerCompat.from(context).areNotificationsEnabled()) {
            try {
                NotificationManagerCompat.from(context).notify(notification.storeId, built)
            } catch (_: SecurityException) {
                // POST_NOTIFICATIONS not granted — nothing to show.
            }
        }
    }
}
