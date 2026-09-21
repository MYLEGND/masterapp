(() => {
  'use strict';
  const el = (tag, text, attributes = {}) => {
    const node = document.createElement(tag);
    if (text !== null) node.textContent = text;
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    return node;
  };
  const button = (text, action, primary = false) => {
    const node = el('button', text, { type: 'button', ...(primary ? { class: 'wm-primary' } : {}) });
    node.addEventListener('click', action); return node;
  };
  document.querySelectorAll('[data-website-manage]').forEach(trigger => trigger.addEventListener('click', async () => {
    const parentModal = trigger.closest('.modal');
    const parentInstance = parentModal && window.bootstrap?.Modal.getInstance(parentModal);
    parentInstance?.hide();
    const dialog = el('dialog', null, { class: 'legend-website-manager', 'aria-label': `${trigger.dataset.title} management` });
    const header = el('header', null), heading = el('div', null);
    heading.append(el('span', 'Website workspace', { class: 'legend-website-eyebrow' }), el('h2', trigger.dataset.title));
    header.append(heading, button('Close', () => dialog.close()));
    const body = el('div', null, { class: 'wm-body' }), status = el('p', 'Loading your website…', { role: 'status', 'aria-live': 'polite' });
    const panel = el('div', null); body.append(status, panel); dialog.append(header, body); document.body.append(dialog);
    dialog.addEventListener('close', () => { dialog.remove(); if (parentInstance) parentInstance.show(); else trigger.focus(); }); dialog.showModal();
    let session, state, busy = false;
    const request = async (path, payload) => {
      const url = new URL(`/api/website-content/manage${path}`, session.apiBase);
      const options = { cache: 'no-store', credentials: 'omit', headers: {} };
      if (payload) { options.method = 'POST'; options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify({ ...payload, ticket: session.ticket, expectedRevision: state?.revision }); }
      else url.searchParams.set('ticket', session.ticket);
      const response = await fetch(url, options);
      if (!response.ok) {
        let detail; try { detail = await response.json(); } catch { /* Server may return no JSON. */ }
        throw new Error(detail?.message || detail?.error || (response.status === 409 ? 'This website changed in another session. Close and reopen management before trying again.' : `The action could not complete (${response.status}).`));
      }
      return response.status === 204 ? null : response.json();
    };
    const run = async action => {
      if (busy) return; busy = true; status.textContent = 'Working…';
      dialog.querySelectorAll('button').forEach(n => n.disabled = true);
      try { await action(); } catch (error) { status.textContent = error.message; }
      finally { busy = false; dialog.querySelectorAll('button').forEach(n => n.disabled = false); }
    };
    const reload = async () => { state = await request(''); };
    const section = title => { panel.replaceChildren(button('← Overview', overview), el('h3', title)); status.textContent = ''; };
    const field = (name, type = 'text') => { const label = el('label', name), input = el('input', null, { type }); label.append(input); panel.append(label); return input; };
    const confirmAction = (title, description, action) => {
      section(title); panel.append(el('p', description), button(title, () => run(action), true));
    };
    const history = () => {
      section('Version history');
      if (!state.history?.length) panel.append(el('p', 'Your first publication will appear here.'));
      for (const version of state.history || []) {
        const row = el('div', null, { class: 'wm-row' });
        row.append(el('span', `Version ${version.revision} · ${version.createdUtc ? new Date(version.createdUtc).toLocaleString() : ''}`));
        if (state.capabilities?.canPublish === true) row.append(button('Restore', () => confirmAction('Restore version', 'Restore this published version as the live website and current draft. Export any unpublished draft changes first if you want to keep them.', async () => {
          await request('/rollback', { versionId: version.versionId ?? version.id }); await reload(); overview(); status.textContent = 'Version restored.';
        }))); panel.append(row);
      }
    };
    const readiness = () => {
      section('Launch readiness');
      const checks = state.readiness?.checks;
      if (!Array.isArray(checks) || !checks.length) {
        panel.append(el('p', 'No readiness checks are available yet. Save your draft, then reopen management to check it.')); return;
      }
      for (const check of checks) {
        const row = el('div', null, { class: 'wm-row' });
        row.append(el('span', typeof check === 'string' ? check : `${check.passed ? '✓' : 'Needs attention:'} ${check.message ?? check.name ?? check.code}`));
        if (typeof check !== 'string' && !check.passed) row.append(el('a', 'Review in editor', { href: trigger.dataset.edit }));
        panel.append(row);
      }
    };
    const migrationReport = result => {
      section('Import review');
      const report = result.report ?? result;
      panel.append(el('p', 'Content is saved as a draft. Review the migration findings before publishing.'),
        el('a', 'Review imported draft', { href: trigger.dataset.edit, class: 'wm-primary' }));
      if (report.sourceUrl) panel.append(el('p', `Original source: ${report.sourceUrl}`));
      if (report.createdUtc) panel.append(el('p', `Imported: ${new Date(report.createdUtc).toLocaleString()}`));
      panel.append(el('p', `${report.addedComponents ?? 0} components added · ${report.preservedComponents ?? 0} existing components preserved`));
      for (const warning of report.warnings ?? []) panel.append(el('p', `Needs review: ${warning}`));
      for (const page of report.pages ?? []) {
        const details = el('details', null); details.append(el('summary', page.title || page.url));
        details.append(el('p', `Source: ${page.url}`), el('p', `Retrieved: ${page.retrievedUtc ? new Date(page.retrievedUtc).toLocaleString() : 'Not recorded'}`));
        for (const warning of page.warnings ?? []) details.append(el('p', `Needs review: ${warning}`));
        for (const kind of ['links', 'images', 'forms']) details.append(el('p', `${kind}: ${page[kind]?.length ?? 0} found`));
        if (page.forms?.length) details.append(el('p', 'Review form destinations and integrations before launch.'));
        if (page.sha256) details.append(el('small', `Source fingerprint: ${page.sha256}`));
        panel.append(details);
      }
    };
    const dnsInstructions = (value, cnameTarget, hostname) => {
      let instructions = value;
      if (typeof value === 'string') { try { instructions = JSON.parse(value); } catch { instructions = {}; } }
      const records = [];
      if (instructions?.ownership) records.push(instructions.ownership);
      if (Array.isArray(instructions?.certificate)) records.push(...instructions.certificate);
      if (cnameTarget) records.push({ type: 'CNAME / ALIAS target', name: hostname, value: cnameTarget });
      if (!records.length) { panel.append(el('p', 'Verification instructions are not available yet. Refresh the domain status.')); return; }
      for (const record of records) {
        const type = record.type ?? record.recordType ?? 'TXT';
        const name = record.name ?? record.txt_name ?? record.hostname ?? '';
        const value = record.value ?? record.txt_value ?? record.target ?? '';
        const row = el('div', null, { class: 'wm-row' }); row.append(el('span', `${type} record · ${name}`));
        const input = el('input', null, { readonly: '', 'aria-label': `${type} record value` }); input.value = value; row.append(input);
        row.append(button('Copy value', async () => { try { await navigator.clipboard.writeText(value); status.textContent = 'DNS value copied.'; } catch { input.select(); status.textContent = 'Select and copy the DNS value.'; } })); panel.append(row);
      }
    };
    const importDraft = () => {
      section('Import into draft'); panel.append(el('p', 'Import an existing public website or authorized structured export. Review the imported draft before publishing. Your live website stays unchanged.'));
      const source = field('Original website address', 'url'), file = field('Website export JSON (optional)', 'file');
      const authorized = field('I own this website or have permission to import it', 'checkbox'); file.accept = 'application/json,.json';
      panel.append(button('Import draft', () => run(async () => {
        if (!authorized.checked) throw new Error('Confirm that you have permission to import this website.');
        if (!file.files[0] && !source.value) throw new Error('Enter the website address or choose an export.');
        if (file.files[0]?.size > 10 * 1024 * 1024) throw new Error('This export is too large to import here.');
        const imported = file.files[0] ? JSON.parse(await file.files[0].text()) : null;
        const report = await request('/import', { document: imported?.document ?? imported?.draft ?? imported, sourceUrl: source.value || null, authorized: true });
        await reload(); migrationReport(report); status.textContent = 'Import complete. Review the report below.';
      }), true));
    };
    const exportWebsite = () => run(async () => {
      const document = await request('/export');
      const blob = new Blob([JSON.stringify(document, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob), link = el('a', 'Download', { href: url, download: 'website-export.json' });
      documentDownload(link); URL.revokeObjectURL(url); status.textContent = 'Website export downloaded.';
    });
    const documentDownload = link => { document.body.append(link); link.click(); link.remove(); };
    const domains = () => run(async () => {
      section('Connected domains');
      const result = await request('/domains');
      for (const domain of result.domains ?? []) {
        const row = el('div', null, { class: 'wm-row' });
        row.append(el('span', `${domain.hostname} · ${domain.status} · HTTPS ${domain.certificateStatus}`), button('Verify status', () => run(async () => {
          const checked = await request('/domains/refresh', { bindingId: domain.id });
          status.textContent = `${checked.hostname ?? domain.hostname}: ${checked.status ?? 'Status refreshed'}`;
        })), button('Disconnect', () => confirmAction('Disconnect domain', `Remove ${domain.hostname} from this website. Your website content will remain saved.`, async () => {
          await request('/domains/remove', { bindingId: domain.id }); overview(); status.textContent = 'Domain disconnected.';
        })));
        panel.append(row);
        dnsInstructions(domain.verificationJson, result.cnameTarget, domain.hostname);
      }
      if (result.cnameTarget) panel.append(el('p', `Website CNAME target: ${result.cnameTarget}`));
      panel.append(
        el('p', 'Verify ownership before routing visitors. Keep your registrar and email records unchanged.'),
        el('p', 'If your registrar does not allow a CNAME at the root/apex domain, use its ALIAS, ANAME, or CNAME-flattening option with the same target, or connect a subdomain such as www.')
      );
      const domain = field('Domain name');
      panel.append(button('Connect domain', () => run(async () => {
        const result = await request('/domains', { hostname: domain.value.trim() });
        const binding = result.binding ?? result;
        dnsInstructions(binding.verificationJson, result.cnameTarget, binding.hostname ?? domain.value.trim());
        status.textContent = 'Add these records at your domain registrar, then refresh verification.';
      }), true)); status.textContent = '';
    });
    const inquiries = () => run(async () => {
      section('Business inquiries');
      const url = new URL('/api/website-inquiries/manage', session.apiBase); url.searchParams.set('ticket', session.ticket);
      const response = await fetch(url, { cache: 'no-store', credentials: 'omit' });
      if (!response.ok) throw new Error('Business inquiries could not be loaded.');
      const result = await response.json();
      const items = result.inquiries ?? result.items ?? [];
      if (!items.length) panel.append(el('p', 'No inquiries yet. Messages submitted through this business website will appear here.'));
      for (const inquiry of items) {
        const row = el('div', null, { class: 'wm-row' }); row.append(el('span', `${inquiry.name ?? inquiry.displayName} · ${inquiry.email}\n${inquiry.message}`));
        const select = el('select', null, { 'aria-label': 'Inquiry status' });
        for (const value of ['New', 'Contacted', 'Closed']) { const option = el('option', value, { value }); option.selected = inquiry.status === value; select.append(option); }
        row.append(select, button('Save status', () => run(async () => {
          const saved = await fetch(new URL('/api/website-inquiries/manage/status', session.apiBase), { method: 'POST', credentials: 'omit', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ticket: session.ticket, inquiryId: inquiry.id, status: select.value }) });
          if (!saved.ok) throw new Error('Inquiry status could not be saved.'); status.textContent = 'Inquiry updated.';
        }))); panel.append(row);
      }
      status.textContent = '';
    });
    const businessDetails = () => run(async () => {
      section('Business details');
      const result = await request('/business-details');
      if (typeof result.revision === 'number') state.revision = result.revision;
      panel.append(el('p', 'Keep public contact details, hours and services in one place. Save them to your draft, review the website, then publish. These fields do not change business ownership.'));
      const inputs = {};
      for (const [key, label, type] of [['contactEmail', 'Public contact email', 'email'], ['phone', 'Public phone', 'tel'], ['hours', 'Business hours', 'text'], ['locations', 'Locations', 'text'], ['services', 'Services', 'text']]) {
        inputs[key] = field(label, type); inputs[key].value = result.details?.[key] ?? '';
      }
      panel.append(button('Save business details', () => run(async () => {
        const details = Object.fromEntries(Object.entries(inputs).map(([key, input]) => [key, input.value.trim()]));
        if (inputs.contactEmail.value && !inputs.contactEmail.checkValidity()) throw new Error('Enter a valid public contact email.');
        await request('/business-details', { details }); await reload(); overview(); status.textContent = 'Business details saved to draft. Review and publish to update the website.';
      }), true)); status.textContent = '';
    });
    const usage = () => {
      section('Website usage');
      const value = state.usage;
      if (!value) { panel.append(el('p', 'Usage information is not available yet. Reopen management after saving your website.')); return; }
      for (const [label, count] of [['Media storage', `${(Number(value.mediaBytes ?? 0) / 1024 / 1024).toFixed(2)} MB`], ['Media files', value.mediaCount ?? 0], ['Published versions', value.publishedVersions ?? 0], ['Imported pages', value.importedPages ?? 0]]) {
        const row = el('div', null, { class: 'wm-row' }); row.append(el('span', label), el('strong', String(count))); panel.append(row);
      }
    };
    const schedule = () => {
      section('Schedule publication');
      panel.append(el('p', 'Schedule the current saved draft. Editing the draft afterward cancels its schedule so unexpected changes cannot go live.'));
      const current = state.schedule;
      if (current?.publishUtc) panel.append(el('p', `Scheduled for ${new Date(current.publishUtc).toLocaleString()} · ${current.status ?? (current.error ? 'Needs attention' : 'Scheduled')}`));
      if (current?.error) panel.append(el('p', `Needs attention: ${current.error}`));
      const date = field('Publish date and time (your local time)', 'datetime-local');
      panel.append(button('Schedule draft', () => run(async () => {
        const when = new Date(date.value);
        if (!date.value || !Number.isFinite(when.getTime()) || when.getTime() <= Date.now()) throw new Error('Choose a future publication time.');
        await request('/schedule', { publishUtc: when.toISOString() }); await reload(); overview(); status.textContent = 'Publication scheduled.';
      }), true));
      if (current?.publishUtc) panel.append(button('Cancel schedule', () => run(async () => {
        await request('/schedule', { publishUtc: null }); await reload(); overview(); status.textContent = 'Publication schedule canceled.';
      })));
    };
    const overview = () => {
      panel.replaceChildren(); status.textContent = `Draft revision ${state.revision ?? 0} · Published revision ${state.publishedRevision ?? 'Not published'}`;
      const links = el('div', null, { class: 'wm-row' });
      let editorHref = trigger.dataset.edit;
      if (trigger.dataset.scope === 'business' && session?.ticket) {
        const editorUrl = new URL(trigger.dataset.edit, location.origin);
        editorUrl.searchParams.set('legendEdit', session.ticket);
        editorHref = editorUrl.toString();
      }
      links.append(el('a', 'Open editor', { href: editorHref, class: 'wm-primary' }), el('a', 'View website ↗', { href: trigger.dataset.live, target: '_blank', rel: 'noopener' })); panel.append(links);
      const grid = el('div', null, { class: 'wm-grid' });
      const tile = (name, caption, action) => { const node = button(name, action); node.append(el('small', caption)); grid.append(node); };
      if (state.capabilities?.canPublish === true) tile('Publish draft', 'Review and make your saved draft live', () => confirmAction('Publish draft', 'Publish the complete saved draft as a new website version.', async () => { await request('/publish', {}); await reload(); overview(); status.textContent = 'Your website is published.'; }));
      if (state.capabilities?.canSchedule === true) tile('Schedule publication', 'Choose when the saved draft goes live', schedule);
      tile('Version history', 'Review publications and restore a version', history);
      tile('Launch readiness', 'Review what needs attention', readiness);
      if (state.capabilities?.canImport === true) tile('Import content', 'Bring an authorized export into draft', importDraft);
      tile('Website usage', 'Storage, media and publication history', usage);
      tile('Export website', 'Download your structured website content', exportWebsite);
      if (trigger.dataset.scope === 'business') { tile('Business details', 'Public contact details, services and locations', businessDetails); if (state.capabilities?.canManageDomains === true) tile('Domains', 'Connect and verify your business address', domains); tile('Inquiries', 'Manage customer messages for this business', inquiries); }
      panel.append(grid);
    };
    await run(async () => {
      const response = await fetch(trigger.dataset.session, { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) throw new Error('Your website permissions could not be verified. Please sign in again.');
      const bootstrap = await response.json();
      if (bootstrap?.handoffUrl && bootstrap?.state) {
        const exchange = await fetch(bootstrap.handoffUrl, {
          method: 'POST',
          cache: 'no-store',
          credentials: 'omit',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ state: bootstrap.state })
        });
        if (!exchange.ok) throw new Error('Your website permissions could not be verified. Please sign in again.');
        session = await exchange.json();
      } else {
        session = bootstrap;
      }
      await reload(); overview();
    });
  }));
})();
