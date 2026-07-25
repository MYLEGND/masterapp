(function () {
  function initializeSecurePaymentForm(form) {
    const root = form.closest("[data-subscription-activation]") || form;
    const messageEl = form.querySelector("[data-square-message]");
    const sourceInput = form.querySelector("[data-square-source]");
    const cardContainer = form.querySelector("[data-square-card]");
    const submitButton = form.querySelector("[data-square-submit]");
    const squareAppId = root.getAttribute("data-square-app-id");
    const squareLocationId = root.getAttribute("data-square-location-id");

    if (!sourceInput || !cardContainer || !submitButton || !squareAppId || !squareLocationId) return;

    let card = null;
    let submitting = false;
    const submitLabel = submitButton.textContent;

    function setMessage(message) {
      if (messageEl) {
        messageEl.textContent = message || "";
        messageEl.hidden = !message;
      }
    }

    async function mountSquareCard() {
      if (!window.Square) {
        setMessage("Secure card entry is not available right now.");
        submitButton.disabled = true;
        return;
      }

      try {
        const payments = window.Square.payments(squareAppId, squareLocationId);
        card = await payments.card();
        await card.attach(`#${cardContainer.id}`);
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
      submitButton.textContent = "Securing payment method...";
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
        submitButton.textContent = submitLabel;
        setMessage("We could not secure the payment method. Please check the card details and try again.");
      }
    });

    mountSquareCard().catch(() => setMessage("Secure card entry could not be initialized."));
  }

  document.querySelectorAll("form[data-square-payment-form]").forEach(initializeSecurePaymentForm);
})();
