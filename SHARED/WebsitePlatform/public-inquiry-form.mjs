export function publicInquiryForm({ preview = false, business = false, ownerLabel = null } = {}) {
  const previewAttrs = preview ? ' data-preview' : '';
  const disabled = preview ? ' disabled' : '';
  const safeOwner = ownerLabel
    ? escapeHtml(ownerLabel)
    : business
      ? '<span data-business-name>this business</span>'
      : 'LEGEND®';
  const notice = preview
    ? '<p data-preview-notice>Inquiries become available on your verified published domain.</p>'
    : '';

  return `<form id="website_inquiry" class="public-form" data-website-inquiry data-form-key="website_inquiry"${previewAttrs} action="/api/website-inquiries/public" method="post"><fieldset${disabled}><legend>Send an inquiry</legend><div class="public-form-grid"><label>First Name<input name="FirstName" autocomplete="given-name" maxlength="120" required></label><label>Last Name<input name="LastName" autocomplete="family-name" maxlength="120" required></label><label>Phone Number<input name="Phone" type="tel" inputmode="tel" autocomplete="tel" maxlength="64" required></label><label>Email<input name="Email" type="email" autocomplete="email" maxlength="254" required></label><label class="public-form-full">Message<textarea rows="5" name="Message" maxlength="12000" required></textarea></label><label class="public-form-consent public-form-full"><input type="checkbox" name="consent" required><span>I agree to share this inquiry with ${safeOwner}.</span></label></div><button class="btn primary" type="submit">Send inquiry</button></fieldset>${notice}<p role="status" aria-live="polite"></p></form>`;
}

export function mountPublicInquiryForms(root = document) {
  if (!root?.querySelectorAll) return 0;
  let mounted = 0;
  root.querySelectorAll('[data-legend-public-inquiry-form]').forEach(host => {
    if (host.dataset.inquiryMounted === 'true') return;
    host.innerHTML = publicInquiryForm({
      preview: host.dataset.preview === 'true',
      business: host.dataset.business === 'true',
      ownerLabel: host.dataset.ownerLabel || null
    });
    host.dataset.inquiryMounted = 'true';
    mounted += 1;
  });
  return mounted;
}

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  window.LegendPublicInquiryForm = Object.freeze({ publicInquiryForm, mountPublicInquiryForms });
  let runtimePromise = null;
  const ensureRuntime = async () => {
    mountPublicInquiryForms(document);
    if (!document.querySelector('[data-website-inquiry]:not([data-preview])')) return;
    runtimePromise ||= import(new URL('./legend-public-inquiry.js', import.meta.url).href);
    await runtimePromise;
  };
  const start = () => { void ensureRuntime(); };
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
  window.addEventListener('legend:website-content-rendered', start);
}
