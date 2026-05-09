(function(){
  const ROUTE_HINTS = Object.freeze({
    'sec-hub': {
      requiresChoice: true,
      note: 'Use the real objection to route back into the right lane instead of restarting the whole call.'
    },
    'sec-voicemail': {
      defaultNext: 'sec-aged',
      note: 'Most live conversations should restart in the aged lead opener after voicemail or callback.'
    },
    'sec-texts': {
      defaultNext: 'sec-aged',
      note: 'Texting should funnel them back into a live conversation, not turn into a debate.'
    },
    'sec-aged': {
      defaultNext: 'sec-verify',
      note: 'Verify first so you can split them cleanly into replacement or one-call-close.'
    },
    'sec-verify': {
      requiresChoice: true,
      note: 'This is the fork. Pick the lane that matches whether they already have coverage.'
    },
    'sec-replace': {
      defaultNext: 'sec-options-choose',
      note: 'After review, force the choice moment instead of letting them drift.'
    },
    'sec-book': {
      terminal: true,
      note: 'This lane usually ends with a scheduled appointment unless the objection reopens.'
    },
    'sec-occ': {
      defaultNext: 'sec-equity',
      note: 'Once the OCC frame lands, move into emotional meaning before presenting numbers.'
    },
    'sec-threeins': {
      note: 'Keep this short. Answer the confusion, then move right back into the sales lane.'
    },
    'sec-equity': {
      defaultNext: 'sec-living',
      note: 'After the meaning bridge, move into the presentation lane that best fits the client.'
    },
    'sec-living': {
      defaultNext: 'sec-options-choose',
      note: 'After showing the living-benefits lane, force clarity on the decision.'
    },
    'sec-rop': {
      defaultNext: 'sec-options-choose',
      note: 'Use the cash-back explanation, then move immediately into the choice moment.'
    },
    'sec-iul': {
      defaultNext: 'sec-options-choose',
      note: 'Once the cash-value/IUL case is made, pin them to a decision instead of re-explaining.'
    },
    'sec-options-choose': {
      requiresChoice: true,
      note: 'This moment should force a clean branch: application or objection.'
    },
    'sec-eapp': {
      defaultNext: 'sec-closeout',
      note: 'Once the application is done, close the loop while trust and momentum are still high.'
    },
    'sec-closeout': {
      terminal: true,
      note: 'This is the retention and expectation-setting finish line.'
    }
  });

  const SHARED_BRANCHES = Object.freeze({
    'sec-hub': ['sec-aged', 'sec-verify', 'sec-options-choose'],
    'sec-verify': ['sec-replace', 'sec-occ', 'sec-book'],
    'sec-replace': ['sec-options-choose', 'sec-hub'],
    'sec-occ': ['sec-threeins', 'sec-equity'],
    'sec-equity': ['sec-living', 'sec-rop', 'sec-iul'],
    'sec-living': ['sec-options-choose', 'sec-hub'],
    'sec-rop': ['sec-options-choose', 'sec-hub'],
    'sec-iul': ['sec-options-choose', 'sec-hub'],
    'sec-options-choose': ['sec-eapp', 'sec-hub', 'sec-book'],
    'sec-eapp': ['sec-closeout', 'sec-hub'],
    'sec-closeout': ['sec-aged'],
    'sec-book': ['sec-aged']
  });

  function sharedSignalLabel(signal){
    const normalized = signalClass(signal);
    if (normalized === 'good') return 'GO';
    if (normalized === 'warn') return 'CLARIFY';
    if (normalized === 'bad') return 'STOP';
    return 'INFO';
  }

  function normalizeSectionRecord(section){
    if (!section) return null;

    if (section instanceof HTMLElement){
      return {
        id: section.id || '',
        title: sectionTitle(section),
        sub: sectionSubtitle(section),
        signal: signalClass(section.getAttribute('data-signal')),
        step: stepText(section),
        stepLabel: flowText(section) ? `Step ${stepText(section)} · ${flowText(section)}` : `Step ${stepText(section)}`
      };
    }

    const step = String(section.step || '').trim();
    return {
      id: String(section.id || '').trim(),
      title: String(section.title || section.targetTitle || '').trim() || String(section.id || '').trim(),
      sub: String(section.sub || '').trim(),
      signal: signalClass(section.signal),
      step,
      stepLabel: String(section.stepLabel || (step ? `Step ${step}` : '')).trim()
    };
  }

  function uniqueRouteOptions(options){
    const seen = new Set();
    return options.filter(option => {
      if (!option?.id) return false;
      if (seen.has(option.id)) return false;
      seen.add(option.id);
      return true;
    });
  }

  function buildSharedOption(sectionMap, targetId, label){
    const target = sectionMap.get(targetId);
    if (!target?.id) return null;
    return {
      id: target.id,
      selector: normalizeTarget(target.id),
      label: label || target.title || target.id,
      targetTitle: target.title || target.id,
      signal: signalClass(target.signal)
    };
  }

  function buildRouteSnapshot(sectionId, sections, extras = {}){
    const sectionMap = new Map(
      (Array.isArray(sections) ? sections : [])
        .map(normalizeSectionRecord)
        .filter(section => section?.id)
        .map(section => [section.id, section])
    );

    const current = sectionMap.get(sectionId) || null;
    const hint = ROUTE_HINTS[sectionId] || {};
    const lastSectionId = extras.lastSectionId || '';

    let primary = null;
    if (hint.defaultNext === 'return' && lastSectionId && lastSectionId !== sectionId){
      const lastSection = sectionMap.get(lastSectionId);
      if (lastSection){
        primary = buildSharedOption(sectionMap, lastSectionId, `Return to ${lastSection.title}`);
      }
    } else if (hint.defaultNext){
      primary = buildSharedOption(sectionMap, hint.defaultNext);
    }

    const branches = (SHARED_BRANCHES[sectionId] || [])
      .map(targetId => buildSharedOption(sectionMap, targetId))
      .filter(Boolean);

    const filteredBranches = hint.requiresChoice
      ? uniqueRouteOptions(primary ? [primary, ...branches] : branches)
      : uniqueRouteOptions(branches.filter(option => option.id !== primary?.id));

    const fallbacks = [];
    if (lastSectionId && lastSectionId !== sectionId){
      const lastSection = sectionMap.get(lastSectionId);
      if (lastSection){
        fallbacks.push(buildSharedOption(sectionMap, lastSectionId, `Return to ${lastSection.title}`));
      }
    }
    if (sectionId !== 'sec-hub'){
      fallbacks.push(buildSharedOption(sectionMap, 'sec-hub', 'Objection hub'));
    }
    if (sectionId !== 'sec-aged'){
      fallbacks.push(buildSharedOption(sectionMap, 'sec-aged', 'Restart opener'));
    }

    return {
      current,
      hint,
      primary,
      branches: filteredBranches,
      fallbacks: uniqueRouteOptions(fallbacks.filter(Boolean))
    };
  }

  function escapeHtml(value){
    return String(value || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function signalClass(signal){
    const normalized = String(signal || '').toLowerCase().trim();
    if (normalized === 'good' || normalized === 'warn' || normalized === 'bad' || normalized === 'info'){
      return normalized;
    }
    return 'warn';
  }

  function stepText(section){
    return (section?.getAttribute('data-step') || '').trim() || '—';
  }

  function flowText(section){
    return (section?.getAttribute('data-flow') || '').trim().replace(/-/g, ' ') || '';
  }

  function sectionTitle(section){
    return section?.querySelector('.title')?.textContent?.trim()
      || section?.getAttribute('data-step')
      || section?.id
      || 'Step';
  }

  function sectionSubtitle(section){
    return section?.querySelector('.sub')?.textContent?.trim() || '';
  }

  function topLevelSections(card){
    return Array.from(card.querySelectorAll(':scope > details.section'));
  }

  function allSections(card){
    return Array.from(card.querySelectorAll('details.section'));
  }

  function parentSection(section){
    return section?.parentElement?.closest?.('details.section') || null;
  }

  function isHubTarget(selector){
    return selector === '#sec-hub';
  }

  function normalizeTarget(selector){
    if (!selector) return '';
    return selector.startsWith('#') ? selector : `#${selector}`;
  }

  function initSharedGuide(options){
    const shell = document.getElementById(options?.shellId || 'mpShell');
    const card = document.getElementById(options?.cardId || 'mpCard');

    if (!shell || !card) return false;
    if (card.dataset.flowGuideInit === 'true') return true;
    card.dataset.flowGuideInit = 'true';

    const search = document.getElementById(options?.searchId || 'mpSearch');
    const toastEl = document.getElementById(options?.toastId || 'toast');
    const btnExpand = document.getElementById(options?.expandId || 'btnExpand');
    const btnCollapse = document.getElementById(options?.collapseId || 'btnCollapse');
    const btnClear = document.getElementById(options?.clearId || 'btnClear');
    const btnNext = document.getElementById(options?.nextId || 'btnNext');
    const btnExitFocus = document.getElementById(options?.exitFocusId || 'btnExitFocus');
    const btnNextFocus = document.getElementById(options?.nextFocusId || 'btnNextFocus');
    const sideNav = document.getElementById(options?.sideNavId || 'sideNav');
    const progDone = document.getElementById(options?.progressDoneId || 'progDone');
    const progTotal = document.getElementById(options?.progressTotalId || 'progTotal');
    const progFill = document.getElementById(options?.progressFillId || 'progFill');
    const main = shell.querySelector('.mp-main');
    const focusBanner = document.getElementById('focusBanner');

    const topSections = topLevelSections(card);
    const orderedSections = allSections(card);
    const navStack = [];
    const visitedIds = new Set();
    const branchMemory = new Map();
    let currentSectionId = '';
    let lastNonHubSectionId = '';

    function toast(message){
      if (!toastEl) return;
      toastEl.textContent = message || 'Copied.';
      toastEl.classList.add('show');
      window.setTimeout(() => toastEl.classList.remove('show'), 1100);
    }

    function findSectionById(sectionId){
      if (!sectionId) return null;
      return orderedSections.find(section => section.id === sectionId) || null;
    }

    function topSectionForNode(node){
      if (!node) return null;
      return node.closest('#mpCard > details.section') || node.closest('details.section') || null;
    }

    function getCurrentSection(){
      return findSectionById(currentSectionId)
        || topSectionForNode(card.querySelector('.section.is-current'))
        || orderedSections.find(section => section.open)
        || findSectionById('sec-aged')
        || orderedSections[0]
        || null;
    }

    function getSummary(section){
      return section?.querySelector(':scope > summary') || null;
    }

    function getAnchorSection(){
      if (shell.classList.contains('focus-on')){
        return topSectionForNode(card.querySelector('.section.is-focus')) || getCurrentSection();
      }
      return getCurrentSection();
    }

    function getSequentialNext(section){
      const currentIndex = orderedSections.findIndex(item => item.id === section?.id);
      if (currentIndex < 0) return null;
      return orderedSections[currentIndex + 1] || null;
    }

    function ensureParentChainOpen(section){
      let cursor = section;
      while (cursor){
        cursor.open = true;
        cursor = parentSection(cursor);
      }
    }

    function setFocusTarget(section){
      orderedSections.forEach(item => item.classList.remove('is-focus'));
      if (!section) return;
      const focusTarget = topSectionForNode(section) || section;
      focusTarget.classList.add('is-focus');
    }

    function rememberVisited(section){
      if (!section?.id) return;
      visitedIds.add(section.id);
      if (section.id !== 'sec-hub'){
        lastNonHubSectionId = section.id;
      }
    }

    function setCurrentSection(section, opts = {}){
      if (!section?.id) return;
      const top = topSectionForNode(section) || section;
      currentSectionId = section.id;
      ensureParentChainOpen(section);
      if (opts.focusMode){
        shell.classList.add('focus-on');
        setFocusTarget(section);
      }
      if (opts.markVisited !== false){
        rememberVisited(section);
      }
      updateCurrentState();
      updateProgress();
      renderFlowGuide();
      updateNextButtons();
    }

    function activeSignal(section){
      return signalClass(section?.getAttribute('data-signal'));
    }

    function activeSignalLabel(section){
      const signal = activeSignal(section);
      if (signal === 'good') return 'GO';
      if (signal === 'warn') return 'CLARIFY';
      if (signal === 'bad') return 'STOP';
      return 'INFO';
    }

    function classifyOption(option){
      if (!option) return 'branch';
      if (isHubTarget(option.selector) || /unsure|hub|objection/i.test(option.label || '')){
        return 'fallback';
      }
      return 'branch';
    }

    function uniqueOptions(options){
      const seen = new Set();
      return options.filter(option => {
        const key = `${option.selector}|${option.label}`;
        if (!option.selector || seen.has(key)) return false;
        seen.add(key);
        return true;
      });
    }

    function collectJumpOptions(section){
      if (!section) return [];
      const buttons = Array.from(section.querySelectorAll('[data-jump]'));
      const options = buttons.map(button => {
        const rawSelector = (button.getAttribute('data-jump') || '').trim();
        if (!rawSelector.startsWith('#')) return null;
        const target = card.querySelector(rawSelector) || document.querySelector(rawSelector);
        if (!target) return null;
        return {
          selector: rawSelector,
          label: (button.textContent || '').trim() || sectionTitle(target),
          signal: activeSignal(target),
          targetTitle: sectionTitle(target),
          type: classifyOption({ selector: rawSelector, label: (button.textContent || '').trim() })
        };
      }).filter(Boolean);
      return uniqueOptions(options);
    }

    function buildSyntheticOption(targetId, label){
      const selector = normalizeTarget(targetId);
      const target = card.querySelector(selector) || document.querySelector(selector);
      if (!target) return null;
      return {
        selector,
        label: label || sectionTitle(target),
        signal: activeSignal(target),
        targetTitle: sectionTitle(target),
        type: classifyOption({ selector, label: label || sectionTitle(target) })
      };
    }

    function getRouteHint(section){
      return ROUTE_HINTS[section?.id] || {};
    }

    function resolvePrimaryTarget(section){
      const hint = getRouteHint(section);

      if (hint.terminal) return null;

      if (hint.requiresChoice){
        const remembered = branchMemory.get(section.id);
        if (remembered){
          return buildSyntheticOption(remembered, `Resume last branch → ${sectionTitle(card.querySelector(remembered) || document.querySelector(remembered))}`);
        }
        if (hint.defaultNext){
          return buildSyntheticOption(hint.defaultNext);
        }
        return null;
      }

      if (hint.defaultNext === 'return'){
        if (lastNonHubSectionId && lastNonHubSectionId !== section.id){
          return buildSyntheticOption(lastNonHubSectionId, `Return to ${sectionTitle(findSectionById(lastNonHubSectionId))}`);
        }
        return null;
      }

      if (hint.defaultNext){
        return buildSyntheticOption(hint.defaultNext);
      }

      const jumpOptions = collectJumpOptions(section).filter(option => option.type === 'branch');
      if (jumpOptions.length === 1){
        return jumpOptions[0];
      }

      const sequential = getSequentialNext(section);
      if (sequential){
        return buildSyntheticOption(sequential.id, `Next in guide → ${sectionTitle(sequential)}`);
      }

      return null;
    }

    function collectBranchOptions(section, primaryOption){
      const hint = getRouteHint(section);
      const jumpOptions = collectJumpOptions(section);
      const primarySelector = primaryOption?.selector || '';
      const branchOptions = jumpOptions.filter(option => option.selector !== primarySelector && option.type === 'branch');
      const fallbackOptions = jumpOptions.filter(option => option.selector !== primarySelector && option.type === 'fallback');

      if (section?.id === 'sec-hub' && lastNonHubSectionId && lastNonHubSectionId !== section.id){
        const resumeOption = buildSyntheticOption(lastNonHubSectionId, `Return to ${sectionTitle(findSectionById(lastNonHubSectionId))}`);
        if (resumeOption && resumeOption.selector !== primarySelector){
          branchOptions.unshift(resumeOption);
        }
      }

      if (section?.id === 'sec-equity'){
        ['sec-living', 'sec-rop', 'sec-iul'].forEach(targetId => {
          const option = buildSyntheticOption(targetId);
          if (!option || option.selector === primarySelector) return;
          if (!branchOptions.some(item => item.selector === option.selector)){
            branchOptions.push(option);
          }
        });
      }

      if (hint.requiresChoice && primarySelector){
        const primaryTarget = card.querySelector(primarySelector) || document.querySelector(primarySelector);
        const choiceOption = {
          selector: primarySelector,
          label: primaryOption.label,
          signal: primaryOption.signal,
          targetTitle: primaryOption.targetTitle,
          type: 'branch'
        };
        if (!branchOptions.some(item => item.selector === choiceOption.selector)){
          branchOptions.unshift(choiceOption);
        }
      }

      return {
        branches: uniqueOptions(branchOptions),
        fallbacks: uniqueOptions(fallbackOptions)
      };
    }

    function createButton(option, variant){
      return `
        <button
          type="button"
          class="btn ${variant || ''} flow-guide-action ${option.signal || ''}"
          data-flow-target="${escapeHtml(option.selector)}"
          title="${escapeHtml(option.targetTitle || option.label || '')}">
          ${escapeHtml(option.label || option.targetTitle || option.selector)}
        </button>
      `;
    }

    function ensureFlowGuide(){
      let guide = shell.querySelector('[data-flow-guide]');
      if (guide || !main) return guide;
      guide = document.createElement('section');
      guide.className = 'flow-guide-shell';
      guide.setAttribute('data-flow-guide', 'true');
      guide.innerHTML = `
        <div class="flow-guide-head">
          <div class="flow-guide-copy">
            <div class="flow-guide-kicker">Conversation Guide</div>
            <div class="flow-guide-title" data-flow-title>Current lane</div>
            <div class="flow-guide-note" data-flow-note></div>
          </div>
          <div class="flow-guide-meta">
            <div class="flow-guide-step" data-flow-step></div>
            <span class="mini-tag" data-flow-signal>INFO</span>
          </div>
        </div>
        <div class="flow-guide-primary" data-flow-primary></div>
        <div class="flow-guide-branches" data-flow-branches></div>
        <div class="flow-guide-fallbacks" data-flow-fallbacks></div>
      `;

      if (card){
        card.insertAdjacentElement('afterend', guide);
      } else if (focusBanner){
        focusBanner.insertAdjacentElement('afterend', guide);
      } else {
        main.appendChild(guide);
      }

      guide.addEventListener('click', event => {
        const button = event.target.closest('[data-flow-target]');
        if (!button) return;
        const selector = button.getAttribute('data-flow-target');
        if (!selector) return;
        event.preventDefault();
        navigateTo(selector, { push: true, source: 'guide' });
      });

      return guide;
    }

    function updateNextButtons(){
      const current = getCurrentSection();
      const hint = getRouteHint(current);
      const primaryTarget = resolvePrimaryTarget(current);
      const nextButtons = [btnNext, btnNextFocus].filter(Boolean);

      nextButtons.forEach(button => {
        button.classList.remove('is-choice', 'is-terminal');
        button.disabled = false;
        button.textContent = 'Next Step';
        button.title = '';
      });

      if (!nextButtons.length || !current) return;

      if (hint.terminal){
        nextButtons.forEach(button => {
          button.classList.add('is-terminal');
          button.textContent = 'Lane Complete';
          button.title = hint.note || 'This lane usually ends here.';
        });
        return;
      }

      if (hint.requiresChoice && !primaryTarget){
        nextButtons.forEach(button => {
          button.classList.add('is-choice');
          button.textContent = 'Choose Branch';
          button.title = hint.note || 'Pick the matching branch before moving.';
        });
        return;
      }

      if (primaryTarget){
        nextButtons.forEach(button => {
          button.title = primaryTarget.targetTitle || primaryTarget.label || '';
        });
      }
    }

    function renderFlowGuide(){
      const guide = ensureFlowGuide();
      const current = getCurrentSection();
      if (!guide || !current) return;

      const hint = getRouteHint(current);
      const primaryTarget = resolvePrimaryTarget(current);
      const branchGroups = collectBranchOptions(current, primaryTarget);
      const titleEl = guide.querySelector('[data-flow-title]');
      const noteEl = guide.querySelector('[data-flow-note]');
      const stepEl = guide.querySelector('[data-flow-step]');
      const signalEl = guide.querySelector('[data-flow-signal]');
      const primaryEl = guide.querySelector('[data-flow-primary]');
      const branchesEl = guide.querySelector('[data-flow-branches]');
      const fallbacksEl = guide.querySelector('[data-flow-fallbacks]');

      if (titleEl){
        titleEl.textContent = `Current lane: ${sectionTitle(current)}`;
      }

      if (noteEl){
        const pieces = [];
        if (sectionSubtitle(current)) pieces.push(sectionSubtitle(current));
        if (hint.note) pieces.push(hint.note);
        noteEl.textContent = pieces.join(' ');
      }

      if (stepEl){
        const flow = flowText(current);
        stepEl.textContent = flow ? `Step ${stepText(current)} · ${flow}` : `Step ${stepText(current)}`;
      }

      if (signalEl){
        const signal = activeSignal(current);
        signalEl.className = `mini-tag ${signal}`;
        signalEl.textContent = activeSignalLabel(current);
      }

      if (primaryEl){
        if (hint.terminal){
          primaryEl.innerHTML = `
            <div class="flow-guide-terminal">
              <strong>Lane complete.</strong>
              <span>${escapeHtml(hint.note || 'No automatic next step is expected here.')}</span>
            </div>
          `;
        } else if (primaryTarget){
          primaryEl.innerHTML = `
            <div class="flow-guide-primary-card">
              <div class="flow-guide-primary-copy">
                <div class="flow-guide-primary-kicker">Most Likely Next</div>
                <div class="flow-guide-primary-title">${escapeHtml(primaryTarget.targetTitle || primaryTarget.label || 'Next step')}</div>
              </div>
              ${createButton(primaryTarget, 'gold')}
            </div>
          `;
        } else if (hint.requiresChoice){
          primaryEl.innerHTML = `
            <div class="flow-guide-terminal flow-guide-choice">
              <strong>Pick the branch that matches the answer.</strong>
              <span>${escapeHtml(hint.note || 'This step needs a branch choice before moving on.')}</span>
            </div>
          `;
        } else {
          primaryEl.innerHTML = `
            <div class="flow-guide-terminal">
              <strong>No automatic next step.</strong>
              <span>Use the route buttons below if the call branches.</span>
            </div>
          `;
        }
      }

      if (branchesEl){
        if (branchGroups.branches.length){
          branchesEl.innerHTML = `
            <div class="flow-guide-block-label">Likely branches</div>
            <div class="flow-guide-button-row">
              ${branchGroups.branches.map(option => createButton(option, '')).join('')}
            </div>
          `;
        } else {
          branchesEl.innerHTML = '';
        }
      }

      if (fallbacksEl){
        const fallbackOptions = branchGroups.fallbacks.slice();
        if (current.id !== 'sec-hub' && lastNonHubSectionId && lastNonHubSectionId !== current.id){
          const resumeOption = buildSyntheticOption(lastNonHubSectionId, `Return to ${sectionTitle(findSectionById(lastNonHubSectionId))}`);
          if (resumeOption && !fallbackOptions.some(item => item.selector === resumeOption.selector)){
            fallbackOptions.unshift(resumeOption);
          }
        }

        if (fallbackOptions.length){
          fallbacksEl.innerHTML = `
            <div class="flow-guide-block-label">If the call slips</div>
            <div class="flow-guide-button-row compact">
              ${fallbackOptions.map(option => createButton(option, 'dark')).join('')}
            </div>
          `;
        } else {
          fallbacksEl.innerHTML = '';
        }
      }
    }

    function updateProgress(){
      if (progDone) progDone.textContent = String(visitedIds.size);
      if (progTotal) progTotal.textContent = String(orderedSections.length);
      const pct = orderedSections.length ? Math.round((visitedIds.size / orderedSections.length) * 100) : 0;
      if (progFill) progFill.style.width = `${pct}%`;

      if (!sideNav) return;
      sideNav.querySelectorAll('.nav-item').forEach(item => {
        const selector = item.getAttribute('data-target') || '';
        const section = card.querySelector(selector);
        const tick = item.querySelector('.tick');
        if (!tick || !section?.id) return;

        tick.classList.remove('done', 'warn', 'bad');
        if (visitedIds.has(section.id)){
          tick.classList.add('done');
        } else {
          const signal = activeSignal(section);
          if (signal === 'bad') tick.classList.add('bad');
          else if (signal === 'warn') tick.classList.add('warn');
        }
      });
    }

    function updateCurrentState(){
      const current = getCurrentSection();
      orderedSections.forEach(section => section.classList.remove('is-current'));
      sideNav?.querySelectorAll('.nav-item').forEach(item => item.classList.remove('is-active'));
      if (!current) return;

      current.classList.add('is-current');

      const activeNav = sideNav?.querySelector(`.nav-item[data-target="#${current.id}"]`);
      if (activeNav){
        activeNav.classList.add('is-active');
      }
    }

    function buildNav(){
      if (!sideNav) return;
      sideNav.innerHTML = '';

      orderedSections.forEach(section => {
        if (!section.id) return;
        const item = document.createElement('div');
        const nested = !!parentSection(section);
        item.className = `nav-item ${activeSignal(section)}${nested ? ' is-nested' : ''}`;
        item.setAttribute('data-target', `#${section.id}`);
        item.innerHTML = `
          <div class="nav-left">
            <div class="nav-badge">${escapeHtml(stepText(section))}</div>
            <div class="nav-text">
              <div class="nav-title">${escapeHtml(sectionTitle(section))}</div>
              <div class="nav-sub">${escapeHtml(sectionSubtitle(section))}</div>
            </div>
          </div>
          <div class="nav-state">
            <div class="tick" data-tick="#${escapeHtml(section.id)}"></div>
          </div>
        `;
        item.addEventListener('click', () => {
          navigateTo(`#${section.id}`, { push: true, source: 'nav' });
        });
        sideNav.appendChild(item);
      });

      updateProgress();
      updateCurrentState();
    }

    function focusSection(selector){
      const target = card.querySelector(selector) || document.querySelector(selector);
      if (!target) return;
      ensureParentChainOpen(target);
      shell.classList.add('focus-on');
      setFocusTarget(target);
      setCurrentSection(target, { focusMode: true });
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function exitFocus(){
      shell.classList.remove('focus-on');
      orderedSections.forEach(section => section.classList.remove('is-focus'));
      updateCurrentState();
      renderFlowGuide();
      updateNextButtons();
    }

    function navigateTo(selector, opts = {}){
      const normalizedSelector = normalizeTarget(selector);
      const target = card.querySelector(normalizedSelector) || document.querySelector(normalizedSelector);
      if (!target) return false;

      const current = getAnchorSection();
      const origin = current?.id || '';
      const targetTop = topSectionForNode(target) || target;

      if (opts.push !== false && current && current.id && current.id !== targetTop.id){
        navStack.push(`#${current.id}`);
      }

      if (origin && origin !== targetTop.id && opts.recordBranch !== false){
        branchMemory.set(origin, normalizedSelector);
      }

      ensureParentChainOpen(target);
      if (shell.classList.contains('focus-on')){
        setFocusTarget(target);
      }

      setCurrentSection(target, { focusMode: shell.classList.contains('focus-on') });
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return true;
    }

    function handleNextStep(){
      const current = getCurrentSection();
      if (!current) return;

      const hint = getRouteHint(current);
      const primaryTarget = resolvePrimaryTarget(current);

      if (hint.terminal){
        toast('This lane usually ends here.');
        return;
      }

      if (primaryTarget?.selector){
        navigateTo(primaryTarget.selector, { push: true, source: 'next' });
        return;
      }

      if (hint.requiresChoice){
        toast('Pick the branch that matches the answer.');
        renderFlowGuide();
        return;
      }

      const sequential = getSequentialNext(current);
      if (!sequential){
        toast('You’re at the last step.');
        return;
      }

      navigateTo(`#${sequential.id}`, { push: true, source: 'next', recordBranch: false });
    }

    function clearHighlights(){
      shell.querySelectorAll('.script').forEach(block => {
        if (block.dataset.raw){
          block.innerHTML = block.dataset.raw;
          delete block.dataset.raw;
        }
      });
      orderedSections.forEach(section => {
        section.style.display = '';
      });
    }

    function highlightHTMLPreservingTags(html, query){
      const safeQuery = query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const regex = new RegExp(safeQuery, 'gi');
      return html.replace(/>([^<]+)</g, (match, text) => {
        const replaced = text.replace(regex, hit => `<mark class="hit">${hit}</mark>`);
        return `>${replaced}<`;
      });
    }

    function highlightQuery(query){
      if (!query) return;
      let anyVisible = false;
      orderedSections.forEach(section => {
        const text = (section.innerText || '').toLowerCase();
        const hit = text.includes(query.toLowerCase());
        section.style.display = hit ? '' : 'none';
        if (hit) anyVisible = true;
        if (hit){
          section.querySelectorAll('.script').forEach(block => {
            if (!block.dataset.raw) block.dataset.raw = block.innerHTML;
            block.innerHTML = highlightHTMLPreservingTags(block.dataset.raw, query);
          });
        }
      });
      if (!anyVisible){
        orderedSections.forEach(section => {
          section.style.display = '';
        });
      }
    }

    orderedSections.forEach(section => {
      const summary = getSummary(section);
      summary?.addEventListener('click', () => {
        window.requestAnimationFrame(() => {
          setCurrentSection(section, {
            focusMode: shell.classList.contains('focus-on')
          });
        });
      });

      section.addEventListener('toggle', () => {
        updateCurrentState();
      });
    });

    card.querySelectorAll('[data-focus]').forEach(button => {
      button.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        focusSection(button.getAttribute('data-focus') || '');
      });
    });

    card.querySelectorAll('[data-jump]').forEach(button => {
      button.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        navigateTo(button.getAttribute('data-jump') || '', { push: true, source: 'jump' });
      });
    });

    card.querySelectorAll('[data-back]').forEach(button => {
      button.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        const previous = navStack.pop();
        if (!previous){
          toast('No previous step.');
          return;
        }
        navigateTo(previous, { push: false, source: 'back', recordBranch: false });
      });
    });

    btnExitFocus?.addEventListener('click', exitFocus);
    btnNext?.addEventListener('click', handleNextStep);
    btnNextFocus?.addEventListener('click', handleNextStep);

    btnExpand?.addEventListener('click', () => {
      orderedSections.forEach(section => {
        section.open = true;
      });
      updateCurrentState();
    });

    btnCollapse?.addEventListener('click', () => {
      orderedSections.forEach(section => {
        section.open = false;
      });

      const aged = findSectionById('sec-aged') || orderedSections[0];
      if (aged){
        ensureParentChainOpen(aged);
        if (shell.classList.contains('focus-on')){
          setFocusTarget(aged);
        }
        setCurrentSection(aged, {
          focusMode: shell.classList.contains('focus-on'),
          markVisited: false
        });
      }
    });

    if (search){
      search.addEventListener('input', () => {
        const query = (search.value || '').trim();
        clearHighlights();
        if (query.length >= 2) highlightQuery(query);
        updateCurrentState();
        renderFlowGuide();
      });
    }

    btnClear?.addEventListener('click', () => {
      if (search) search.value = '';
      clearHighlights();
      toast('Search cleared.');
      updateCurrentState();
      renderFlowGuide();
    });

    orderedSections.forEach(section => {
      section.classList.remove('good', 'warn', 'bad', 'info');
      section.classList.add(activeSignal(section));
    });

    buildNav();
    const initial = findSectionById('sec-aged')
      || orderedSections.find(section => section.open)
      || orderedSections[0]
      || null;
    if (initial){
      setCurrentSection(initial, { markVisited: true });
    } else {
      renderFlowGuide();
      updateNextButtons();
    }

    return true;
  }

  window.WorkstationScriptGuide = window.WorkstationScriptGuide || {};
  window.WorkstationScriptGuide.initSharedGuide = initSharedGuide;
  window.WorkstationScriptGuide.getRouteSnapshot = buildRouteSnapshot;
  window.WorkstationScriptGuide.getSignalLabel = sharedSignalLabel;
})();
