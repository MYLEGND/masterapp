export function websiteReceptionist({ preview = false, label = null } = {}) {
  const previewAttrs = preview ? ' data-preview' : '';
  const disabled = preview ? ' disabled' : '';
  const safeLabel = escapeHtml(typeof label === 'string' && label.trim() ? label.trim().slice(0, 160) : 'Speak inside the website');
  return `<section class="public-receptionist" data-website-receptionist${previewAttrs}><h2>Speak inside the website</h2><p>Talk with our AI receptionist without leaving this page. Voice availability is checked when you choose to speak. Microphone access is optional; you can send a written inquiry on this page.</p><button class="btn primary" type="button" data-receptionist-start data-cms-field-key="receptionist_start"${disabled}>${safeLabel}</button><button class="btn" type="button" data-receptionist-end hidden>End conversation</button><audio data-receptionist-audio controls autoplay hidden></audio><p data-receptionist-status aria-live="polite"></p></section>`;
}

// Only explicit canonical placeholders are mounted. Existing forms and their
// customized controls, consent and submission state are never replaced.
export function mountWebsiteReceptionists(root = document) {
  if (!root?.querySelectorAll) return 0;
  let mounted = 0;
  root.querySelectorAll('[data-legend-receptionist-entry]').forEach(host => {
    if (host.querySelector('[data-website-receptionist]')) return;
    host.innerHTML = websiteReceptionist({preview:host.hasAttribute('data-preview'),label:host.dataset.receptionistLabel});
    mounted += 1;
  });
  return mounted;
}

export function publicInquiryForm({ preview = false, business = false, ownerLabel = null } = {}) {
  const previewAttrs = preview ? ' data-preview' : '';
  const disabled = preview ? ' disabled' : '';
  const safeOwner = ownerLabel
    ? escapeHtml(ownerLabel)
    : business
      ? '<span data-business-name>this business</span>'
      : 'this website';
  const notice = preview
    ? '<p data-preview-notice>Inquiries become available on your verified published domain.</p>'
    : '';

  return `${websiteReceptionist({preview})}<form id="website_inquiry" class="public-form" data-website-inquiry data-form-key="website_inquiry"${previewAttrs} action="/api/website-inquiries/public" method="post"><fieldset${disabled}><legend>Send an inquiry</legend><div class="public-form-grid"><label>First Name<input name="FirstName" autocomplete="given-name" maxlength="120" required></label><label>Last Name<input name="LastName" autocomplete="family-name" maxlength="120" required></label><label>Phone Number<input name="Phone" type="tel" inputmode="tel" autocomplete="tel" maxlength="64" required></label><label>Email<input name="Email" type="email" autocomplete="email" maxlength="254" required></label><label class="public-form-full">Message<textarea rows="5" name="Message" maxlength="12000" required></textarea></label><label class="public-form-consent public-form-full"><input type="checkbox" name="consent" required><span>I agree to share this inquiry with ${safeOwner}.</span></label></div><button class="btn primary" type="submit">Send inquiry</button></fieldset>${notice}<p role="status" aria-live="polite"></p></form>`;
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
  window.LegendPublicInquiryForm = Object.freeze({ publicInquiryForm, mountPublicInquiryForms, websiteReceptionist, mountWebsiteReceptionists });
  let runtimePromise = null;
  const ensureRuntime = async () => {
    mountPublicInquiryForms(document);
    const mounted = mountWebsiteReceptionists(document);
    if (mounted) window.dispatchEvent(new CustomEvent('legend:public-inquiry-mounted'));
    if (!document.querySelector(
      '[data-website-inquiry]:not([data-preview]),' +
      '[data-website-experience-form][data-submit-capability="lead_capture"]:not([data-preview])'
    )) return;
    // Older immutable publications already load the same handler as a classic
    // script. Notify that owner rather than importing a second voice lifecycle.
    const existingRuntime = [...document.scripts].some(script => {
      const source = script.getAttribute('src');
      return source && ['/legend-public-inquiry.js','/js/legend-public-inquiry.js'].includes(new URL(source, location.href).pathname);
    });
    if (existingRuntime) {
      if (mounted) window.dispatchEvent(new CustomEvent('legend:website-content-rendered'));
      return;
    }
    const moduleUrl = new URL(import.meta.url);
    const moduleScript = [...document.scripts].find(script =>
      script.src && new URL(script.src, location.href).href === moduleUrl.href);
    const runtimeUrl = new URL('./legend-public-inquiry.js', moduleUrl);
    const boundUrl = moduleScript?.getAttribute('data-inquiry-runtime');
    if (boundUrl) {
      const binding = new URL(boundUrl, location.href);
      if (binding.origin === runtimeUrl.origin && binding.pathname === runtimeUrl.pathname)
        runtimeUrl.search = binding.search;
    }
    runtimePromise ||= import(runtimeUrl.href);
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
