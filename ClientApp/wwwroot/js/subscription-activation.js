(function () {
  const root = document.querySelector("[data-subscription-activation]");
  if (!root) return;

  const form = document.getElementById("activationPaymentForm");
  const messageEl = document.getElementById("activationMessage");
  const sourceInput = document.getElementById("activationSourceId");
  const firstChargeEl = root.querySelector("[data-first-charge]");
  const firstRenewalEl = root.querySelector("[data-first-renewal]");
  const anchorLabelEl = root.querySelector("[data-anchor-label]");
  const authorizationCopyEl = document.getElementById("activationAuthorizationCopy");
  const anchorSelect = document.getElementById("billingAnchorDaySelect");
  const prepareUrl = root.getAttribute("data-prepare-url");
  const squareAppId = root.getAttribute("data-square-app-id");
  const squareLocationId = root.getAttribute("data-square-location-id");
  const submitButton = document.getElementById("activationSubmitButton");
  const antiforgeryInput = form?.querySelector('input[name="__RequestVerificationToken"]');

  if (!form || !submitButton) return;

  let card = null;
  let submitting = false;

  function setMessage(message) {
    if (messageEl) {
      messageEl.textContent = message || "";
      messageEl.hidden = !message;
    }
  }

  async function refreshSchedule() {
    if (!prepareUrl || !antiforgeryInput) return true;

    const body = new URLSearchParams();
    body.set("__RequestVerificationToken", antiforgeryInput.value);
    if (anchorSelect && anchorSelect.value) {
      body.set("BillingAnchorDay", anchorSelect.value);
    }

    const response = await fetch(prepareUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
        "X-Requested-With": "XMLHttpRequest"
      },
      body
    });

    if (!response.ok) {
      setMessage("We could not refresh the billing schedule right now.");
      return false;
    }

    const payload = await response.json();
    if (!payload.ok) {
      setMessage(payload.message || "The selected billing option is not available.");
      return false;
    }

    setMessage("");
    if (firstChargeEl) firstChargeEl.textContent = payload.firstChargeDateDisplay || "";
    if (firstRenewalEl) firstRenewalEl.textContent = payload.firstRecurringRenewalDateDisplay || "";
    if (anchorLabelEl) anchorLabelEl.textContent = payload.billingAnchorLabel || "";
    if (authorizationCopyEl && payload.billingAnchorLabel) {
      const amount = authorizationCopyEl.dataset.monthlyAmount || "the first";
      authorizationCopyEl.textContent = `I authorize today’s ${amount} charge and recurring monthly billing on ${payload.billingAnchorLabel}, including saving this card for future payments; cancellations can be requested after sign-in.`;
    }
    return true;
  }

  async function mountSquareCard() {
    if (!window.Square || !squareAppId || !squareLocationId) {
      setMessage("Secure card entry is not available right now.");
      submitButton.disabled = true;
      return;
    }

    try {
      const payments = window.Square.payments(squareAppId, squareLocationId);
      card = await payments.card();
      await card.attach("#square-card");
    } catch (error) {
      setMessage("Secure card entry could not be initialized.");
      submitButton.disabled = true;
    }
  }

  anchorSelect?.addEventListener("change", () => {
    refreshSchedule().catch(() => setMessage("We could not refresh the billing schedule right now."));
  });

  form.addEventListener("submit", async (event) => {
    if (submitting) {
      event.preventDefault();
      return;
    }

    event.preventDefault();
    setMessage("");

    const scheduleOk = await refreshSchedule();
    if (!scheduleOk) return;

    if (!card) {
      setMessage("Secure card entry is not ready yet.");
      return;
    }

    submitting = true;
    submitButton.disabled = true;
    submitButton.textContent = "Activating...";

    try {
      const result = await card.tokenize();
      if (result.status !== "OK" || !result.token) {
        throw new Error("Tokenization failed");
      }

      sourceInput.value = result.token;
      form.submit();
    } catch (error) {
      submitting = false;
      submitButton.disabled = false;
      submitButton.textContent = "Activate Subscription";
      setMessage("We could not secure the payment method. Please check the card details and try again.");
    }
  });

  mountSquareCard().catch(() => setMessage("Secure card entry could not be initialized."));
})();
