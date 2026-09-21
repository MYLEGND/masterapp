(() => {
  'use strict';
  document.querySelectorAll('[data-business-inquiry]:not([data-preview])').forEach(form => {
    let submissionId = null;
    let pendingPayload = null;
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!form.reportValidity()) return;
      const button = form.querySelector('[type="submit"]');
      const status = form.querySelector('[role="status"]');
      const fields = new FormData(form);
      const values = {name:String(fields.get('name')||''),email:String(fields.get('email')||''),message:String(fields.get('message')||''),sourcePath:location.pathname,consent:fields.get('consent')==='on'};
      const fingerprint = JSON.stringify(values);
      if (pendingPayload !== fingerprint) { submissionId=crypto.randomUUID(); pendingPayload=fingerprint; }
      button.disabled = true;
      status.textContent = 'Sending your inquiry…';
      try {
        const response = await fetch('/api/website-inquiries/public', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({submissionId,...values})});
        if (!response.ok || !(await response.json()).accepted) throw new Error('inquiry_failed');
        status.textContent = 'Your inquiry has been sent.';
        form.reset(); submissionId=null; pendingPayload=null;
      } catch {
        status.textContent = 'Your inquiry could not be confirmed. Please try again.';
      } finally { button.disabled=false; }
    });
  });
})();
