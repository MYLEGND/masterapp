(function () {
  const recordTypeRadios = Array.from(document.querySelectorAll('input[name="RecordType"]'));
  const submitBtn = document.getElementById("submitCreateBtn");
  const pipelineStage = document.getElementById("PipelineStage");
  const crmStatus = document.getElementById("CrmStatus");
  const status = document.getElementById("MaritalStatus");
  const soCard = document.getElementById("soCard");
  const soFirst = document.getElementById("SignificantOtherFirstName");
  const soLast = document.getElementById("SignificantOtherLastName");
  const soDob = document.getElementById("SignificantOtherDOB");
  const requiredForClient = Array.from(document.querySelectorAll("[data-client-required]"));
  const accountManagementCard = document.getElementById("accountManagementCard");
  const accountManagementInputs = Array.from(document.querySelectorAll('input[name="AccountManagementMode"]'));
  const subscriptionCard = document.getElementById("subscriptionCard");
  const subscriptionPriceType = document.getElementById("SubscriptionPriceType");
  const subscriptionCustomAmount = document.getElementById("SubscriptionCustomMonthlyAmount");
  const subscriptionCustomAmountWrap = document.getElementById("subscriptionCustomAmountWrap");
  const subscriptionAnchorMode = document.getElementById("SubscriptionBillingAnchorMode");
  const subscriptionAnchorDay = document.getElementById("SubscriptionBillingAnchorDay");
  const subscriptionAnchorDayWrap = document.getElementById("subscriptionAnchorDayWrap");
  const subscriptionCurrency = document.getElementById("SubscriptionCurrency");

  // Ensure a default selection (Lead) so required radios don't block submit silently
  if (!recordTypeRadios.some(r => r.checked)) {
    const leadRadio = recordTypeRadios.find(r => r.value === "Lead");
    if (leadRadio) leadRadio.checked = true;
  }

  function isPortalRecordType(value) {
    return value === "Client" || value === "BusinessClient";
  }

  function needsSO(value) {
    if (!value) return false;

    const normalized = value.toLowerCase();
    return normalized === "married" || normalized === "domestic partnership";
  }

  function getFieldLabel(field) {
    if (!field?.id) {
      return null;
    }

    return field.labels?.[0] || document.querySelector(`label[for="${field.id}"]`);
  }

  function setRequiredState(field, required) {
    if (!field) {
      return;
    }

    field.required = required;
    field.toggleAttribute("aria-required", required);
    getFieldLabel(field)?.classList.toggle("is-required", required);
  }

  function setFieldState(field, options) {
    if (!field) {
      return;
    }

    const required = Boolean(options?.required);
    const enabled = options?.enabled !== false;
    const clearWhenDisabled = Boolean(options?.clearWhenDisabled);

    setRequiredState(field, required);
    field.disabled = !enabled;

    if (!enabled && clearWhenDisabled) {
      field.value = "";
    }
  }

  function submitLabel(value) {
    if (value === "BusinessClient") return "Create Business Client";
    if (value === "Client") return "Create Client";
    return "Create Lead";
  }

  function defaultPipelineStage(value) {
    if (value === "BusinessClient") return "BusinessClient";
    if (value === "Client") return "Client";
    return "NewLead";
  }

  function applyRecordType() {
    const selected = recordTypeRadios.find((x) => x.checked)?.value || "Lead";
    const isClient = isPortalRecordType(selected);
    const requiresSignificantOther = isClient && needsSO(status ? status.value : "");
    const useCustomAmount = isClient && subscriptionPriceType?.value === "Custom";
    const useAnchorDay = isClient && subscriptionAnchorMode?.value === "SpecificDayOfMonth";

    if (submitBtn) submitBtn.textContent = submitLabel(selected);

    requiredForClient.forEach((element) => {
      setRequiredState(element, isClient);
    });

    if (pipelineStage) {
      if (isClient) {
        pipelineStage.value = defaultPipelineStage(selected);
        pipelineStage.setAttribute("disabled", "disabled");
      } else {
        pipelineStage.removeAttribute("disabled");
        if (pipelineStage.value === "Client" || pipelineStage.value === "BusinessClient") {
          pipelineStage.value = "NewLead";
        }
      }
    }

    if (crmStatus && isClient && (crmStatus.value === "Lead" || crmStatus.value === "Prospect")) {
      crmStatus.value = "Active";
    }

    if (subscriptionCard) {
      subscriptionCard.classList.toggle("is-hidden", !isClient);
    }

    if (accountManagementCard) {
      accountManagementCard.classList.toggle("is-hidden", !isClient);
    }
    accountManagementInputs.forEach((input) => {
      input.disabled = !isClient;
      input.required = isClient;
      input.toggleAttribute("aria-required", isClient);
    });

    if (subscriptionCurrency && !subscriptionCurrency.value) {
      subscriptionCurrency.value = "USD";
    }

    if (subscriptionCustomAmountWrap) {
      subscriptionCustomAmountWrap.classList.toggle("is-hidden", !useCustomAmount);
    }
    setFieldState(subscriptionCustomAmount, {
      required: useCustomAmount,
      enabled: useCustomAmount,
      clearWhenDisabled: true
    });

    if (soCard) {
      soCard.classList.toggle("is-hidden", !requiresSignificantOther);
    }
    setFieldState(soFirst, {
      required: requiresSignificantOther,
      enabled: requiresSignificantOther,
      clearWhenDisabled: true
    });
    setFieldState(soLast, {
      required: requiresSignificantOther,
      enabled: requiresSignificantOther,
      clearWhenDisabled: true
    });
    setFieldState(soDob, {
      required: requiresSignificantOther,
      enabled: requiresSignificantOther,
      clearWhenDisabled: true
    });

    if (subscriptionAnchorDayWrap) {
      subscriptionAnchorDayWrap.classList.toggle("is-hidden", !useAnchorDay);
    }
    setFieldState(subscriptionAnchorDay, {
      required: useAnchorDay,
      enabled: useAnchorDay,
      clearWhenDisabled: true
    });
  }

  recordTypeRadios.forEach((radio) => radio.addEventListener("change", applyRecordType));
  if (status) {
    status.addEventListener("change", applyRecordType);
  }
  if (subscriptionPriceType) {
    subscriptionPriceType.addEventListener("change", applyRecordType);
  }
  if (subscriptionAnchorMode) {
    subscriptionAnchorMode.addEventListener("change", applyRecordType);
  }
  applyRecordType();
})();

(function () {
  const lastTouch = document.getElementById("CrmLastTouch");
  const nextDate = document.getElementById("CrmNextDate");
  const nextText = document.getElementById("CrmNextText");
  const tags = document.getElementById("CrmTags");
  const notes = document.getElementById("CrmNotes");
  const status = document.getElementById("CrmStatus");
  const priority = document.getElementById("CrmPriority");

  const touchBtn = document.getElementById("btnCrmTouchToday");
  const nextBtn = document.getElementById("btnCrmNextToday");
  const clearBtn = document.getElementById("btnCrmClear");

  function todayISO() {
    const date = new Date();
    const tzDate = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    return tzDate.toISOString().slice(0, 10);
  }

  if (touchBtn) {
    touchBtn.addEventListener("click", () => {
      if (lastTouch) lastTouch.value = todayISO();
    });
  }

  if (nextBtn) {
    nextBtn.addEventListener("click", () => {
      if (nextDate) nextDate.value = todayISO();
    });
  }

  if (clearBtn) {
    clearBtn.addEventListener("click", () => {
      if (status) status.value = "Lead";
      if (priority) priority.value = "Normal";
      if (lastTouch) lastTouch.value = "";
      if (nextDate) nextDate.value = "";
      if (nextText) nextText.value = "";
      if (tags) tags.value = "";
      if (notes) notes.value = "";
    });
  }
})();
