(function () {
  const root = document.querySelector("[data-subscription-activation]");
  if (!root) return;

  const form = document.getElementById("activationPaymentForm");
  const messageEl = document.getElementById("activationMessage");
  const sourceInput = document.getElementById("activationSourceId");
  const squareAppId = root.getAttribute("data-square-app-id");
  const squareLocationId = root.getAttribute("data-square-location-id");
  const submitButton = document.getElementById("activationSubmitButton");

  if (!form || !submitButton) return;

  let card = null;
  let submitting = false;

  function setMessage(message) {
    if (messageEl) {
      messageEl.textContent = message || "";
      messageEl.hidden = !message;
    }
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

  form.addEventListener("submit", async (event) => {
    if (submitting) {
      event.preventDefault();
      return;
    }

    event.preventDefault();
    setMessage("");

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
      submitButton.textContent = "Activate access";
      setMessage("We could not secure the payment method. Please check the card details and try again.");
    }
  });

  mountSquareCard().catch(() => setMessage("Secure card entry could not be initialized."));
})();
