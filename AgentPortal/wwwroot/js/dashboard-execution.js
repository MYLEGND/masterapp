(() => {
  const token = document.querySelector('#dashboardAf input[name="__RequestVerificationToken"]')?.value || "";
  const toastEl = document.getElementById("dashboardToast");

  function showToast(message, error = false) {
    if (!toastEl) {
      if (error) console.error(message);
      else console.log(message);
      return;
    }
    toastEl.textContent = message || "Done";
    toastEl.classList.toggle("error", !!error);
    toastEl.classList.add("show");
    clearTimeout(window.__dashToastTimer);
    window.__dashToastTimer = setTimeout(() => toastEl.classList.remove("show"), error ? 4000 : 1800);
  }

  async function reloadSection(selector, url) {
    const container = document.querySelector(selector);
    if (!container) return;
    try {
      const res = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      container.innerHTML = await res.text();
    } catch (err) {
      container.innerHTML = '<div class="text-danger">Failed to refresh.</div>';
    }
  }

  async function completeAction(actionId) {
    if (!actionId) return;
    try {
      const res = await fetch("/Dashboard/CompleteAction", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "RequestVerificationToken": token
        },
        body: JSON.stringify({ id: actionId }),
        credentials: "include"
      });
      if (!res.ok) throw new Error(await res.text());
      await reloadSection("#todayContainer", "/Dashboard/Today");
      await reloadSection("#overdueContainer", "/Dashboard/Overdue");
      showToast("Action completed");
    } catch (err) {
      console.error(err);
      showToast("Failed to complete action", true);
    }
  }

  document.addEventListener("click", (event) => {
    const btn = event.target.closest(".action-complete-btn");
    if (btn) {
      event.preventDefault();
      const id = btn.getAttribute("data-action-id");
      completeAction(id);
    }
  });
})();
