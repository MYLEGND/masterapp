package com.mylegnd.legend.registered

import androidx.test.platform.app.InstrumentationRegistry
import com.mylegnd.legend.registered.feature.calling.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class LegendDirectCallingTest {
    @Test fun accountSwitchingNeverReusesARetiredCallStore() = runBlocking {
        withContext(Dispatchers.Main) {
            val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as LegendApplication
            val coordinator = LegendCallingCoordinator()
            val a = com.mylegnd.legend.registered.core.model.MobileIdentity("calling-test-a", "Client")
            val b = com.mylegnd.legend.registered.core.model.MobileIdentity("calling-test-b", "Agent")
            val first = coordinator.activate(app, app.container, a)
            assertSame(first, coordinator.activate(app, app.container, a))
            val second = coordinator.activate(app, app.container, b)
            assertNotSame(first, second)
            val returned = coordinator.activate(app, app.container, a)
            assertNotSame(first, returned)
            coordinator.deactivate(returned)
        }
    }

    @Test fun nativePeersConnectWithTrickleIceAndRenegotiate() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        InstrumentationRegistry.getInstrumentation().uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.RECORD_AUDIO").close()
        withContext(Dispatchers.Main) {
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
            val policy = LegendCallPolicy(emptyList(), 1280, 720, 30, 640, 480, 24, 1_000_000, 64_000, 45, 20, 3)
            var callerConnected = false
            var calleeConnected = false
            var candidates = 0
            val epochs = mutableSetOf<Int>()
            lateinit var caller: LegendRTCPeer
            lateinit var callee: LegendRTCPeer
            caller = LegendRTCPeer(context, policy, false, true, scope,
                signal = { kind, data, epoch ->
                    if (kind == "candidate") candidates++
                    if (kind == "offer") epochs.add(epoch)
                    callee.receive(kind, data, epoch)
                }, state = { if (it == "Connected") callerConnected = true }, remoteVideo = {})
            callee = LegendRTCPeer(context, policy, false, false, scope,
                signal = { kind, data, epoch -> caller.receive(kind, data, epoch) },
                state = { if (it == "Connected") calleeConnected = true }, remoteVideo = {})
            try {
                caller.offer()
                withTimeout(15_000) { while (!callerConnected || !calleeConnected) delay(100) }
                assertTrue(candidates > 0)
                caller.offer(true)
                assertEquals(setOf(1, 2), epochs)
                caller.muted(true)
            } finally { caller.close(); callee.close(); scope.cancel() }
        }
    }
}
