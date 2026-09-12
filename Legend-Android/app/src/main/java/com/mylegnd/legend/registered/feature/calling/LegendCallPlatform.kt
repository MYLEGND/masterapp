package com.mylegnd.legend.registered.feature.calling

import android.app.*
import android.content.*
import android.content.pm.ServiceInfo
import android.media.AudioManager
import android.media.AudioAttributes
import android.net.Uri
import android.os.*
import android.telecom.*
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import kotlinx.coroutines.*
import com.mylegnd.legend.registered.LegendApplication
import com.mylegnd.legend.registered.core.session.SessionState
import com.mylegnd.legend.registered.MainActivity
import com.mylegnd.legend.registered.R

/** Android Telecom owns system call coordination; the account store owns all call data. */
object LegendCallPlatform {
    var store: LegendCallViewModel? = null
    var connection: Connection? = null
    private var avatarJob: Job? = null
    private var answeringCallId: String? = null
    private val audioGate = LegendCallAudioGate()
    val hasTelecomAudioFocus: Boolean get() = audioGate.hasFocus
    val mediaSession: Int get() = audioGate.mediaSession
    private fun audioEvent(event: String) {
        android.util.Log.i("LegendCallMedia", "mediaSession=$mediaSession event=$event telecomFocus=${audioGate.hasFocus} foregroundReady=${audioGate.foregroundIsReady}")
    }
    fun bindAudioService(service: Any) { audioGate.bindService(service); audioEvent("service-bound") }
    fun unbindAudioService(service: Any) {
        if (audioGate.unbindService(service)) {
            audioEvent("service-destroyed")
            store?.telecomAudioFocusChanged(false)
        }
    }
    fun telecomAudioFocusChanged(service: Any, granted: Boolean) {
        if (!audioGate.focusChanged(service, granted)) return
        audioEvent(if (granted) "focus-gained" else "focus-lost")
        store?.telecomAudioFocusChanged(granted)
    }
    fun foregroundReady(callId: String) { audioGate.foregroundReady(callId); audioEvent("foreground-started") }
    suspend fun activateForMedia(context: Context, call: LegendCallSnapshot) {
        val owner = checkNotNull(store) { "Android call session is unavailable." }
        check(owner.state.value.call?.id == call.id) { "This call has ended." }
        val activeConnection = checkNotNull(connection) { "Android call connection is unavailable." }
        audioGate.begin(call.id)
        audioEvent("activation-started")
        answeringCallId = call.id
        avatarJob?.cancel(); avatarJob = null
        // Cancel the insistent incoming notification before replacing it with the
        // silent foreground notification; an in-flight avatar must not ring again.
        context.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)
        activeConnection.setActive()
        context.startForegroundService(Intent(context, LegendCallForegroundService::class.java)
            .putExtra("video", call.video).putExtra("callId", call.id))
        try {
            withTimeout(8_000) { audioGate.awaitReady(call.id, Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) }
            audioEvent("media-ready")
        } catch (error: TimeoutCancellationException) {
            audioEvent("readiness-timeout")
            throw error
        }
        if (store !== owner || connection !== activeConnection || owner.state.value.call?.id != call.id)
            throw CancellationException("This call has ended.")
    }
    const val CHANNEL = "legend_calls"
    const val NOTIFICATION = 7042
    private fun handle(context: Context) = PhoneAccountHandle(ComponentName(context, LegendConnectionService::class.java), "legend")
    fun startScreenSharing(context: Context, permission: Intent) {
        context.startForegroundService(Intent(context, LegendCallForegroundService::class.java)
            .putExtra("video", true).putExtra("screenPermission", permission).putExtra("callId", store?.state?.value?.call?.id))
    }
    fun register(context: Context) {
        val telecom = context.getSystemService(TelecomManager::class.java)
        telecom.registerPhoneAccount(PhoneAccount.builder(handle(context), "LEGEND®").setCapabilities(PhoneAccount.CAPABILITY_SELF_MANAGED).build())
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL, "Legend calls", NotificationManager.IMPORTANCE_HIGH).apply { description = "Incoming and ongoing Legend calls"
            setSound(Uri.parse("android.resource://${context.packageName}/${R.raw.legend_incoming}"), AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE).build())
            enableVibration(true) })
    }
    fun incoming(context: Context, call: LegendCallSnapshot) {
        context.getSystemService(TelecomManager::class.java).addNewIncomingCall(handle(context), Bundle().apply { putString("legend_call_id", call.id) })
    }
    fun outgoing(context: Context, id: String) {
        try { context.getSystemService(TelecomManager::class.java).placeCall(Uri.parse("legend:$id"), Bundle().apply {
            putParcelable(TelecomManager.EXTRA_PHONE_ACCOUNT_HANDLE, handle(context))
            putBundle(TelecomManager.EXTRA_OUTGOING_CALL_EXTRAS, Bundle().apply { putString("legend_call_id", id) })
        }) } catch (error: SecurityException) {
            throw IllegalStateException("Android did not allow this call. Check calling permissions and try again.", error)
        }
    }
    fun showIncoming(context: Context) {
        val call = store?.state?.value?.call ?: return
        showPushIncoming(context, call)
        store?.confirmIncomingPresentation(call.id)
    }
    fun showPushIncoming(context: Context, call: LegendCallSnapshot) {
        if (answeringCallId == call.id) return
        if (java.time.Instant.parse(call.expiresUtc).isBefore(java.time.Instant.now())) return
        register(context)
        val launch = PendingIntent.getActivity(context, 7043, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val decline = PendingIntent.getBroadcast(context, 7044, Intent(context, LegendCallActionReceiver::class.java).setAction("end").putExtra("callId", call.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val manager = context.getSystemService(NotificationManager::class.java)
        check(manager.areNotificationsEnabled() && manager.getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE) {
            "Enable Legend call notifications to receive incoming calls."
        }
        val notification = NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_legend_notification)
            .addExtras(Bundle().apply { putString("legend_call_id", call.id) })
            .setContentTitle(call.callerName).setContentText("Incoming Legend® call").setCategory(NotificationCompat.CATEGORY_CALL)
            .setPriority(NotificationCompat.PRIORITY_MAX).setContentIntent(launch).setFullScreenIntent(launch, true)
            .setOngoing(true).setOnlyAlertOnce(true).setTimeoutAfter((java.time.Instant.parse(call.expiresUtc).toEpochMilli() - System.currentTimeMillis()).coerceAtLeast(1)).addAction(0, "Decline", decline).addAction(0, "Open call", launch).build()
        notification.flags = notification.flags or Notification.FLAG_INSISTENT
        manager.notify(NOTIFICATION, notification)
        avatarJob?.cancel()
        call.callerImagePath?.let { path ->
            avatarJob = CoroutineScope(Dispatchers.IO).launch {
                val bitmap = com.mylegnd.legend.registered.core.push.loadLegendSenderAvatar(context, path) ?: return@launch
                withContext(Dispatchers.Main) {
                    if (answeringCallId != call.id && java.time.Instant.parse(call.expiresUtc).isAfter(java.time.Instant.now()) &&
                        manager.activeNotifications.any { it.id == NOTIFICATION && it.notification.extras.getString("legend_call_id") == call.id }) {
                        manager.notify(NOTIFICATION, NotificationCompat.Builder(context, notification)
                            .setLargeIcon(bitmap).setOnlyAlertOnce(true).build())
                    }
                }
            }
        }
    }
    fun ended(context: Context) {
        audioEvent("call-ended")
        audioGate.ended()
        answeringCallId = null
        avatarJob?.cancel(); avatarJob = null
        connection?.setDisconnected(DisconnectCause(DisconnectCause.LOCAL)); connection?.destroy(); connection = null
        context.stopService(Intent(context, LegendCallForegroundService::class.java))
        context.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)
    }
}
class LegendConnectionService : ConnectionService() {
    override fun onCreate() {
        super.onCreate()
        LegendCallPlatform.bindAudioService(this)
    }
    override fun onDestroy() {
        LegendCallPlatform.unbindAudioService(this)
        super.onDestroy()
    }
    // These callbacks describe the service's focus, including between connections.
    // The bound service identity rejects callbacks from a replaced service instance.
    override fun onConnectionServiceFocusGained() {
        LegendCallPlatform.telecomAudioFocusChanged(this, true)
    }
    override fun onConnectionServiceFocusLost() {
        try { LegendCallPlatform.telecomAudioFocusChanged(this, false) }
        finally { connectionServiceFocusReleased() }
    }
    override fun onCreateOutgoingConnection(manager: PhoneAccountHandle?, request: ConnectionRequest): Connection {
        val store = LegendCallPlatform.store ?: return Connection.createFailedConnection(DisconnectCause(DisconnectCause.ERROR))
        if (request.address?.schemeSpecificPart != store.pendingCallId) return Connection.createFailedConnection(DisconnectCause(DisconnectCause.ERROR))
        val connection = makeConnection(store)
        connection.setDialing()
        store.placePendingCall()
        return connection
    }
    override fun onCreateIncomingConnection(manager: PhoneAccountHandle?, request: ConnectionRequest): Connection {
        val store = LegendCallPlatform.store ?: return Connection.createFailedConnection(DisconnectCause(DisconnectCause.ERROR))
        if (request.extras?.getString("legend_call_id") != store.state.value.call?.id) return Connection.createFailedConnection(DisconnectCause(DisconnectCause.ERROR))
        return makeConnection(store).apply { setRinging() }
    }
    override fun onCreateOutgoingConnectionFailed(manager: PhoneAccountHandle?, request: ConnectionRequest) { LegendCallPlatform.store?.platformFailed() }
    override fun onCreateIncomingConnectionFailed(manager: PhoneAccountHandle?, request: ConnectionRequest) { LegendCallPlatform.store?.platformFailed() }
    private fun makeConnection(store: LegendCallViewModel): Connection = object : Connection() {
        init {
            connectionProperties = PROPERTY_SELF_MANAGED
            audioModeIsVoip = true
            setAddress(Uri.parse("legend:call"), TelecomManager.PRESENTATION_ALLOWED)
            setCallerDisplayName(store.state.value.name, TelecomManager.PRESENTATION_ALLOWED)
        }
        override fun onShowIncomingCallUi() { runCatching { LegendCallPlatform.showIncoming(this@LegendConnectionService) }.onFailure { store.platformFailed() } }
        override fun onAnswer() { store.requestSystemAnswer() }
        override fun onAnswer(videoState: Int) { store.requestSystemAnswer() }
        override fun onReject() { store.systemEnd() }
        override fun onDisconnect() { store.systemEnd() }
        override fun onAbort() { store.systemEnd() }
        override fun onCallAudioStateChanged(state: CallAudioState) { store.audioRouteChanged(state.route) }
    }.also { LegendCallPlatform.connection = it }
}
class LegendCallForegroundService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val call = LegendCallPlatform.store?.state?.value?.call ?: run { stopSelf(); return START_NOT_STICKY }
        if (intent?.getStringExtra("callId")?.let { it != call.id } == true) return START_NOT_STICKY
        val launch = PendingIntent.getActivity(this, 7043, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val end = PendingIntent.getBroadcast(this, 7044, Intent(this, LegendCallActionReceiver::class.java).setAction("end").putExtra("callId", call.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(this, LegendCallPlatform.CHANNEL).setSmallIcon(R.drawable.ic_legend_notification)
            .setContentTitle("LEGEND®").setContentText("Call in progress").setOngoing(true).setCategory(NotificationCompat.CATEGORY_CALL)
            .setContentIntent(launch).addAction(0, "End call", end).setSilent(true).build()
        var types = if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL else 0
        if (Build.VERSION.SDK_INT >= 30) {
            types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            if (intent?.getBooleanExtra("video", false) == true) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
        }
        @Suppress("DEPRECATION")
        val screenPermission = intent?.getParcelableExtra<Intent>("screenPermission")
        if (screenPermission != null && Build.VERSION.SDK_INT >= 29) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        try {
            ServiceCompat.startForeground(this, LegendCallPlatform.NOTIFICATION, notification, types)
            LegendCallPlatform.foregroundReady(call.id)
            if (screenPermission != null) LegendCallPlatform.store?.captureScreen(screenPermission)
        }
        catch (_: RuntimeException) { LegendCallPlatform.store?.platformFailed(); stopSelf() }
        return START_NOT_STICKY
    }
}
class LegendCallActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != "end") return
        val id = intent.getStringExtra("callId") ?: return
        val store = LegendCallPlatform.store
        if (store != null) {
            if (id == store.state.value.call?.id) store.end()
            return
        }
        context.getSystemService(NotificationManager::class.java).cancel(LegendCallPlatform.NOTIFICATION)
        val pendingResult = goAsync()
        CoroutineScope(Dispatchers.Main.immediate).launch {
            var transport: com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient? = null
            try {
                withTimeout(8_000) {
                    val container = (context.applicationContext as? LegendApplication)?.container ?: return@withTimeout
                    val restored = container.sessionRepository.restore() as? SessionState.Authenticated ?: return@withTimeout
                    // Resolve the current saved account through the normal session authority;
                    // notification extras never choose a user or supply credentials.
                    LegendCallPlatform.store?.let { active ->
                        if (active.state.value.call?.id == id) active.end()
                        return@withTimeout
                    }
                    val device = context.getSharedPreferences("legend_calls", Context.MODE_PRIVATE).getString("device", null) ?: return@withTimeout
                    transport = container.messagingRealtime(restored.session.actor.identity.participantType)
                    transport?.call(LegendCallCommand("decline", device, id))
                }
            } catch (_: Exception) {
                // The authoritative ring lease expires if offline; no local call is fabricated.
            } finally { transport?.close(); pendingResult.finish(); cancel() }
        }
    }
}
