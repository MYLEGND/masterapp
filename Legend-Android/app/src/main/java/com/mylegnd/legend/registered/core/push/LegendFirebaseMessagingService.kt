package com.mylegnd.legend.registered.core.push

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import androidx.core.app.NotificationCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.mylegnd.legend.registered.MainActivity
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.LegendApplication
import com.mylegnd.legend.registered.core.navigation.LegendNotificationNavigation
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents

/** FCM is transport only. Notification text, recipient selection, and badges remain server-owned. */
class LegendFirebaseMessagingService : FirebaseMessagingService() {
    override fun onNewToken(token: String) {
        // The opaque FCM transport token is forwarded directly to
        // the authenticated platform-aware endpoint; it is never logged or
        // persisted by Legend Android.
        (application as? LegendApplication)?.container?.fcmPushRegistration?.registerFreshToken(token)
    }
    override fun onMessageReceived(message: RemoteMessage) {
        // FCM and SignalR intentionally converge on one small server-issued
        // event contract. Neither transport becomes a local message or badge
        // authority; the app reconciles against the existing API projections.
        LegendRealtimeEvents.publish(
            com.mylegnd.legend.registered.core.realtime.LegendMessagingRealtimeEvent(
                conversationId = message.data["conversationId"],
                notificationId = message.data["notificationId"],
                unreadCount = message.data["unreadCount"]?.toIntOrNull(),
                revision = message.data["revision"]?.toLongOrNull(),
            ),
        )
        val notification = message.notification ?: return
        val conversationId = message.data["conversationId"].orEmpty()
        val intent = Intent(this, MainActivity::class.java)
            .setFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(LegendNotificationNavigation.EXTRA_CONVERSATION_ID, conversationId)
        val pendingIntent = PendingIntent.getActivity(
            this,
            conversationId.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        notificationManager().notify(
            message.data["notificationId"]?.hashCode() ?: message.messageId?.hashCode() ?: 0,
            NotificationCompat.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_legend_notification)
                .setContentTitle(notification.title ?: "LEGEND®")
                .setContentText(notification.body)
                .setContentIntent(pendingIntent)
                .setAutoCancel(true)
                .build(),
        )
    }
    private fun notificationManager(): NotificationManager = getSystemService(NotificationManager::class.java).also {
        ensureNotificationChannel(this)
    }

    companion object {
        const val CHANNEL = "legend_activity"

        /** Creates the channel before either foreground or system-tray FCM presentation. */
        fun ensureNotificationChannel(context: android.content.Context) {
            context.getSystemService(NotificationManager::class.java).createNotificationChannel(
                NotificationChannel(CHANNEL, "LEGEND activity", NotificationManager.IMPORTANCE_DEFAULT),
            )
        }
    }
}
