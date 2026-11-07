(() => {
  'use strict';

  // Native validation remains authoritative. Reveal collapsed ancestors before the browser
  // focuses an invalid field, so grouped services/answers cannot hide a required correction.
  document.addEventListener('invalid', event => {
    const field = event.target;
    if (!(field instanceof HTMLElement) || !field.closest('.receptionist-workspace, .receptionist-guide')) return;
    for (let parent = field.parentElement; parent; parent = parent.parentElement) {
      if (parent instanceof HTMLDetailsElement) parent.open = true;
    }
  }, true);

  const modal = document.querySelector('[data-receptionist-guide]');
  if (!modal || modal.dataset.guideBound === 'true') return;
  modal.dataset.guideBound = 'true';
  const find = name => modal.querySelector(`[data-guide-${name}]`);
  const status = find('status');
  const reader = find('reader');
  const editor = find('editor');
  const fields = find('fields');
  const reload = find('reload');
  let current = null;
  let draft = null;
  let audience = 'business';
  let editAudience = 'business';
  let step = 0;
  let busy = false;
  let publicationUnverified = false;
  let publicationConflict = false;
  let currentVerified = false;
  let loadSequence = 0;

  const message = text => { status.textContent = text; };
  const create = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const sameOriginUrl = value => {
    if (typeof value !== 'string' || !value) throw new Error('Guide route unavailable.');
    const url = new URL(value, location.href);
    if (url.origin !== location.origin || url.username || url.password) throw new Error('Guide route unavailable.');
    return url.href;
  };
  const validDocument = document => document && typeof document.title === 'string' &&
    typeof document.introduction === 'string' && Array.isArray(document.steps) && document.steps.length >= 1 &&
    document.steps.length <= 16 && document.steps.every(item => item && typeof item.title === 'string' && typeof item.instructions === 'string');
  const validGuide = guide => guide && Number.isSafeInteger(guide.version) && guide.version > 0 &&
    typeof guide.revision === 'string' && typeof guide.publishedUtc === 'string' && validDocument(guide.business) &&
    (guide.founder == null || validDocument(guide.founder));
  const canEdit = () => currentVerified && current?.founder && current?.publishUrl;
  const plainCopy = (parent, text) => {
    parent.replaceChildren();
    for (const paragraph of text.split(/\n\s*\n/)) parent.append(create('p', 'receptionist-guide__copy', paragraph));
  };

  function renderStep(focus = false) {
    const guide = current?.[audience];
    if (!guide) return;
    step = Math.max(0, Math.min(step, guide.steps.length - 1));
    find('document-title').textContent = guide.title;
    find('introduction').textContent = guide.introduction;
    find('progress').textContent = `Step ${step + 1} of ${guide.steps.length}`;
    find('step-title').textContent = guide.steps[step].title;
    plainCopy(find('instructions'), guide.steps[step].instructions);
    find('previous').disabled = step === 0;
    find('next').disabled = step === guide.steps.length - 1;
    find('next').textContent = step === guide.steps.length - 1 ? 'Last step' : 'Next step';
    const list = find('steps');
    list.replaceChildren();
    const select = find('step-select');
    select.replaceChildren();
    guide.steps.forEach((item, index) => {
      const option = create('option', '', `${index + 1}. ${item.title}`);
      option.value = String(index); option.selected = index === step;
      select.append(option);
      const li = create('li');
      const button = create('button', 'receptionist-button receptionist-button--quiet');
      button.type = 'button';
      button.dataset.guideStep = String(index);
      button.append(create('span', 'receptionist-guide__step-number', String(index + 1).padStart(2, '0')),
        create('span', '', item.title));
      if (index === step) button.setAttribute('aria-current', 'step');
      li.append(button); list.append(li);
    });
    modal.querySelectorAll('[data-guide-audience]').forEach(button =>
      button.setAttribute('aria-pressed', String(button.dataset.guideAudience === audience)));
    if (focus) find('step-title').focus();
  }

  function showPublished(focus = false) {
    if (!currentVerified || !current) {
      reader.hidden = true; find('toolbar').hidden = true; find('version').textContent = '';
      find('publish').disabled = true; reload.hidden = false;
      message('Current published instructions are unverified. Reload them before continuing.');
      return;
    }
    editor.hidden = true;
    reader.hidden = false;
    find('toolbar').hidden = false;
    find('audiences').hidden = !current.founder;
    find('edit').hidden = !canEdit();
    find('edit').textContent = draft ? 'Resume unpublished edits' : 'Edit canonical instructions';
    const date = new Date(current.publishedUtc);
    find('version').textContent = `Published version ${current.version} · ${Number.isNaN(date.getTime()) ? 'Update time unavailable' : date.toLocaleString()}`;
    renderStep(focus);
  }

  async function loadCurrent() {
    if (busy) return;
    if (draft && !editor.hidden) retainFields();
    currentVerified = false;
    const sequence = ++loadSequence;
    busy = true; reload.disabled = true;
    reader.hidden = true; find('toolbar').hidden = true;
    find('version').textContent = ''; find('publish').disabled = true;
    message('Loading the current published instructions…');
    try {
      const response = await fetch(sameOriginUrl(modal.dataset.guideUrl), { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error('Current setup instructions are unavailable. Reload the guide or ask your platform operator to verify access and deployment.');
      const data = await response.json();
      if (!validGuide(data)) throw new Error('The current published guide could not be verified. Ask your platform operator to check it.');
      if (sequence !== loadSequence) return;
      // A retained editor can remain usable while this read is pending. Capture
      // its latest inputs immediately before hiding it, not only when the read began.
      if (draft && !editor.hidden) retainFields();
      const initial = current === null;
      current = data; currentVerified = true;
      if (initial) audience = current.founder ? 'founder' : 'business';
      if (!current[audience]) audience = 'business';
      reload.hidden = true;
      showPublished();
      if (draft) {
        message(`Published version ${current.version} loaded. Your unpublished edits are retained separately. Review the current guide before deciding whether to resume them.`);
      } else if (publicationUnverified) {
        message(`Current published version ${current.version} loaded. The earlier publication result was not confirmed; check both views before starting a new edit.`);
      } else message('Follow the steps below. Live call readiness remains in Call status.');
    } catch (error) {
      if (sequence !== loadSequence) return;
      // An unavailable read must not display an older guide as the current canonical version.
      reader.hidden = true; find('toolbar').hidden = true; find('version').textContent = '';
      find('publish').disabled = true;
      message(error.message || 'Current setup instructions are unavailable.');
      reload.hidden = false;
    } finally {
      if (sequence === loadSequence) { busy = false; reload.disabled = false; }
    }
  }

  function field(label, value, maximum, key, index, multiline = false) {
    const wrapper = create('label', 'receptionist-field');
    const caption = create('span', '', label);
    const input = create(multiline ? 'textarea' : 'input');
    if (!multiline) input.type = 'text';
    else input.rows = key === 'introduction' ? 3 : 7;
    input.value = value;
    input.maxLength = maximum;
    input.required = true;
    input.dataset.guideField = key;
    if (index !== undefined) input.dataset.stepIndex = String(index);
    wrapper.append(caption, input, create('small', 'receptionist-copy', `Up to ${maximum.toLocaleString()} characters. Plain text.`));
    return wrapper;
  }

  function retainFields() {
    if (!draft) return;
    const document = draft[editAudience];
    fields.querySelectorAll('[data-guide-field]').forEach(input => {
      const key = input.dataset.guideField;
      if (input.dataset.stepIndex !== undefined) document.steps[Number(input.dataset.stepIndex)][key] = input.value;
      else document[key] = input.value;
    });
  }

  function renderEditor(focusIndex) {
    const document = draft[editAudience];
    fields.replaceChildren(field('Guide title', document.title, 120, 'title'),
      field('Introduction', document.introduction, 600, 'introduction', undefined, true));
    document.steps.forEach((item, index) => {
      const block = create('fieldset', 'receptionist-inset');
      block.append(create('legend', '', `Step ${index + 1}`), field('Step title', item.title, 120, 'title', index),
        field('Step instructions', item.instructions, 4000, 'instructions', index, true));
      const actions = create('div', 'receptionist-actions');
      for (const [action, label, disabled] of [['up', 'Move earlier', index === 0], ['down', 'Move later', index === document.steps.length - 1], ['remove', 'Remove step', document.steps.length === 1]]) {
        const button = create('button', 'receptionist-button receptionist-button--quiet', label);
        button.type = 'button'; button.dataset.guideStepAction = action; button.dataset.stepIndex = String(index); button.disabled = disabled;
        button.setAttribute('aria-label', `${label}: step ${index + 1}`);
        actions.append(button);
      }
      block.append(actions); fields.append(block);
    });
    modal.querySelectorAll('[data-guide-editor-audience]').forEach(button =>
      button.setAttribute('aria-pressed', String(button.dataset.guideEditorAudience === editAudience)));
    find('add-step').disabled = document.steps.length >= 16;
    find('edit-preview').hidden = true;
    find('publish').disabled = publicationUnverified || publicationConflict || !currentVerified;
    if (focusIndex !== undefined) fields.querySelector(`[data-step-index="${focusIndex}"][data-guide-field="title"]`)?.focus();
  }

  function openEditor() {
    if (!canEdit() || busy) return;
    if (!draft) {
      draft = JSON.parse(JSON.stringify({ expectedVersion: current.version, expectedRevision: current.revision,
        business: current.business, founder: current.founder }));
      editAudience = audience;
      publicationUnverified = false; publicationConflict = false;
    }
    reader.hidden = true; editor.hidden = false; find('toolbar').hidden = true;
    renderEditor(); find('editor-title').focus();
    message(`Editing from published version ${draft.expectedVersion}. Publish replaces both canonical views in one new version.`);
    if (draft.expectedRevision !== current.revision) {
      publicationConflict = true; find('publish').disabled = true;
      message(`Your retained edits began at version ${draft.expectedVersion}; the published guide is now version ${current.version}. Copy any changes you want to keep, then discard the old draft and edit the current version.`);
      reload.hidden = false;
    }
  }

  modal.addEventListener('shown.bs.modal', () => {
    if (!editor.hidden) retainFields();
    loadCurrent();
  });
  // Closing the canonical modal retains unpublished inputs; the modal manager owns focus and scrolling.
  modal.addEventListener('hide.bs.modal', () => { if (!editor.hidden) retainFields(); });
  reload.addEventListener('click', () => loadCurrent());
  find('edit').addEventListener('click', openEditor);
  find('previous').addEventListener('click', () => { step--; renderStep(true); });
  find('next').addEventListener('click', () => { step++; renderStep(true); });
  find('step-select').addEventListener('change', event => { step = Number(event.target.value); renderStep(true); });
  find('steps').addEventListener('click', event => {
    const button = event.target.closest('[data-guide-step]');
    if (!button) return;
    step = Number(button.dataset.guideStep); renderStep(true);
  });
  modal.querySelectorAll('[data-guide-audience]').forEach(button => button.addEventListener('click', () => {
    const value = button.dataset.guideAudience;
    if (!current?.[value]) return;
    audience = value; step = 0; renderStep(true);
  }));
  modal.querySelectorAll('[data-guide-editor-audience]').forEach(button => button.addEventListener('click', () => {
    if (!draft) return;
    retainFields(); editAudience = button.dataset.guideEditorAudience; renderEditor();
  }));
  fields.addEventListener('click', event => {
    const button = event.target.closest('[data-guide-step-action]');
    if (!button || !draft) return;
    retainFields(); const index = Number(button.dataset.stepIndex); const steps = draft[editAudience].steps;
    if (button.dataset.guideStepAction === 'remove' && steps.length > 1) steps.splice(index, 1);
    else if (button.dataset.guideStepAction === 'up' && index > 0) [steps[index - 1], steps[index]] = [steps[index], steps[index - 1]];
    else if (button.dataset.guideStepAction === 'down' && index < steps.length - 1) [steps[index + 1], steps[index]] = [steps[index], steps[index + 1]];
    renderEditor(Math.min(index, steps.length - 1));
  });
  find('add-step').addEventListener('click', () => {
    if (!draft) return;
    retainFields(); const steps = draft[editAudience].steps;
    if (steps.length >= 16) return;
    steps.push({ title: '', instructions: '' }); renderEditor(steps.length - 1);
  });
  find('preview').addEventListener('click', () => {
    retainFields(); const document = draft?.[editAudience]; if (!document) return;
    const preview = find('edit-preview'); preview.replaceChildren(create('h4', '', 'Unpublished preview'),
      create('h5', '', document.title), create('p', 'receptionist-copy', document.introduction));
    document.steps.forEach((item, index) => {
      preview.append(create('h5', '', `${index + 1}. ${item.title}`));
      const copy = create('div', 'receptionist-guide__copy'); plainCopy(copy, item.instructions); preview.append(copy);
    });
    preview.hidden = false;
  });
  find('cancel').addEventListener('click', () => {
    retainFields();
    if (draft && window.confirm('Discard these unpublished edits and return to the current published guide?')) {
      const needsCurrentRead = !currentVerified || publicationUnverified || publicationConflict;
      draft = null; editor.hidden = true;
      // Discarding local text does not verify an earlier publication or a failed read.
      // A cached pre-publication document must never be relabeled as current.
      if (needsCurrentRead) loadCurrent();
      else { showPublished(true); message('Unpublished edits discarded. The current published guide is shown.'); }
    }
  });
  editor.addEventListener('submit', async event => {
    event.preventDefault();
    if (busy || !draft || !canEdit() || publicationUnverified || publicationConflict) return;
    retainFields();
    if (!editor.reportValidity()) return;
    busy = true; find('publish').disabled = true; editor.inert = true; editor.setAttribute('aria-busy', 'true');
    message('Publishing the two canonical guides…');
    try {
      const token = editor.querySelector('input[name="__RequestVerificationToken"]')?.value;
      if (!token) throw new Error('The publication security token is unavailable. Keep your edits and reload the page.');
      const response = await fetch(sameOriginUrl(current.publishUrl), {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: token },
        body: JSON.stringify(draft)
      });
      const data = await response.json().catch(() => null);
      if (!response.ok) {
        if (response.status >= 500) publicationUnverified = true;
        if (response.status === 409) publicationConflict = true;
        if (publicationUnverified || publicationConflict) currentVerified = false;
        reload.hidden = response.status !== 409 && !publicationUnverified;
        throw new Error(data?.message || 'Publication was not confirmed. Keep your edits and verify the current published guide.');
      }
      if (!validGuide(data) || !data.founder) { publicationUnverified = true; currentVerified = false; throw new Error('Publication returned an unverified result. Keep your edits and reload the current instructions.'); }
      current = data; currentVerified = true; draft = null; publicationUnverified = false; publicationConflict = false; step = 0;
      reload.hidden = true; showPublished(true);
      message(`Version ${current.version} is published. All businesses receive the updated Business setup guide the next time they open it.`);
    } catch (error) {
      // Network errors can follow a committed write. Never automatically replay a publication.
      if (error instanceof TypeError) publicationUnverified = true;
      if (publicationUnverified || publicationConflict) {
        currentVerified = false; reader.hidden = true; find('toolbar').hidden = true;
        find('version').textContent = ''; reload.hidden = false;
      }
      message(error.message || 'Publication could not be verified. Your edits are retained.');
    } finally {
      busy = false; editor.inert = false; editor.removeAttribute('aria-busy');
      find('publish').disabled = publicationUnverified || publicationConflict || !currentVerified;
    }
  });
})();
