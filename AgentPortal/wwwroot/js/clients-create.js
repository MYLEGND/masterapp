(function () {
  const status = document.getElementById("MaritalStatus");
  const soCard = document.getElementById("soCard");
  const soFirst = document.getElementById("SignificantOtherFirstName");
  const soLast = document.getElementById("SignificantOtherLastName");
  const soDob = document.getElementById("SignificantOtherDOB");
  const recordTypeRadios = Array.from(document.querySelectorAll('input[name="RecordType"]'));
  const soChips = Array.from(document.querySelectorAll("[data-so-chip]"));

  function isPortalRecordType(value) {
    return value === "Client" || value === "BusinessClient";
  }

  function needsSO(value) {
    if (!value) return false;
    value = value.toLowerCase();
    return value === "married" || value === "domestic partnership";
  }

  function applySO() {
    const selected = recordTypeRadios.find((x) => x.checked)?.value || "Lead";
    const required = isPortalRecordType(selected) && needsSO(status ? status.value : "");

    if (soCard) soCard.style.display = required ? "" : "none";
    if (soFirst) soFirst.required = required;
    if (soLast) soLast.required = required;
    if (soDob) soDob.required = required;
    soChips.forEach((chip) => chip.classList.toggle("is-on", required));

    if (!required) {
      if (soFirst) soFirst.value = "";
      if (soLast) soLast.value = "";
      if (soDob) soDob.value = "";
    }
  }

  if (status) {
    status.addEventListener("change", applySO);
  }

  recordTypeRadios.forEach((radio) => radio.addEventListener("change", applySO));
  applySO();
})();

(function () {
  const recordTypeRadios = Array.from(document.querySelectorAll('input[name="RecordType"]'));
  const submitBtn = document.getElementById("submitCreateBtn");
  const pipelineStage = document.getElementById("PipelineStage");
  const crmStatus = document.getElementById("CrmStatus");
  const requiredForClient = Array.from(document.querySelectorAll("[data-client-required]"));
  const clientChips = Array.from(document.querySelectorAll("[data-client-chip]"));

  // Ensure a default selection (Lead) so required radios don't block submit silently
  if (!recordTypeRadios.some(r => r.checked)) {
    const leadRadio = recordTypeRadios.find(r => r.value === "Lead");
    if (leadRadio) leadRadio.checked = true;
  }

  function isPortalRecordType(value) {
    return value === "Client" || value === "BusinessClient";
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

    if (submitBtn) submitBtn.textContent = submitLabel(selected);

    requiredForClient.forEach((element) => {
      element.required = isClient;
      element.toggleAttribute("aria-required", isClient);
    });
    clientChips.forEach((chip) => chip.classList.toggle("is-on", isClient));

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
  }

  recordTypeRadios.forEach((radio) => radio.addEventListener("change", applyRecordType));
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
