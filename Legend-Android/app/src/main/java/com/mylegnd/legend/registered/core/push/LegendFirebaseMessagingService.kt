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
    override fun onRegistered(installationId: String) {
        // The opaque FCM v1 registration identifier is forwarded directly to
        // the authenticated platform-aware endpoint; it is never logged or
        // persisted by Legend Android.
        (application as? LegendApplication)?.container?.fcmPushRegistration?.registerFreshToken(installationId)
    }
    override fun onMessageReceived(message: RemoteMessage) {
        LegendRealtimeEvents.conversationUpdated(message.data["conversationId"])
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
                .setSmallIcon(R.drawable.ic_legend_launcher)
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
