(function () {
  const recordTypeRadios = Array.from(document.querySelectorAll('input[name="RecordType"]'));
  const submitBtn = document.getElementById("submitCreateBtn");
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
  const subscriptionHasFreeTrial = document.getElementById("SubscriptionHasFreeTrial");
  const subscriptionFreeTrialDays = document.getElementById("SubscriptionFreeTrialDays");
  const subscriptionFreeTrialDaysWrap = document.getElementById("subscriptionFreeTrialDaysWrap");
  const subscriptionCurrency = document.getElementById("SubscriptionCurrency");

  const ownerRows = document.getElementById('businessAdditionalOwners');
  const ownerLookupUrl = ownerRows?.dataset.ownerLookupUrl || '';
  const primaryEmailLabel = document.getElementById('primaryEmailLabel');
  const businessProfileEmailHelp = document.getElementById('businessProfileEmailHelp');

  function clearOwnerIdentity(row, message) {
    const id = row?.querySelector('[data-owner-field="ClientProfileId"]');
    const first = row?.querySelector('[data-owner-first-name]');
    const last = row?.querySelector('[data-owner-last-name]');
    const statusElement = row?.querySelector('[data-owner-status]');
    if (id) id.value = '';
    if (first) first.value = '';
    if (last) last.value = '';
    if (statusElement) statusElement.textContent = message || '';
  }

  async function resolveOwner(row) {
    const email = row?.querySelector('[data-owner-email]')?.value?.trim() || '';
    if (!email || !ownerLookupUrl) {
      clearOwnerIdentity(row, email ? 'Owner lookup is unavailable.' : '');
      return;
    }

    clearOwnerIdentity(row, 'Checking linked client…');
    try {
      const response = await fetch(`${ownerLookupUrl}?${new URLSearchParams({ email })}`, {
        method: 'GET',
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' }
      });
      if (!response.ok) {
        clearOwnerIdentity(row, 'No eligible linked client was found for this email.');
        return;
      }

      const owner = await response.json();
      const id = row.querySelector('[data-owner-field="ClientProfileId"]');
      const first = row.querySelector('[data-owner-first-name]');
      const last = row.querySelector('[data-owner-last-name]');
      const statusElement = row.querySelector('[data-owner-status]');
      if (id) id.value = owner.clientProfileId || owner.ClientProfileId || '';
      if (first) first.value = owner.firstName || owner.FirstName || '';
      if (last) last.value = owner.lastName || owner.LastName || '';
      if (statusElement) statusElement.textContent = 'Linked client verified.';
    } catch {
      clearOwnerIdentity(row, 'Owner lookup could not be completed.');
    }
  }

  function reindexOwners() {
    ownerRows?.querySelectorAll('[data-business-owner]').forEach((row, index) => {
      row.querySelectorAll('[data-owner-field]').forEach(input => {
        input.name = `BusinessOwners[${index}].${input.dataset.ownerField}`;
      });
    });
  }

  document.getElementById('addBusinessOwner')?.addEventListener('click', () => {
    if (!ownerRows || ownerRows.children.length >= 20) return;
    const row = document.createElement('div');
    row.dataset.businessOwner = '';
    row.innerHTML = '<input type="hidden" data-owner-field="ClientProfileId"><label>Owner email <input type="email" class="client-create-input" required data-owner-field="Email" data-owner-email></label><label>First name <input type="text" class="client-create-input" readonly aria-readonly="true" data-owner-first-name></label><label>Last name <input type="text" class="client-create-input" readonly aria-readonly="true" data-owner-last-name></label><label>Share (%) <input type="number" min="0.01" max="100" step="0.01" class="client-create-input" required data-owner-field="Percentage"></label><p class="client-create-section-copy" data-owner-status aria-live="polite"></p><button type="button" data-remove-business-owner>Remove owner</button>';
    ownerRows.appendChild(row);
    reindexOwners();
  });

  ownerRows?.addEventListener('change', event => {
    const email = event.target.closest('[data-owner-email]');
    if (email) void resolveOwner(email.closest('[data-business-owner]'));
  });
  ownerRows?.addEventListener('click', event => {
    const button = event.target.closest('[data-remove-business-owner]');
    if (!button) return;
    button.closest('[data-business-owner]').remove();
    reindexOwners();
  });
  ownerRows?.querySelectorAll('[data-business-owner]').forEach(row => {
    if (row.querySelector('[data-owner-email]')?.value) void resolveOwner(row);
  });

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

  function applyRecordType() {
    const selected = recordTypeRadios.find((x) => x.checked)?.value || "Lead";
    const isClient = isPortalRecordType(selected);
    const businessFields = document.getElementById('businessIdentityFields');
    const isBusiness = selected === 'BusinessClient';
    if (businessFields) {
      businessFields.hidden = !isBusiness;
      businessFields.querySelectorAll('input').forEach(field => { field.disabled = !isBusiness; });
      setRequiredState(document.getElementById('EntityName'), isBusiness);
    }
    if (primaryEmailLabel) primaryEmailLabel.textContent = isBusiness ? 'Business Profile Email' : 'Email';
    businessProfileEmailHelp?.classList.toggle('is-hidden', !isBusiness);
    const requiresSignificantOther = isClient && needsSO(status ? status.value : "");
    const useCustomAmount = isClient && subscriptionPriceType?.value === "Custom";
    const useAnchorDay = isClient && subscriptionAnchorMode?.value === "SpecificDayOfMonth";
    const useFreeTrial = isClient && subscriptionHasFreeTrial?.value === "true";

    if (submitBtn) submitBtn.textContent = submitLabel(selected);

    requiredForClient.forEach((element) => {
      setRequiredState(element, isClient);
    });

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

    setFieldState(subscriptionHasFreeTrial, {
      required: isClient,
      enabled: isClient,
      clearWhenDisabled: true
    });

    if (subscriptionFreeTrialDaysWrap) {
      subscriptionFreeTrialDaysWrap.classList.toggle("is-hidden", !useFreeTrial);
    }
    setFieldState(subscriptionFreeTrialDays, {
      required: useFreeTrial,
      enabled: useFreeTrial,
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
  if (subscriptionHasFreeTrial) {
    subscriptionHasFreeTrial.addEventListener("change", applyRecordType);
  }
  applyRecordType();
})();
