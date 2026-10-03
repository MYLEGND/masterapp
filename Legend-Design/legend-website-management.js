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
  const iconPaths = Object.freeze({
    editor:'M4 4h16v12H8l-4 4V4m5 4h6m-6 4h9',
    drafts:'M4 5h6l2 2h8v12H4V5',
    publish:'M12 3v12m0-12 4 4m-4-4-4 4M5 15v5h14v-5',
    schedule:'M12 7v5l3 2M6 3v3m12-3v3M4 8h16M5 5h14v15H5V5',
    history:'M3 12a9 9 0 1 0 3-6.7M3 4v6h6',
    readiness:'M5 12l4 4L19 6',
    usage:'M4 19V9m6 10V5m6 14v-7m4 7H2',
    export:'M12 3v12m0 0-4-4m4 4 4-4M5 19h14',
    trash:'M4 7h16M9 7V4h6v3m-8 0 1 13h8l1-13',
    promote:'M4 14l7-7 4 4 5-6M15 5h5v5',
    import:'M12 21V9m0 0-4 4m4-4 4 4M5 5h14',
    marketing:'M4 18V6m4 12V9m4 9V4m4 14v-7m4 7H2',
    details:'M4 5h16v14H4V5m4 4h8m-8 4h8',
    domains:'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18m-9-9h18M12 3c3 3 4 6 4 9s-1 6-4 9c-3-3-4-6-4-9s1-6 4-9',
    inquiries:'M4 5h16v12H8l-4 4V5m4 4h8m-8 4h6'
  });
  const icon = name => {
    const wrap=el('span',null,{class:'wm-icon','aria-hidden':'true'});
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
    svg.setAttribute('viewBox','0 0 24 24'); svg.setAttribute('fill','none');
    svg.setAttribute('stroke','currentColor'); svg.setAttribute('stroke-width','1.8');
    svg.setAttribute('stroke-linecap','round'); svg.setAttribute('stroke-linejoin','round');
    const path=document.createElementNS('http://www.w3.org/2000/svg','path');
    path.setAttribute('d',iconPaths[name] || iconPaths.details); svg.append(path); wrap.append(svg); return wrap;
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
    const editorHref = () => {
      // The management session has already been authorized against the canonical
      // website API. Reuse that exact ticket for editor entry instead of minting
      // a second cross-app ticket/redirect for founder and agent scopes.
      const destination = trigger.dataset.scope === 'business'
        ? trigger.dataset.edit
        : (trigger.dataset.live || trigger.dataset.edit);
      const url = new URL(destination, location.origin);
      if (session?.ticket) url.searchParams.set('legendEdit', session.ticket);
      return url.href;
    };
    const request = async (path, payload) => {
      const url = new URL(`/api/website-content/manage${path}`, session.apiBase);
      const options = { cache: 'no-store', credentials: 'omit', headers: {} };
      if (payload) { options.method = 'POST'; options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify({ ...payload, ticket: session.ticket, expectedRevision: state?.revision }); }
      else url.searchParams.set('ticket', session.ticket);
      let response;
      try {
        response = await fetch(url, options);
      } catch {
        throw new Error('LEGEND could not reach the website service. Your saved website and DNS instructions were not changed. Reopen the workspace or try Verify status again.');
      }
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
    const section = title => {
      const back=button('Overview', overview); back.classList.add('wm-back'); back.prepend(icon('history'));
      panel.replaceChildren(back, el('h3', title));
      status.textContent = '';
    };
    const field = (name, type = 'text') => { const label = el('label', name), input = el('input', null, { type }); label.append(input); panel.append(label); return input; };
    const confirmAction = (title, description, action) => {
      if (!window.confirm(description)) return;
      void run(action);
    };
    const drafts = () => {
      section('Saved drafts');
      const list=el('div',null,{class:'wm-list wm-draft-list'});
      if (!state.drafts?.length) {
        list.append(el('p','No named drafts yet. Save a named draft from Website Studio.'));
      }
      for (const draft of state.drafts || []) {
        const row=el('div',null,{class:'wm-list-row'});
        const meta=el('div',null,{class:'wm-list-meta'});
        meta.append(el('strong',draft.name),el('small',new Date(draft.updatedUtc).toLocaleString()));
        const actions=el('div',null,{class:'wm-list-actions'});
        const edit=button('Edit',()=>run(async()=>{
          await request('/drafts/load',{draftId:draft.id});
          await reload();
          location.href=editorHref();
        }),true);
        const remove=button('Delete',()=>confirmAction('Delete draft',
          `Delete “${draft.name}”? The live website and current published version will remain unchanged.`,
          async()=>{await request('/drafts/delete',{draftId:draft.id});await reload();drafts();status.textContent='Draft deleted.';}));
        actions.append(edit,remove); row.append(meta,actions); list.append(row);
      }
      panel.append(list);
    };
    const history = () => {
      section('Version history');
      const list=el('div',null,{class:'wm-list'});
      if (!state.history?.length) list.append(el('p','Your first publication will appear here.'));
      for (const version of state.history || []) {
        const row=el('div',null,{class:'wm-list-row'});
        const meta=el('div',null,{class:'wm-list-meta'});
        meta.append(el('strong',`Version ${version.revision}`),
          el('small',version.createdUtc ? new Date(version.createdUtc).toLocaleString() : ''));
        const actions=el('div',null,{class:'wm-list-actions'});
        if(state.capabilities?.canPublish===true) actions.append(button('Restore',()=>confirmAction(
          'Restore version',
          'Restore this published version as the live website and current draft?',
          async()=>{await request('/rollback',{versionId:version.versionId ?? version.id});await reload();history();status.textContent='Version restored.';}
        )));
        row.append(meta,actions); list.append(row);
      }
      panel.append(list);
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
        if (typeof check !== 'string' && !check.passed) row.append(el('a', 'Review in editor', { href: editorHref() }));
        panel.append(row);
      }
    };
    const migrationReport = result => {
      section('Import review');
      const report = result.report ?? result;
      panel.append(el('p', 'Content is saved as a draft. Review the migration findings before publishing.'),
        el('a', 'Review imported draft', { href: editorHref(), class: 'wm-primary' }));
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
    const routingHost = hostname => {
      const labels = String(hostname || '').split('.').filter(Boolean);
      return labels.length <= 2 ? '@' : labels.slice(0, -2).join('.');
    };
    const dnsInstructions = (cnameTarget, hostname) => {
      if (!cnameTarget) { panel.append(el('p', 'The website routing target is not available yet.')); return; }
      panel.append(el('p', `At the DNS provider for ${hostname}, add exactly this record:`));
      const values = [
        ['Type', 'CNAME'],
        ['Host / Name', routingHost(hostname)],
        ['Points to / Value', cnameTarget]
      ];
      for (const [label, value] of values) {
        const row = el('div', null, { class: 'wm-row' });
        row.append(el('span', label));
        const input = el('input', null, { readonly: '', 'aria-label': label }); input.value = value; row.append(input);
        row.append(button('Copy', async () => { try { await navigator.clipboard.writeText(value); status.textContent = `${label} copied.`; } catch { input.select(); status.textContent = 'Select and copy the value.'; } }));
        panel.append(row);
      }
      panel.append(el('p', 'Save this DNS record at the registrar, then return here and choose Verify status. LEGEND handles the Cloudflare hostname and certificate checks automatically.'));
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
    const domainDiagnosticText = domain => {
      const diagnostic = domain?.diagnostic;
      if (!diagnostic?.summary && !diagnostic?.requiredAction) {
        return `${domain?.hostname ?? 'Domain'}: verification details are not available yet. Choose Verify status to run the provider and LEGEND routing checks now.`;
      }
      const providerErrors = Array.isArray(diagnostic.providerErrors) ? diagnostic.providerErrors.filter(Boolean) : [];
      const certificateErrors = Array.isArray(diagnostic.certificateErrors) ? diagnostic.certificateErrors.filter(Boolean) : [];
      const details = [...providerErrors, ...certificateErrors]
        .filter((value, index, values) => values.indexOf(value) === index)
        .map(value => `Provider detail: ${value}`);
      return [
        `${domain.hostname}: ${diagnostic.summary ?? 'Verification is pending.'}`,
        diagnostic.requiredAction ? `Required action: ${diagnostic.requiredAction}` : null,
        ...details
      ].filter(Boolean).join(' ');
    };
    const domains = () => run(async () => {
      section('Connected domains');
      const result = await request('/domains');
      for (const domain of result.domains ?? []) {
        const row = el('div', null, { class: 'wm-row' });
        const domainStatus = el('span', `${domain.hostname} · ${domain.status} · HTTPS ${domain.certificateStatus}`);
        const diagnostic = el('p', domainDiagnosticText(domain), { role: 'status', 'aria-live': 'polite' });
        row.append(domainStatus, button('Verify status', () => run(async () => {
          const checked = await request('/domains/refresh', { bindingId: domain.id });
          domainStatus.textContent = `${checked.hostname ?? domain.hostname} · ${checked.status ?? 'pending'} · HTTPS ${checked.certificateStatus ?? 'pending'}`;
          diagnostic.textContent = domainDiagnosticText(checked);
          status.textContent = domainDiagnosticText(checked);
        })), button('Disconnect', () => confirmAction('Disconnect domain', `Remove ${domain.hostname} from this website. Your website content will remain saved.`, async () => {
          await request('/domains/remove', { bindingId: domain.id }); overview(); status.textContent = 'Domain disconnected.';
        })));
        panel.append(row, diagnostic);
        dnsInstructions(result.cnameTarget, domain.hostname);
      }
      if (result.cnameTarget) panel.append(el('p', `Website CNAME target: ${result.cnameTarget}`));
      panel.append(el('p', 'Only change the website routing record shown above. Leave email and other DNS records unchanged.'));
      const domain = field('Domain name');
      panel.append(button('Connect domain', () => run(async () => {
        const result = await request('/domains', { hostname: domain.value.trim() });
        const binding = result.binding ?? result;
        dnsInstructions(result.cnameTarget, binding.hostname ?? domain.value.trim());
        status.textContent = domainDiagnosticText(binding);
      }), true)); status.textContent = '';
    });
    const marketingProfile = () => run(async () => {
      section('Marketing & booking');
      const profile = await request('/profile');
      const settings = profile.settings;
      panel.append(el('p', 'These settings belong to this business and apply across its website and analytics.'));
      panel.append(el('h4', 'Public card'));
      const bioLabel = el('label', 'Short bio'), bio = el('textarea', null, { rows: '4', maxlength: '2000' });
      bio.value = settings.shortBio || ''; bioLabel.append(bio); panel.append(bioLabel);
      panel.append(el('h4', 'Marketing'), el('p', profile.adsConnected ? `Connected account: ${profile.connectedAccount || 'Meta'}` : 'No active Meta Ads connection.'));
      const pixel = field('Meta Pixel ID'); pixel.value = settings.metaPixelId || ''; pixel.inputMode = 'numeric';
      panel.append(el('p', profile.hasSecureCapiToken
        ? 'Meta CAPI: configured securely through the scoped Meta Ads connection.'
        : 'Meta CAPI: connect Meta Ads to configure securely and automatically.'));
      const test = field('Meta Test Event Code (optional)'); test.value = settings.metaTestEventCode || '';
      panel.append(el('h4', 'Booking'));
      const enabled = field('Enable this business scheduler', 'checkbox'); enabled.checked = settings.bookingEnabled;
      const embed = field('Embed URL', 'url'); embed.value = settings.bookingEmbedUrl || '';
      const fallback = field('Fallback URL', 'url'); fallback.value = settings.bookingFallbackUrl || '';
      const mailbox = field('Mailbox / Page ID'); mailbox.value = settings.bookingMailboxId || '';
      const calendar = field('Calendar email', 'email'); calendar.value = settings.bookingCalendarEmail || '';
      panel.append(button('Save marketing & booking', () => run(async () => {
        const result = await request('/profile', { settings: {
          profileRevision: settings.profileRevision, connectionRevision: settings.connectionRevision,
          shortBio: bio.value, bookingEnabled: enabled.checked, bookingEmbedUrl: embed.value,
          bookingFallbackUrl: fallback.value, bookingMailboxId: mailbox.value, bookingCalendarEmail: calendar.value || null,
          metaPixelId: pixel.value, metaTestEventCode: test.value
        } });
        settings.profileRevision = result.settings.profileRevision; settings.connectionRevision = result.settings.connectionRevision;
        status.textContent = 'Marketing and booking settings saved.';
      }), true));
      status.textContent = '';
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
        const splitName = [inquiry.firstName, inquiry.lastName].filter(Boolean).join(' ').trim();
        const displayName = splitName || inquiry.name || inquiry.displayName || 'Website visitor';
        const contact = [inquiry.email, inquiry.phone].filter(Boolean).join(' · ');
        const row = el('div', null, { class: 'wm-row' });
        row.append(el('span', `${displayName}${contact ? ' · ' + contact : ''}\n${inquiry.message ?? ''}`));
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
    const promoteThis = () => run(async () => {
      section('Promote This');
      panel.append(el('p', 'Build a scoped ChatGPT Ads proposal from your published website, service, or product. Preview and approval are required before any provider mutation.'));
      const [sourceResult, conversionResult] = await Promise.all([
        request('/promote/sources'),
        request('/promote/conversions').catch(() => ({ payload: { data: [] } }))
      ]);
      const sources = sourceResult.items || [];
      if (!sources.length) { panel.append(el('p', 'Publish a page or add an active business product/service before creating a promotion.')); return; }

      const sourceLabel = el('label', 'What do you want to promote?');
      const sourceSelect = el('select', null, { 'aria-label': 'Promotion source' });
      for (const item of sources) {
        const option = el('option', item.detail ? `${item.label} · ${item.detail}` : item.label, { value: `${item.sourceKind}|${item.sourceId}|${item.pagePath || ''}` });
        sourceSelect.append(option);
      }
      sourceLabel.append(sourceSelect); panel.append(sourceLabel);

      const objectiveLabel = el('label', 'Objective');
      const objective = el('select', null, { 'aria-label': 'Promotion objective' });
      objective.append(el('option', 'Drive qualified visits (clicks)', { value: 'clicks' }), el('option', 'Optimize for a conversion', { value: 'conversions' }));
      objectiveLabel.append(objective); panel.append(objectiveLabel);

      const conversionLabel = el('label', 'Conversion objective');
      const conversion = el('select', null, { 'aria-label': 'Conversion objective' });
      conversion.append(el('option', 'Choose a standard conversion', { value: '' }));
      const conversionRows = Array.isArray(conversionResult?.payload?.data) ? conversionResult.payload.data : [];
      for (const row of conversionRows.filter(row => String(row.event_type || '').toLowerCase() !== 'custom' && String(row.status || '').toLowerCase() !== 'archived')) {
        conversion.append(el('option', row.name || row.event_name || row.event_type || 'Conversion', { value: row.id }));
      }
      conversionLabel.append(conversion); panel.append(conversionLabel);
      conversionLabel.hidden = true;
      objective.addEventListener('change', () => { conversionLabel.hidden = objective.value !== 'conversions'; });

      const budget = field('Daily budget (connected ad account currency)', 'number');
      budget.min = '1'; budget.step = '0.01';
      const goal = field('Campaign goal / offer');
      const hints = field('Context hints (comma separated)');
      const countries = field('Country codes (comma separated, optional)');
      const launchLabel = el('label', 'Execution state');
      const launchState = el('select', null, { 'aria-label': 'Execution state' });
      launchState.append(el('option', 'Create paused for final provider review', { value: 'paused' }), el('option', 'Launch active after approval', { value: 'active' }));
      launchLabel.append(launchState); panel.append(launchLabel);

      const readPromotion = selectedCreativeKey => {
        const [sourceKind, sourceId, pagePath] = sourceSelect.value.split('|');
        const amount = Number(budget.value);
        if (!Number.isFinite(amount) || amount < 1) throw new Error('Enter a daily budget of at least 1.00 in the connected account currency.');
        if (objective.value === 'conversions' && !conversion.value) throw new Error('Choose a standard conversion objective.');
        return {
          sourceKind, sourceId, pagePath: pagePath || null,
          goal: goal.value.trim() || null,
          dailyBudgetMicros: Math.round(amount * 1000000),
          biddingType: objective.value,
          contextHints: hints.value.split(',').map(value => value.trim()).filter(Boolean),
          countries: countries.value.split(',').map(value => value.trim().toUpperCase()).filter(Boolean),
          platforms: null,
          status: launchState.value,
          conversionEventSettingId: objective.value === 'conversions' ? conversion.value : null,
          selectedCreativeKey: selectedCreativeKey || null
        };
      };

      const renderProposal = proposal => {
        section('Review exact advertising action');
        panel.append(el('p', 'This proposal is persisted but not approved and has not executed.'));
        panel.append(el('p', `Action digest: ${proposal.actionDigest}`));
        const steps = proposal.plan?.steps || [];
        for (const step of steps) {
          const details = el('details', null);
          details.append(el('summary', `${step.stepKey}: ${step.actionType}`), el('pre', JSON.stringify(step.payload, null, 2)));
          panel.append(details);
        }
        panel.append(button('Approve exact plan', () => run(async () => {
          const approved = await request('/promote/approve', { proposalId: proposal.id, revision: proposal.revision });
          const receipt = approved.receipt;
          section('Advertising plan approved');
          panel.append(el('p', 'Approval is bound to the exact reviewed plan. No provider action has executed yet.'),
            el('p', `Approval expires ${new Date(receipt.approvalExpiresUtc).toLocaleTimeString()}.`),
            button(launchState.value === 'active' ? 'Launch approved plan' : 'Create approved paused campaign', () => run(async () => {
              const executed = await request('/promote/execute', { proposalId: proposal.id, revision: receipt.revision });
              const result = executed.receipt;
              section('Advertising execution receipt');
              panel.append(el('p', `State: ${result.state}`));
              if (result.errorMessage) panel.append(el('p', `Provider result: ${result.errorMessage}`));
              if (result.providerReceipt) {
                const details = el('details', null); details.append(el('summary', 'Provider receipt'), el('pre', JSON.stringify(result.providerReceipt, null, 2))); panel.append(details);
              }
              panel.append(button('Back to website overview', overview));
              status.textContent = result.state === 'Completed' ? 'Approved advertising plan executed and audited.' : `Advertising execution ended in ${result.state}.`;
            }), true),
            button('Reject instead', () => run(async () => {
              await request('/promote/reject', { proposalId: proposal.id, revision: receipt.revision });
              overview(); status.textContent = 'Advertising proposal rejected. Nothing was launched.';
            })));
        }), true),
        button('Reject proposal', () => run(async () => {
          await request('/promote/reject', { proposalId: proposal.id, revision: proposal.revision });
          overview(); status.textContent = 'Advertising proposal rejected. Nothing was launched.';
        })));
      };

      const renderDraft = draft => {
        section('Preview promotion');
        panel.append(el('p', `${draft.source.displayName} → ${draft.source.landingUrl}`),
          el('p', `Objective: ${draft.biddingType} · Daily budget: ${(draft.dailyBudgetMicros / 1000000).toFixed(2)}`));
        let selected = draft.selectedAlternativeKey;
        for (const alternative of draft.alternatives || []) {
          const card = el('label', null, { class: 'wm-promotion-creative' });
          const radio = el('input', null, { type: 'radio', name: 'promotion-creative', value: alternative.key });
          radio.checked = alternative.key === selected;
          radio.addEventListener('change', () => { selected = alternative.key; });
          card.append(radio, el('strong', alternative.title), el('span', alternative.body));
          if (alternative.imageUrl) card.append(el('img', null, { src: alternative.imageUrl, alt: '', loading: 'lazy' }));
          panel.append(card);
        }
        const exact = el('details', null); exact.append(el('summary', 'Exact proposed provider actions'), el('pre', JSON.stringify(draft.plan?.steps || [], null, 2))); panel.append(exact);
        panel.append(button('Create approval proposal', () => run(async () => {
          const proposed = await request('/promote/propose', { promotion: readPromotion(selected) });
          renderProposal(proposed.proposal);
        }), true), button('Change setup', promoteThis));
        status.textContent = 'Preview only. Nothing has been approved or executed.';
      };

      panel.append(button('Preview promotion', () => run(async () => {
        const draft = await request('/promote/draft', { promotion: readPromotion(null) });
        renderDraft(draft.draft);
      }), true));
      status.textContent = '';
    });
    const deleteWebsite = () => {
      section('Delete website');
      panel.append(el('p', 'This removes the current published website, working draft, named drafts, import state, and scheduled publication for this website scope. It does not delete the account, CRM, analytics, Meta settings, business profile, inquiries, or historical audit versions.'));
      const confirmation = field('Type DELETE to confirm');
      panel.append(button('Delete website', () => run(async () => {
        if (confirmation.value.trim().toUpperCase() !== 'DELETE') throw new Error('Type DELETE to confirm website deletion.');
        await request('/delete', {});
        await reload();
        overview();
        status.textContent = 'Website deleted. This scope is no longer published.';
      }), true));
    };
    const overview = () => {
      panel.replaceChildren();
      status.textContent = `Draft ${state.revision ?? 0} · Published ${state.publishedRevision ?? '—'}`;
      const links=el('div',null,{class:'wm-hero-actions'});
      const edit=el('a','Open editor',{href:editorHref(),class:'wm-primary'}); edit.prepend(icon('editor'));
      const live=el('a','View website',{href:trigger.dataset.live,target:'_blank',rel:'noopener'}); live.prepend(icon('export'));
      links.append(edit,live); panel.append(links);

      const grid=el('div',null,{class:'wm-grid'});
      const tile=(name,iconName,action,{danger=false}={})=>{
        const node=button(name,action);
        node.classList.add('wm-tile'); if(danger) node.classList.add('wm-danger');
        node.prepend(icon(iconName)); grid.append(node);
      };
      tile('Drafts','drafts',drafts);
      if(state.capabilities?.canPublish===true) tile('Publish','publish',()=>confirmAction(
        'Publish',
        'Publish the current saved draft as a new live website version?',
        async()=>{await request('/publish',{});await reload();overview();status.textContent='Website published.';}
      ));
      if(state.capabilities?.canSchedule===true) tile('Schedule','schedule',schedule);
      tile('History','history',history);
      tile('Readiness','readiness',readiness);
      if(state.capabilities?.canImport===true) tile('Import','import',importDraft);
      tile('Usage','usage',usage);
      tile('Export','export',exportWebsite);
      if(state.capabilities?.canDelete===true) tile('Delete','trash',deleteWebsite,{danger:true});
      if(state.capabilities?.canPromote===true) tile('Promote This','promote',promoteThis);
      if(trigger.dataset.scope==='business'){
        if(state.capabilities?.canPublish===true) tile('Marketing & booking','marketing',marketingProfile);
        tile('Business details','details',businessDetails);
        if(state.capabilities?.canManageDomains===true) tile('Domains','domains',domains);
        tile('Inquiries','inquiries',inquiries);
      }
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
