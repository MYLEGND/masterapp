(() => {
  "use strict";
  const escape = value => String(value ?? "").replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character]));
  function render({ id, name, phone = "", starred = false, stageClass = "", productionHtml = "", appointmentHtml = "" }) {
    const digits = phone.replace(/\D/g, "");
    const displayPhone = digits.length === 10 ? `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}` : phone;
    const displayName = name || (digits ? `Contact · ${digits.slice(-4)}` : "Unnamed contact");
    return `<article class="client-card ${escape(stageClass)} ${starred ? "is-starred" : ""}" draggable="true"
      data-cardid="${escape(id)}" data-open-card="${escape(id)}" role="button" tabindex="0" aria-label="Open Quick View for ${escape(displayName)}">
      <div class="client-card-head">
        <div class="client-card-main">
          <h3 class="cc-name" data-open-card="${escape(id)}">${escape(displayName)}</h3>
          <div class="cc-sub cc-sub-primary">${phone ? `<a class="link link-phone" href="tel:${escape(phone)}">${escape(displayPhone)}</a><a class="crm-card-text" href="sms:${escape(phone)}" aria-label="Text ${escape(displayName)}">Text</a>` : '<span class="muted">No phone</span>'}</div>
        </div>
        <button type="button" class="crm-star-toggle ${starred ? "is-starred" : ""}" data-star-contact="${escape(id)}"
          draggable="false" aria-pressed="${starred}" aria-label="${starred ? "Unstar" : "Star"} ${escape(displayName)}"
          title="${starred ? "Unstar contact" : "Keep contact at top"}">★</button>
      </div>
      <div class="lead-prod-badge ${productionHtml ? "" : "hidden"}" data-prod-card data-card-prod="${escape(id)}">${productionHtml}</div>
      ${appointmentHtml}
    </article>`;
  }
  window.LegendCrmContactCard = Object.freeze({ render });
})();
