(() => {
  if (!window.signalR) return;

  const listeners = { call: [], del: [], page: [], reorder: [], update: [] };

  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/livesync")
    .withAutomaticReconnect()
    .build();

  connection.on("callUpdated", (leadId, callCount) => {
    listeners.call.forEach(fn => fn(leadId, callCount));
  });

  connection.on("leadDeleted", (leadId) => {
    listeners.del.forEach(fn => fn(leadId));
  });

  connection.on("pageChanged", (pageKey, pageNumber) => {
    listeners.page.forEach(fn => fn(pageKey, pageNumber));
  });

  connection.on("orderChanged", (stageKey, orderedIds) => {
    listeners.reorder.forEach(fn => fn(stageKey, orderedIds || []));
  });

  connection.on("leadUpdated", (payload) => {
    listeners.update.forEach(fn => fn(payload || {}));
  });

  const start = () => connection.start().catch(() => {
    setTimeout(start, 2000);
  });
  start();

  window.liveSync = {
    onCall(fn) { listeners.call.push(fn); },
    onDelete(fn) { listeners.del.push(fn); },
    onPage(fn) { listeners.page.push(fn); },
    onReorder(fn) { listeners.reorder.push(fn); },
    onUpdate(fn) { listeners.update.push(fn); },
    sendCall(leadId, callCount) {
      if (!leadId) return;
      connection.invoke("BroadcastCall", leadId, callCount).catch(() => {});
    },
    sendDelete(leadId) {
      if (!leadId) return;
      connection.invoke("BroadcastDelete", leadId).catch(() => {});
    },
    sendPage(pageKey, pageNumber) {
      if (!pageKey) return;
      connection.invoke("BroadcastPage", pageKey, pageNumber).catch(() => {});
    },
    sendReorder(stageKey, orderedIds) {
      if (!stageKey || !Array.isArray(orderedIds)) return;
      connection.invoke("BroadcastOrder", stageKey, orderedIds).catch(() => {});
    },
    sendUpdate(payload) {
      if (!payload || !payload.leadId) return;
      connection.invoke("BroadcastUpdate", payload).catch(() => {});
    }
  };
})();
