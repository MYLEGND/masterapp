(() => {
  if (!window.signalR) return;

  const listeners = { call: [], del: [], page: [], reorder: [], update: [] };

  // App Insights trace helper — no-op when AI not loaded
  function aiTrace(name, props) {
    try {
      if (window.appInsights && typeof window.appInsights.trackEvent === 'function') {
        window.appInsights.trackEvent({ name }, props || {});
      }
    } catch (_) {}
  }

  function aiException(err, props) {
    try {
      if (window.appInsights && typeof window.appInsights.trackException === 'function') {
        window.appInsights.trackException({ exception: err, properties: props || {} });
      }
    } catch (_) {}
  }

  const MAX_BACKOFF_MS = 30000; // 30 s ceiling
  let attempt = 0;

  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/livesync")
    .withAutomaticReconnect({
      // Custom backoff: 0, 2, 4, 8, 16, 30, 30, 30 … seconds
      nextRetryDelayInMilliseconds(ctx) {
        const delay = Math.min(1000 * Math.pow(2, ctx.previousRetryCount), MAX_BACKOFF_MS);
        console.warn(`[live-sync] reconnect attempt ${ctx.previousRetryCount + 1}, retry in ${delay}ms`);
        aiTrace('LiveSync.Reconnecting', { attempt: ctx.previousRetryCount + 1, delayMs: delay });
        return delay;
      }
    })
    .build();

  // Some older SignalR client builds expose onreconnecting as a property,
  // others as a function. Guard for both so the hook never throws.
  if (typeof connection.onreconnecting === 'function') {
    connection.onreconnecting(err => {
      console.warn('[live-sync] connection lost, reconnecting…', err);
      if (err) aiException(err, { phase: 'reconnecting' });
    });
  } else if ('onreconnecting' in connection) {
    connection.onreconnecting = err => {
      console.warn('[live-sync] connection lost, reconnecting…', err);
      if (err) aiException(err, { phase: 'reconnecting' });
    };
  } else {
    // Fallback: no hook available — silently continue
  }

  if (typeof connection.onreconnected === 'function') {
    connection.onreconnected(connId => {
      console.info('[live-sync] reconnected', connId);
      attempt = 0;
      aiTrace('LiveSync.Reconnected', { connectionId: connId });
    });
  } else if ('onreconnected' in connection) {
    connection.onreconnected = connId => {
      console.info('[live-sync] reconnected', connId);
      attempt = 0;
      aiTrace('LiveSync.Reconnected', { connectionId: connId });
    };
  } else {
    // No hook exposed — silently continue
  }

  const attachOnClose = () => {
    const handler = err => {
      console.error('[live-sync] connection closed', err);
      if (err) aiException(err, { phase: 'closed' });
      // Manual restart with exponential backoff after final close
      scheduleRestart();
    };

    if (typeof connection.onclose === 'function') {
      connection.onclose(handler);
    } else if ('onclose' in connection) {
      connection.onclose = handler;
    } else {
      // No onclose hook exposed; rely on promise rejection in start()
    }
  };
  attachOnClose();

  connection.on("callUpdated", (leadId, callCount) => {
    if (!leadId) return;
    listeners.call.forEach(fn => fn(leadId, callCount));
  });

  connection.on("leadDeleted", (leadId) => {
    if (!leadId) return;
    listeners.del.forEach(fn => fn(leadId));
  });

  connection.on("pageChanged", (pageKey, pageNumber) => {
    if (!pageKey) return;
    listeners.page.forEach(fn => fn(pageKey, pageNumber));
  });

  connection.on("orderChanged", (stageKey, orderedIds) => {
    if (!stageKey) return;
    listeners.reorder.forEach(fn => fn(stageKey, orderedIds || []));
  });

  connection.on("leadUpdated", (payload) => {
    if (!payload || !payload.leadId) return;
    listeners.update.forEach(fn => fn(payload));
  });

  function scheduleRestart() {
    attempt++;
    const delay = Math.min(1000 * Math.pow(2, attempt - 1), MAX_BACKOFF_MS);
    console.info(`[live-sync] manual restart in ${delay}ms (attempt ${attempt})`);
    setTimeout(start, delay);
  }

  function start() {
    connection.start()
      .then(() => {
        attempt = 0;
        console.info('[live-sync] connected');
        aiTrace('LiveSync.Connected');
      })
      .catch(err => {
        console.error('[live-sync] start failed', err);
        aiException(err, { phase: 'start', attempt });
        scheduleRestart();
      });
  }

  start();

  window.liveSync = {
    onCall(fn) { listeners.call.push(fn); },
    onDelete(fn) { listeners.del.push(fn); },
    onPage(fn) { listeners.page.push(fn); },
    onReorder(fn) { listeners.reorder.push(fn); },
    onUpdate(fn) { listeners.update.push(fn); },
    sendCall(leadId, callCount) {
      if (!leadId) return;
      connection.invoke("BroadcastCall", leadId, callCount).catch(err => {
        console.error('[live-sync] BroadcastCall failed', err);
        aiException(err, { method: 'BroadcastCall' });
      });
    },
    sendDelete(leadId) {
      if (!leadId) return;
      connection.invoke("BroadcastDelete", leadId).catch(err => {
        console.error('[live-sync] BroadcastDelete failed', err);
        aiException(err, { method: 'BroadcastDelete' });
      });
    },
    sendPage(pageKey, pageNumber) {
      if (!pageKey) return;
      connection.invoke("BroadcastPage", pageKey, pageNumber).catch(err => {
        console.error('[live-sync] BroadcastPage failed', err);
        aiException(err, { method: 'BroadcastPage' });
      });
    },
    sendReorder(stageKey, orderedIds) {
      if (!stageKey || !Array.isArray(orderedIds)) return;
      connection.invoke("BroadcastOrder", stageKey, orderedIds).catch(err => {
        console.error('[live-sync] BroadcastOrder failed', err);
        aiException(err, { method: 'BroadcastOrder' });
      });
    },
    sendUpdate(payload) {
      if (!payload || !payload.leadId) return;
      connection.invoke("BroadcastUpdate", payload).catch(err => {
        console.error('[live-sync] BroadcastUpdate failed', err);
        aiException(err, { method: 'BroadcastUpdate' });
      });
    }
  };
})();
