package com.mylegnd.legend.registered.core.push

import android.app.NotificationChannel
import android.app.NotificationManager
import androidx.core.app.NotificationCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents

/** FCM is transport only. Notification text, recipient selection, and badges remain server-owned. */
class LegendFirebaseMessagingService : FirebaseMessagingService() {
    @Deprecated("Firebase callback retained for transport-token lifecycle.")
    override fun onNewToken(token: String) {
        // Deliberately not logged or persisted. Registration is enabled only once AgentPortal exposes the
        // platform-aware device-registration contract; today it only exposes APNs-specific endpoints.
    }
    override fun onMessageReceived(message: RemoteMessage) {
        LegendRealtimeEvents.conversationUpdated(message.data["conversationId"])
        val notification = message.notification ?: return
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "LEGEND activity", NotificationManager.IMPORTANCE_DEFAULT),
        )
        manager.notify(message.messageId?.hashCode() ?: 0, NotificationCompat.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_legend_launcher).setContentTitle(notification.title ?: "LEGEND®").setContentText(notification.body).setAutoCancel(true).build())
    }
    private companion object { const val CHANNEL = "legend_activity" }
}
