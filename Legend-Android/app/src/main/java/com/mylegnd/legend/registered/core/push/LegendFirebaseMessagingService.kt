package com.mylegnd.legend.registered.core.push

import android.annotation.SuppressLint
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.mylegnd.legend.registered.MainActivity
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.LegendApplication
import com.mylegnd.legend.registered.core.navigation.LegendNotificationNavigation
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents
import com.mylegnd.legend.registered.core.realtime.LegendMessagingRealtimeEvent

/** FCM is transport only. Notification text, recipient selection, and badges remain server-owned. */
// Android lint has not yet learned Firebase Messaging's replacement callback;
// onRegistered is the supported FID refresh callback in the installed SDK.
@SuppressLint("MissingFirebaseInstanceTokenRefresh")
class LegendFirebaseMessagingService : FirebaseMessagingService() {
    override fun onRegistered(installationId: String) {
        // The opaque Firebase Installation ID is forwarded directly to
        // the authenticated platform-aware endpoint; it is never logged or
        // persisted by Legend Android.
        (application as? LegendApplication)?.container?.fcmPushRegistration?.registerInstallation(installationId)
    }
    override fun onMessageReceived(message: RemoteMessage) {
        message.data["legendCall"]?.let { payload ->
            val call = runCatching {
                kotlinx.serialization.json.Json { ignoreUnknownKeys = true }.decodeFromString<com.mylegnd.legend.registered.feature.calling.LegendCallSnapshot>(payload)
            }.getOrNull() ?: return
            val platform = com.mylegnd.legend.registered.feature.calling.LegendCallPlatform
            if (platform.store != null) platform.store?.receivePush(call)
            else {
                // Authenticate through the existing account authority before acknowledging
                // a cold-start notification. FCM delivery alone is never a receipt.
                kotlinx.coroutines.runBlocking {
                    var transport: com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient? = null
                    try {
                        kotlinx.coroutines.withTimeout(8_000) {
                            val container = (application as? LegendApplication)?.container ?: return@withTimeout
                            val session = container.sessionRepository.restore() as? com.mylegnd.legend.registered.core.session.SessionState.Authenticated ?: return@withTimeout
                            transport = container.messagingRealtime(session.session.actor.identity.participantType)
                            val device = getSharedPreferences("legend_calls", Context.MODE_PRIVATE).let { prefs ->
                                prefs.getString("device", null) ?: java.util.UUID.randomUUID().toString().also { prefs.edit().putString("device", it).apply() }
                            }
                            val verified = transport?.call(com.mylegnd.legend.registered.feature.calling.LegendCallCommand("get", device, call.id))
                            val snapshot = verified?.call ?: return@withTimeout
                            val identity = session.session.actor.identity
                            if (verified.succeeded && snapshot.status == "ringing" && snapshot.calleeType.equals(identity.participantType, true) &&
                                (snapshot.calleeUserIds ?: listOf(snapshot.calleeUserId)).any { it.equals(identity.userId, true) } &&
                                java.time.Instant.parse(snapshot.expiresUtc).isAfter(java.time.Instant.now())) {
                                platform.showPushIncoming(this@LegendFirebaseMessagingService, snapshot)
                                transport?.call(com.mylegnd.legend.registered.feature.calling.LegendCallCommand("received", device, snapshot.id))
                            }
                        }
                    } catch (_: Exception) {
                        // No confirmed receipt is sent if authentication or presentation fails.
                    } finally { transport?.close() }
                }
            }
            return
        }
        // FCM and SignalR intentionally converge on one small server-issued
        // event contract. Neither transport becomes a local message or badge
        // authority; the app reconciles against the existing API projections.
        LegendRealtimeEvents.publish(
            LegendMessagingRealtimeEvent(
                conversationId = message.data["conversationId"],
                notificationId = message.data["notificationId"],
                unreadCount = message.data["unreadCount"]?.toIntOrNull(),
                revision = message.data["revision"]?.toLongOrNull(),
            ),
        )
        val title = message.data["title"] ?: message.notification?.title ?: return
        val body = message.data["body"] ?: message.notification?.body.orEmpty()
        val sender = runCatching { org.json.JSONObject(message.data["sender"].orEmpty()) }.getOrNull()
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
        val builder = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_legend_notification)
            .setContentTitle(title)
            .setContentText(body)
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .setOnlyAlertOnce(true)
        val senderName = sender?.optString("name").orEmpty()
        val senderId = sender?.optString("id").orEmpty()
        if (senderName.isNotBlank() && senderId.isNotBlank()) {
            val person = androidx.core.app.Person.Builder().setName(senderName).setKey(senderId)
            loadLegendSenderAvatar(this, sender?.optString("imagePath").orEmpty())?.let {
                person.setIcon(androidx.core.graphics.drawable.IconCompat.createWithBitmap(it))
                builder.setLargeIcon(it)
            }
            builder.setStyle(NotificationCompat.MessagingStyle(
                androidx.core.app.Person.Builder().setName(com.mylegnd.legend.registered.core.design.legendLocalized("You")).build()
            ).addMessage(body, message.sentTime, person.build()).setGroupConversation(false))
        }
        notificationManager().notify(
            message.data["notificationId"]?.hashCode() ?: message.messageId?.hashCode() ?: 0,
            builder.build(),
        )
    }

    private fun notificationManager(): NotificationManager = getSystemService(NotificationManager::class.java).also {
        ensureNotificationChannel(this)
    }

    companion object {
        const val CHANNEL = "legend_activity"

        /** Creates the channel before either foreground or system-tray FCM presentation. */
        fun ensureNotificationChannel(context: Context) {
            context.getSystemService(NotificationManager::class.java).createNotificationChannel(
                NotificationChannel(CHANNEL, "LEGEND activity", NotificationManager.IMPORTANCE_DEFAULT),
            )
        }
    }
}

    /** Only the server-issued relative capability on our configured origin is fetched. */
internal fun loadLegendSenderAvatar(context: Context, path: String): android.graphics.Bitmap? = runCatching {
        if (!path.startsWith("/api/v1/mobile/notifications/") || path.contains('\\')) return null
        val base = java.net.URI(com.mylegnd.legend.registered.core.config.LegendRuntimeConfigurationLoader.load(context).apiBaseUrl)
        val uri = base.resolve(path)
        if (uri.scheme != "https" || uri.host != base.host || uri.port != base.port) return null
        val connection = uri.toURL().openConnection() as java.net.HttpURLConnection
        try {
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 1_500
            connection.readTimeout = 1_500
            connection.useCaches = false
            if (connection.responseCode != 200 || connection.contentLengthLong > 3 * 1024 * 1024) return null
            val deadline = android.os.SystemClock.elapsedRealtime() + 2_000
            val bytes = connection.inputStream.use { input ->
                val output = java.io.ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    if (android.os.SystemClock.elapsedRealtime() > deadline) return null
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (output.size() + count > 3 * 1024 * 1024) return null
                    output.write(buffer, 0, count)
                }
                output.toByteArray()
            }
            if (bytes.size > 3 * 1024 * 1024) return null
            val options = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
            android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
            if (options.outWidth <= 0 || options.outHeight <= 0) return null
            options.inJustDecodeBounds = false
            options.inSampleSize = 1
            while (maxOf(options.outWidth, options.outHeight) / options.inSampleSize > 320) options.inSampleSize *= 2
            android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
        } finally { connection.disconnect() }
    }.getOrNull()
