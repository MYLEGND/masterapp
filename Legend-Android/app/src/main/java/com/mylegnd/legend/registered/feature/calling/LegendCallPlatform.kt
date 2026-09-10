package com.mylegnd.legend.registered.feature.calling

import android.app.*
import android.content.*
import android.content.pm.ServiceInfo
import android.media.AudioManager
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
    const val CHANNEL = "legend_calls"
    const val NOTIFICATION = 7042
    private fun handle(context: Context) = PhoneAccountHandle(ComponentName(context, LegendConnectionService::class.java), "legend")
    fun register(context: Context) {
        val telecom = context.getSystemService(TelecomManager::class.java)
        telecom.registerPhoneAccount(PhoneAccount.builder(handle(context), "LEGEND®").setCapabilities(PhoneAccount.CAPABILITY_SELF_MANAGED).build())
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL, "Legend calls", NotificationManager.IMPORTANCE_HIGH).apply { description = "Incoming and ongoing Legend calls" })
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
    }
    fun showPushIncoming(context: Context, call: LegendCallSnapshot) {
        if (java.time.Instant.parse(call.expiresUtc).isBefore(java.time.Instant.now())) return
        register(context)
        val launch = PendingIntent.getActivity(context, 7043, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val decline = PendingIntent.getBroadcast(context, 7044, Intent(context, LegendCallActionReceiver::class.java).setAction("end").putExtra("callId", call.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_legend_notification)
            .setContentTitle(call.callerName).setContentText("Incoming Legend call").setCategory(NotificationCompat.CATEGORY_CALL)
            .setPriority(NotificationCompat.PRIORITY_MAX).setContentIntent(launch).setFullScreenIntent(launch, true)
            .setOngoing(true).setTimeoutAfter(45_000).addAction(0, "Decline", decline).addAction(0, "Open call", launch).build()
        context.getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification)
    }
    fun foreground(context: Context, video: Boolean) {
        context.startForegroundService(Intent(context, LegendCallForegroundService::class.java).putExtra("video", video))
    }
    fun ended(context: Context) {
        connection?.setDisconnected(DisconnectCause(DisconnectCause.LOCAL)); connection?.destroy(); connection = null
        context.stopService(Intent(context, LegendCallForegroundService::class.java))
        context.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)
    }
}
class LegendConnectionService : ConnectionService() {
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
        override fun onShowIncomingCallUi() { LegendCallPlatform.showIncoming(this@LegendConnectionService) }
        override fun onAnswer() { store.requestSystemAnswer() }
        override fun onAnswer(videoState: Int) { store.requestSystemAnswer() }
        override fun onReject() { store.end() }
        override fun onDisconnect() { store.end() }
        override fun onAbort() { store.end() }
        override fun onCallAudioStateChanged(state: CallAudioState) { store.audioRouteChanged(state.route) }
    }.also { LegendCallPlatform.connection = it }
}
class LegendCallForegroundService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val call = LegendCallPlatform.store?.state?.value?.call ?: run { stopSelf(); return START_NOT_STICKY }
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
        try { ServiceCompat.startForeground(this, LegendCallPlatform.NOTIFICATION, notification, types) }
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
