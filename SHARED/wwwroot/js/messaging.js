(() => {
  const root = document.querySelector('[data-messaging-command-center]');
  if (!root) return;

  const currentUserId = (root.dataset.currentUserId || '').trim().toLowerCase();
  const composePrompt = root.dataset.messagingComposePrompt || 'Choose an authorized contact to begin.';
  const storagePrefix = `masterapp.messaging.${currentUserId || 'current'}.`;
  const token = root.querySelector('#messagingAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const elements = {
    window: root.querySelector('.messaging-command-center-window'),
    close: root.querySelector('#messagingCommandCenterClose'),
    error: root.querySelector('#messagingError'),
    grid: root.querySelector('#messagingCommandCenterGrid'),
    unread: root.querySelector('#messagingCommandCenterUnread'),
    search: root.querySelector('#messagingUniversalSearch'),
    searchResults: root.querySelector('#messagingSearchResults'),
    list: root.querySelector('#messagingConversationList'),
    threadEmpty: root.querySelector('#messagingThreadEmpty'),
    threadContent: root.querySelector('#messagingThreadContent'),
    threadAvatar: root.querySelector('#messagingThreadAvatar'),
    threadTitle: root.querySelector('#messagingThreadTitle'),
    threadSubject: root.querySelector('#messagingThreadSubject'),
    messages: root.querySelector('#messagingMessages'),
    newMessages: root.querySelector('#messagingNewMessages'),
    sendForm: root.querySelector('#messagingSendForm'),
    composeTarget: root.querySelector('#messagingComposeTarget'),
    messageBody: root.querySelector('#messagingMessageBody'),
    files: root.querySelector('#messagingFiles'),
    fileLabel: root.querySelector('.messaging-file-label'),
    sendButton: root.querySelector('#messagingSendButton'),
    mute: root.querySelector('#messagingMuteConversation'),
    closeConversation: root.querySelector('#messagingCloseConversation'),
    journeyOpen: root.querySelector('#messagingJourneyCirclesOpen'),
    journeyPanel: root.querySelector('#messagingJourneyCircles'),
    journeyBack: root.querySelector('#messagingJourneyCirclesBack'),
    journeyStatus: root.querySelector('#messagingJourneyStatus'),
    journeyProfileForm: root.querySelector('#messagingJourneyProfileForm'),
    journeyRecommendations: root.querySelector('#messagingJourneyRecommendations'),
    journeyRequests: root.querySelector('#messagingJourneyRequests'),
    journeyConnections: root.querySelector('#messagingJourneyConnections')
  };
  const unreadBadges = Array.from(document.querySelectorAll('[data-messaging-unread-badge]'));
  const state = {
    conversations: [],
    recipients: [],
    recipientMatches: [],
    recipientMatchesQuery: '',
    recipientsLoaded: false,
    active: null,
    draftTarget: null,
    drafts: readSession('drafts', {}),
    scrollPositions: readSession('scroll-positions', {}),
    searchTimer: null,
    searchAbortController: null,
    searchRequestId: 0,
    isSearchingContacts: false,
    searchResultNodes: new Map(),
    searchStatusNode: null,
    pollTimer: null,
    realtime: null,
    realtimeStarted: false,
    isOpen: false,
    isOpening: false,
    isJourneyOpen: false,
    journeyDashboard: null,
    lastTrigger: null,
    pendingSubmission: null
  };

  function readSession(key, fallback) {
    try {
      const stored = window.sessionStorage.getItem(`${storagePrefix}${key}`);
      return stored ? JSON.parse(stored) : fallback;
    } catch (_) {
      return fallback;
    }
  }

  function writeSession(key, value) {
    try {
      window.sessionStorage.setItem(`${storagePrefix}${key}`, JSON.stringify(value));
    } catch (_) {
      // Session storage is a convenience only; the active in-page draft remains available.
    }
  }

  function removeSession(key) {
    try {
      window.sessionStorage.removeItem(`${storagePrefix}${key}`);
    } catch (_) {
      // Session storage is a convenience only; the active in-page state remains available.
    }
  }

  function isCommandCenterMarkedOpen() {
    try {
      return window.localStorage.getItem(`${storagePrefix}command-center-open`) === 'true';
    } catch (_) {
      return false;
    }
  }

  function markCommandCenterOpen() {
    try {
      window.localStorage.setItem(`${storagePrefix}command-center-open`, 'true');
    } catch (_) {
      // The active modal remains open even if persistent browser storage is unavailable.
    }
  }

  function clearCommandCenterOpenMark() {
    try {
      window.localStorage.removeItem(`${storagePrefix}command-center-open`);
    } catch (_) {
      // The modal has already closed in the current document.
    }
  }

  function isJourneyCirclesMarkedOpen() {
    try {
      return window.localStorage.getItem(`${storagePrefix}journey-circles-open`) === 'true';
    } catch (_) {
      return false;
    }
  }

  function markJourneyCirclesOpen() {
    try {
      window.localStorage.setItem(`${storagePrefix}journey-circles-open`, 'true');
    } catch (_) {
      // The active panel remains available even when persistent browser storage is unavailable.
    }
  }

  function clearJourneyCirclesOpenMark() {
    try {
      window.localStorage.removeItem(`${storagePrefix}journey-circles-open`);
    } catch (_) {
      // The panel has already closed in the current document.
    }
  }

  function normalize(value) {
    return (value || '').trim().toLowerCase();
  }

  function normalizeSearch(value) {
    return normalize(value)
      .replace(/[^\p{L}\p{N}]+/gu, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  function createTextElement(tag, className, value) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    element.textContent = value || '';
    return element;
  }

  function initials(value) {
    const letters = (value || 'Conversation')
      .trim()
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map(part => part[0]?.toUpperCase())
      .join('');
    return letters || 'C';
  }

  function roleLabel(participantType) {
    if (participantType === 'Agent') return 'Agent';
    if (participantType === 'Client') return 'Client';
    return 'Participant';
  }

  function createAvatar(person, loading = 'lazy') {
    const displayName = person?.displayName || 'Participant';
    const avatar = document.createElement('span');
    avatar.className = 'messaging-avatar';
    avatar.setAttribute('role', 'img');
    avatar.setAttribute('aria-label', `${displayName} profile image`);

    const fallback = createTextElement('span', 'messaging-avatar-fallback', initials(displayName));
    fallback.setAttribute('aria-hidden', 'true');
    avatar.append(fallback);

    if (!person?.userId || !person?.participantType) return avatar;

    const image = document.createElement('img');
    image.src = `/Messaging/Participants/${encodeURIComponent(person.userId)}/Avatar?participantType=${encodeURIComponent(person.participantType)}`;
    image.alt = '';
    image.loading = loading;
    image.decoding = 'async';
    image.addEventListener('load', () => avatar.classList.add('has-image'), { once: true });
    image.addEventListener('error', () => image.remove(), { once: true });
    avatar.append(image);
    return avatar;
  }

  function parseUtcTimestamp(value) {
    if (!value) return null;

    const timestamp = String(value).trim();
    if (!timestamp) return null;

    // Messaging timestamps are transported as UTC. Preserve an explicit offset when
    // present; otherwise make the UTC contract explicit before the browser converts
    // the value to the user's current local time zone.
    const isoTimestamp = timestamp.includes('T') ? timestamp : timestamp.replace(' ', 'T');
    const zonedTimestamp = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(isoTimestamp)
      ? isoTimestamp
      : `${isoTimestamp}Z`;
    const date = new Date(zonedTimestamp);
    return Number.isNaN(date.getTime()) ? null : date;
  }

  function formatConversationTime(value) {
    const date = parseUtcTimestamp(value);
    if (!date) return '';
    const now = new Date();
    const isToday = date.toDateString() === now.toDateString();
    if (isToday) {
      return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    }
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    if (date.toDateString() === yesterday.toDateString()) return 'Yesterday';
    return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  function formatMessageTime(value) {
    const date = parseUtcTimestamp(value);
    return date ? date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : '';
  }

  function dayLabel(value) {
    const date = parseUtcTimestamp(value);
    if (!date) return '';
    const now = new Date();
    if (date.toDateString() === now.toDateString()) return 'Today';
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    if (date.toDateString() === yesterday.toDateString()) return 'Yesterday';
    return date.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' });
  }

  function showError(message) {
    if (!message) {
      elements.error.hidden = true;
      elements.error.textContent = '';
      return;
    }
    elements.error.textContent = message;
    elements.error.hidden = false;
  }

  function clientMessageId() {
    return window.crypto?.randomUUID?.() || `messaging-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }

  function requestHeaders(json) {
    const headers = { 'X-Requested-With': 'XMLHttpRequest' };
    if (token) headers.RequestVerificationToken = token;
    if (json) headers['Content-Type'] = 'application/json';
    return headers;
  }

  async function request(url, options = {}) {
    let response;
    try {
      response = await fetch(url, {
        credentials: 'same-origin',
        ...options,
        headers: {
          ...requestHeaders(Boolean(options.body && typeof options.body === 'string')),
          ...(options.headers || {})
        }
      });
    } catch (error) {
      if (error?.name === 'AbortError') throw error;
      throw new Error('Messaging is temporarily unavailable. Please try again.');
    }
    let data = null;
    try { data = await response.json(); } catch (_) { }
    if (!response.ok) {
      throw new Error(data?.errorMessage || 'The messaging request could not be completed.');
    }
    return data;
  }

  function supportsJourneyCircles() {
    return Boolean(elements.journeyOpen && elements.journeyPanel && elements.journeyProfileForm);
  }

  function setJourneyStatus(message, isError = false) {
    if (!elements.journeyStatus) return;
    elements.journeyStatus.hidden = !message;
    elements.journeyStatus.textContent = message || '';
    elements.journeyStatus.classList.toggle('is-error', Boolean(message && isError));
  }

  function selectedValues(select) {
    return Array.from(select?.selectedOptions || []).map(option => option.value).filter(Boolean);
  }

  function setSelectedValues(select, values) {
    if (!select) return;
    const selected = new Set((values || []).map(normalize));
    Array.from(select.options).forEach(option => {
      option.selected = selected.has(normalize(option.value));
    });
  }

  function replaceJourneyOptions(name, values, selected) {
    const select = elements.journeyProfileForm?.elements.namedItem(name);
    if (!(select instanceof HTMLSelectElement)) return;
    select.replaceChildren();
    (values || []).forEach(value => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = value;
      select.append(option);
    });
    setSelectedValues(select, selected);
  }

  function journeyFormField(name) {
    return elements.journeyProfileForm?.elements.namedItem(name);
  }

  function setJourneyBoolean(name, value) {
    const field = journeyFormField(name);
    if (field instanceof HTMLInputElement) field.value = String(Boolean(value));
  }

  function createJourneyEmpty(message) {
    return createTextElement('p', 'messaging-journey-empty', message);
  }

  function createJourneyCard(profile, detail, actions) {
    const card = document.createElement('article');
    card.className = 'messaging-journey-card';
    card.append(createTextElement('h5', '', profile?.displayName || 'Journey member'));
    if (detail) card.append(createTextElement('p', '', detail));
    if (actions?.length) {
      const actionRow = document.createElement('div');
      actionRow.className = 'messaging-journey-card-actions';
      actions.forEach(action => {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = action.label;
        button.addEventListener('click', action.run);
        actionRow.append(button);
      });
      card.append(actionRow);
    }
    return card;
  }

  function journeyFormData(values = {}) {
    const data = new FormData();
    if (token) data.append('__RequestVerificationToken', token);
    Object.entries(values).forEach(([name, value]) => data.append(name, String(value)));
    return data;
  }

  async function runJourneyAction(url, data, successMessage) {
    setJourneyStatus('Saving…');
    try {
      const dashboard = await request(url, {
        method: 'POST',
        body: data,
        headers: { 'X-Journey-Circles-Modal': '1' }
      });
      state.journeyDashboard = dashboard;
      renderJourneyDashboard();
      setJourneyStatus(successMessage);
      state.recipientsLoaded = false;
      await loadRecipients();
    } catch (error) {
      setJourneyStatus(error.message, true);
    }
  }

  function renderJourneyCards(container, items, emptyMessage, buildCard) {
    if (!container) return;
    container.replaceChildren();
    if (!items?.length) {
      container.append(createJourneyEmpty(emptyMessage));
      return;
    }
    items.forEach(item => container.append(buildCard(item)));
  }

  function returnToMessages(profile) {
    closeJourneyCircles();
    if (profile?.displayName) {
      elements.search.value = profile.displayName;
      renderSearchResults();
    }
    elements.search.focus({ preventScroll: true });
  }

  function renderJourneyDashboard() {
    if (!supportsJourneyCircles()) return;
    const dashboard = state.journeyDashboard || {};
    const profile = dashboard.profile || null;
    const preferences = dashboard.preferences || {};
    const privacy = journeyFormField('PrivacyChoices');
    if (privacy instanceof HTMLSelectElement) {
      setSelectedValues(privacy, [
        preferences.consentAffirmed ? 'consent' : '',
        preferences.isOptedIn ? 'opt-in' : '',
        preferences.isDiscoverable ? 'discoverable' : '',
        preferences.allowSuggestions ? 'suggestions' : '',
        preferences.allowConnectionRequests ? 'requests' : ''
      ]);
    }
    setJourneyBoolean('ConsentAffirmed', preferences.consentAffirmed);
    setJourneyBoolean('IsOptedIn', preferences.isOptedIn);
    setJourneyBoolean('IsDiscoverable', preferences.isDiscoverable);
    setJourneyBoolean('AllowSuggestions', preferences.allowSuggestions);
    setJourneyBoolean('AllowConnectionRequests', preferences.allowConnectionRequests);
    replaceJourneyOptions('LifeStages', dashboard.lifeStages, profile?.lifeStages);
    replaceJourneyOptions('Locations', dashboard.locations, profile?.locations);
    replaceJourneyOptions('Goals', dashboard.goals, profile?.goals);
    replaceJourneyOptions('Interests', dashboard.interests, profile?.interests);
    replaceJourneyOptions('CircleCodes', dashboard.circles, profile?.circleCodes);
    replaceJourneyOptions('ConnectionTypes', dashboard.connectionTypes, profile?.connectionTypes);
    replaceJourneyOptions('CommunicationStyles', dashboard.communicationStyles, profile?.communicationStyles);
    replaceJourneyOptions('AccountabilityFrequencies', dashboard.accountabilityFrequencies, profile?.accountabilityFrequencies);
    const introduction = journeyFormField('Introduction');
    if (introduction instanceof HTMLTextAreaElement) introduction.value = profile?.introduction || '';

    renderJourneyCards(
      elements.journeyRecommendations,
      dashboard.recommendations,
      profile ? 'No recommendations match your current selections yet.' : 'Save your profile to receive relevant recommendations.',
      recommendation => createJourneyCard(recommendation.profile, recommendation.explanation, [{
        label: 'Connect',
        run: () => runJourneyAction('/JourneyCircles/Connections', journeyFormData({ targetClientProfileId: recommendation.profile.clientProfileId }), 'Connection request sent.')
      }]));
    renderJourneyCards(
      elements.journeyRequests,
      dashboard.requests,
      'No incoming requests.',
      connection => createJourneyCard(connection.profile, connection.introduction || 'Connection request', [
        { label: 'Accept', run: () => runJourneyAction(`/JourneyCircles/Connections/${encodeURIComponent(connection.id)}/Response`, journeyFormData({ accept: true }), 'Connection accepted.') },
        { label: 'Decline', run: () => runJourneyAction(`/JourneyCircles/Connections/${encodeURIComponent(connection.id)}/Response`, journeyFormData({ accept: false }), 'Connection declined.') }
      ]));
    renderJourneyCards(
      elements.journeyConnections,
      dashboard.connections,
      'No active connections yet.',
      connection => createJourneyCard(connection.profile, connection.profile?.goals?.slice(0, 2).join(' · ') || 'Accepted Journey Circles connection', [
        { label: 'Message', run: () => returnToMessages(connection.profile) },
        { label: 'Disconnect', run: () => runJourneyAction(`/JourneyCircles/Connections/${encodeURIComponent(connection.id)}/Disconnect`, journeyFormData(), 'Connection removed.') },
        { label: 'Block', run: () => runJourneyAction(`/JourneyCircles/Profiles/${encodeURIComponent(connection.profile.clientProfileId)}/Block`, journeyFormData(), 'Connection blocked.') },
        { label: 'Report', run: () => runJourneyAction(`/JourneyCircles/Profiles/${encodeURIComponent(connection.profile.clientProfileId)}/Report`, journeyFormData({ category: 'Safety concern' }), 'Report submitted.') }
      ]));
  }

  async function loadJourneyDashboard() {
    const dashboard = await request('/JourneyCircles/Modal');
    state.journeyDashboard = dashboard;
    renderJourneyDashboard();
  }

  async function openJourneyCircles() {
    if (!supportsJourneyCircles()) return;
    if (!state.isOpen) await openCommandCenter(null);
    if (!state.isOpen) return;
    state.isJourneyOpen = true;
    elements.grid.hidden = true;
    elements.journeyPanel.hidden = false;
    elements.journeyOpen.setAttribute('aria-expanded', 'true');
    markJourneyCirclesOpen();
    setJourneyStatus('');
    try {
      await loadJourneyDashboard();
    } catch (error) {
      setJourneyStatus(error.message, true);
    }
  }

  function closeJourneyCircles() {
    if (!supportsJourneyCircles() || !state.isJourneyOpen) return;
    state.isJourneyOpen = false;
    elements.journeyPanel.hidden = true;
    elements.grid.hidden = false;
    elements.journeyOpen.setAttribute('aria-expanded', 'false');
    clearJourneyCirclesOpenMark();
    elements.search.focus({ preventScroll: true });
  }

  function saveJourneyProfile(event) {
    event.preventDefault();

    const form = elements.journeyProfileForm;
    if (!(form instanceof HTMLFormElement)) {
      setJourneyStatus('Journey Circles profile form is unavailable.', true);
      return;
    }

    const privacy = new Set(selectedValues(journeyFormField('PrivacyChoices')));
    const data = new FormData(form);

    data.set('ConsentAffirmed', String(privacy.has('consent')));
    data.set('IsOptedIn', String(privacy.has('opt-in')));
    data.set('IsDiscoverable', String(privacy.has('discoverable')));
    data.set('AllowSuggestions', String(privacy.has('suggestions')));
    data.set('AllowConnectionRequests', String(privacy.has('requests')));

    runJourneyAction('/JourneyCircles/Profile', data, 'Journey Circles profile saved.');
  }

  function activeDraftKey() {
    if (state.active?.id) return `conversation:${state.active.id}`;
    if (state.draftTarget?.userId) return `recipient:${normalize(state.draftTarget.userId)}`;
    return null;
  }

  function saveDraft() {
    const key = activeDraftKey();
    if (!key) return;
    const body = elements.messageBody.value;
    if (body) state.drafts[key] = body;
    else delete state.drafts[key];
    writeSession('drafts', state.drafts);
  }

  function restoreDraft() {
    elements.messageBody.value = state.drafts[activeDraftKey()] || '';
  }

  function setUnreadCount() {
    const count = state.conversations.reduce((total, conversation) => total + (conversation.unreadCount || 0), 0);
    const label = count > 99 ? '99+' : String(count);
    elements.unread.textContent = count > 0 ? `${label} unread` : '';
    elements.unread.hidden = count === 0;
    unreadBadges.forEach(badge => {
      badge.textContent = count > 0 ? label : '';
      badge.hidden = count === 0;
      badge.setAttribute('aria-label', count > 0 ? `${count} unread messages` : '');
    });
  }

  function renderConversations() {
    elements.list.replaceChildren();
    if (state.conversations.length === 0) {
      elements.list.append(createTextElement('p', 'messaging-list-empty', 'No conversations yet.'));
      return;
    }

    state.conversations.forEach(conversation => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'messaging-conversation-item';
      if (state.active?.id === conversation.id) button.classList.add('is-active');
      if (conversation.unreadCount > 0) button.classList.add('is-unread');

      const identity = document.createElement('span');
      identity.className = 'messaging-conversation-identity';
      identity.append(createAvatar(conversation.counterparty));
      const copy = document.createElement('span');
      copy.className = 'messaging-conversation-copy';
      copy.append(createTextElement('span', 'messaging-conversation-title', conversation.counterparty?.displayName || 'Conversation'));
      copy.append(createTextElement('span', 'messaging-conversation-preview', conversation.lastMessagePreview || conversation.subject || 'No messages yet.'));
      if (state.drafts[`conversation:${conversation.id}`]) {
        copy.append(createTextElement('span', 'messaging-conversation-draft', 'Draft'));
      }
      identity.append(copy);
      button.append(identity);

      const meta = document.createElement('span');
      meta.className = 'messaging-conversation-meta';
      meta.append(createTextElement('time', 'messaging-conversation-time', formatConversationTime(conversation.lastMessageUtc)));
      if (conversation.unreadCount > 0) {
        meta.append(createTextElement('span', 'messaging-unread-count', String(conversation.unreadCount)));
      }
      button.append(meta);
      button.addEventListener('click', () => loadConversation(conversation.id, true).catch(error => showError(error.message)));
      elements.list.append(button);
    });
  }

  function participantName(conversation, userId) {
    return conversation.participants?.find(participant => normalize(participant.userId) === normalize(userId))?.displayName || 'Participant';
  }

  function currentCounterparty(conversation) {
    return conversation?.participants?.find(participant => normalize(participant.userId) !== currentUserId) || null;
  }

  function setComposerState(target, isClosed) {
    const isAvailable = Boolean(target?.contactKey || state.active?.id) && !isClosed;
    elements.messageBody.disabled = !isAvailable;
    elements.files.disabled = !isAvailable;
    elements.sendButton.disabled = !isAvailable;
    elements.fileLabel.classList.toggle('is-disabled', !isAvailable);
    if (!target) elements.composeTarget.textContent = composePrompt;
    else if (isClosed) elements.composeTarget.textContent = 'This conversation is closed.';
    else elements.composeTarget.textContent = `Secure message to ${target.displayName || 'recipient'}.`;
  }

  function restoreMessageScroll(conversationId, shouldScrollToBottom) {
    window.requestAnimationFrame(() => {
      const saved = state.scrollPositions[conversationId];
      elements.messages.scrollTop = shouldScrollToBottom || typeof saved !== 'number'
        ? elements.messages.scrollHeight
        : saved;
      if (shouldScrollToBottom) elements.newMessages.hidden = true;
    });
  }

  function isNearMessageBottom() {
    return elements.messages.scrollHeight - elements.messages.scrollTop - elements.messages.clientHeight < 96;
  }

  function renderConversation(shouldScrollToBottom = false) {
    const conversation = state.active;
    const target = conversation ? currentCounterparty(conversation) : state.draftTarget;
    const isClosed = conversation?.isClosed === true;
    const isDraft = !conversation && Boolean(target);

    elements.threadEmpty.hidden = Boolean(conversation || isDraft);
    elements.threadContent.hidden = !(conversation || isDraft);
    elements.messages.replaceChildren();
    elements.mute.hidden = !conversation;
    elements.closeConversation.hidden = !conversation;

    if (!conversation && !isDraft) {
      setComposerState(null, false);
      restoreDraft();
      return;
    }

    elements.threadAvatar.replaceChildren(createAvatar(target, 'eager'));
    elements.threadTitle.textContent = target?.displayName || 'Conversation';
    elements.threadSubject.textContent = [
      roleLabel(target?.participantType),
      'Secure conversation',
      conversation?.subject || (isDraft ? 'New secure conversation' : '')
    ].filter(Boolean).join(' · ');
    if (conversation) {
      elements.mute.textContent = conversation.isMuted ? 'Unmute' : 'Mute';
      elements.closeConversation.textContent = isClosed ? 'Reopen' : 'Close';

      let previousDay = '';
      (conversation.messages || []).forEach(message => {
        const label = dayLabel(message.sentUtc);
        if (label && label !== previousDay) {
          elements.messages.append(createTextElement('p', 'messaging-day-divider', label));
          previousDay = label;
        }

        const card = document.createElement('article');
        card.className = 'messaging-message';
        const isOwn = normalize(message.senderUserId) === currentUserId;
        if (isOwn) card.classList.add('is-own');
        const meta = document.createElement('div');
        meta.className = 'messaging-message-meta';
        meta.append(createTextElement('span', 'messaging-message-sender', isOwn ? 'You' : participantName(conversation, message.senderUserId)));
        meta.append(createTextElement('time', '', formatMessageTime(message.sentUtc)));
        if (message.editedUtc) meta.append(createTextElement('span', 'messaging-message-edited', 'Edited'));
        card.append(meta);
        card.append(createTextElement('p', 'messaging-message-body', message.isDeleted ? 'This message was deleted.' : message.body));

        if (message.attachments?.length) {
          const attachments = document.createElement('div');
          attachments.className = 'messaging-attachments';
          message.attachments.forEach(attachment => {
            const status = attachment.scanStatus || 'Pending';
            if (attachment.canDownload) {
              const link = document.createElement('a');
              link.className = 'messaging-attachment';
              link.href = `/Messaging/Attachments/${encodeURIComponent(attachment.id)}`;
              link.textContent = attachment.originalFileName;
              link.setAttribute('download', attachment.originalFileName || 'attachment');
              attachments.append(link);
            } else {
              attachments.append(createTextElement(
                'span',
                `messaging-attachment is-${normalize(status) || 'pending'}`,
                `${attachment.originalFileName} — ${status}`));
            }
          });
          card.append(attachments);
        }
        elements.messages.append(card);
      });
      restoreMessageScroll(conversation.id, shouldScrollToBottom);
    } else {
      elements.messages.append(createTextElement('p', 'messaging-draft-intro', 'Write the first message to start this secure conversation.'));
    }

    setComposerState(target, isClosed);
    restoreDraft();
  }

  function searchText(conversation) {
    return [
      conversation.counterparty?.displayName,
      conversation.subject,
      conversation.lastMessagePreview
    ].filter(Boolean).join(' ');
  }

  function matchesSearch(value, query) {
    const normalizedQuery = normalizeSearch(query);
    if (!normalizedQuery) return true;
    const normalizedValue = normalizeSearch(value);
    return normalizedQuery.split(' ').every(token => normalizedValue.includes(token));
  }

  function searchRank(person, query, conversation) {
    const normalizedQuery = normalizeSearch(query);
    const displayName = normalizeSearch(person?.displayName);
    const email = normalizeSearch(person?.email);
    if (normalizedQuery === displayName || normalizedQuery === email) return 0;
    if (conversation) return conversation.unreadCount > 0 ? 2 : 1;
    if (displayName.startsWith(normalizedQuery) || email.startsWith(normalizedQuery)) return 3;
    return 4;
  }

  function searchResultKey(kind, userId, participantType) {
    return `${kind}:${normalize(participantType)}:${normalize(userId)}`;
  }

  function createSearchResultNode() {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = 'messaging-search-result';
    const copy = document.createElement('span');
    const title = document.createElement('strong');
    const subtitle = document.createElement('small');
    copy.append(title, subtitle);
    item.append(copy);
    item.addEventListener('click', () => item._messagingSelect?.());
    item._messagingCopy = copy;
    item._messagingTitle = title;
    item._messagingSubtitle = subtitle;
    return item;
  }

  function updateSearchResultNode(item, result) {
    const identity = `${normalize(result.person?.userId)}:${normalize(result.person?.participantType)}`;
    if (item.dataset.identity !== identity) {
      item.dataset.identity = identity;
      item.querySelector('.messaging-avatar')?.remove();
      item.insertBefore(createAvatar(result.person), item._messagingCopy);
    }
    item._messagingTitle.textContent = result.title;
    item._messagingSubtitle.textContent = result.subtitle || '';
    item._messagingSubtitle.hidden = !result.subtitle;
    item._messagingSelect = result.select;
  }

  function setSearchStatus(message) {
    if (!message) {
      state.searchStatusNode?.remove();
      state.searchStatusNode = null;
      return;
    }

    if (!state.searchStatusNode) {
      state.searchStatusNode = createTextElement('p', 'messaging-search-empty', message);
    } else {
      state.searchStatusNode.textContent = message;
    }
    elements.searchResults.append(state.searchStatusNode);
  }

  function renderSearchResults() {
    const query = normalizeSearch(elements.search.value);
    if (!query) {
      state.searchResultNodes.forEach(item => item.remove());
      state.searchResultNodes.clear();
      setSearchStatus('');
      elements.searchResults.hidden = true;
      return;
    }

    const matchingConversations = state.conversations
      .filter(conversation => matchesSearch(searchText(conversation), query))
      .sort((left, right) =>
        searchRank(left.counterparty, query, left) - searchRank(right.counterparty, query, right) ||
        (parseUtcTimestamp(right.lastMessageUtc)?.getTime() || 0) -
        (parseUtcTimestamp(left.lastMessageUtc)?.getTime() || 0));
    const existingCounterparties = new Set(matchingConversations.map(conversation => normalize(conversation.counterparty?.userId)));
    const existingConversationIds = new Set(matchingConversations.map(conversation => conversation.id));
    const recipientSource = state.recipientMatchesQuery === query
      ? state.recipientMatches
      : state.recipients;
    const matchingRecipients = recipientSource.filter(recipient =>
      !existingCounterparties.has(normalize(recipient.userId)) &&
      !existingConversationIds.has(recipient.existingConversationId) &&
      matchesSearch([recipient.displayName, recipient.email].filter(Boolean).join(' '), query))
      .sort((left, right) =>
        searchRank(left, query) - searchRank(right, query) ||
        left.displayName.localeCompare(right.displayName));

    const results = [
      ...matchingConversations.map(conversation => ({
        key: searchResultKey('conversation', conversation.id, 'conversation'),
        person: conversation.counterparty,
        title: conversation.counterparty?.displayName || 'Conversation',
        subtitle: `Existing conversation${conversation.unreadCount > 0 ? ` · ${conversation.unreadCount} unread` : ''}`,
        select: () => {
          elements.search.value = '';
          renderSearchResults();
          loadConversation(conversation.id, true).catch(error => showError(error.message));
        }
      })),
      ...matchingRecipients.map(recipient => ({
        key: searchResultKey('recipient', recipient.userId, recipient.participantType),
        person: recipient,
        title: recipient.displayName,
        subtitle: [
          recipient.existingConversationId ? 'Existing conversation' : recipient.relationshipLabel,
          recipient.email
        ].filter(Boolean).join(' · '),
        select: () => {
          if (recipient.existingConversationId) {
            elements.search.value = '';
            renderSearchResults();
            loadConversation(recipient.existingConversationId, true).catch(error => showError(error.message));
            return;
          }
          state.active = null;
          state.draftTarget = recipient;
          state.pendingSubmission = null;
          elements.search.value = '';
          renderSearchResults();
          renderConversations();
          renderConversation();
          elements.messageBody.focus({ preventScroll: true });
        }
      }))
    ];

    const desiredKeys = new Set();
    results.forEach(result => {
      desiredKeys.add(result.key);
      let item = state.searchResultNodes.get(result.key);
      if (!item) {
        item = createSearchResultNode();
        state.searchResultNodes.set(result.key, item);
      }
      updateSearchResultNode(item, result);
      elements.searchResults.append(item);
    });
    state.searchResultNodes.forEach((item, key) => {
      if (!desiredKeys.has(key)) {
        item.remove();
        state.searchResultNodes.delete(key);
      }
    });

    if (!results.length) {
      setSearchStatus(state.isSearchingContacts
        ? 'Searching authorized contacts…'
        : 'No authorized conversations or recipients found.');
    } else {
      setSearchStatus('');
    }
    elements.searchResults.hidden = false;
  }

  async function refreshList() {
    const result = await request('/Messaging/Conversations');
    state.conversations = result.conversations || [];
    setUnreadCount();
    renderConversations();
    renderSearchResults();
  }

  async function loadConversation(conversationId, markRead, shouldScrollToBottom = false) {
    if (state.active?.id !== conversationId) elements.newMessages.hidden = true;
    if (state.active?.id) {
      state.scrollPositions[state.active.id] = elements.messages.scrollTop;
      writeSession('scroll-positions', state.scrollPositions);
    }
    const result = await request(`/Messaging/Conversations/${encodeURIComponent(conversationId)}`);
    state.active = result.conversation;
    state.draftTarget = null;
    state.pendingSubmission = null;
    writeSession('last-conversation', conversationId);
    renderConversation(shouldScrollToBottom);
    renderConversations();
    if (markRead) {
      try {
        await request(`/Messaging/Conversations/${encodeURIComponent(conversationId)}/Read`, { method: 'POST' });
        await refreshList();
      } catch (error) {
        showError(error.message);
      }
    }
  }

  async function loadRecipients() {
    if (state.recipientsLoaded) return;
    const result = await request('/Messaging/Recipients');
    state.recipients = result.recipients || [];
    if (!state.recipientMatchesQuery) state.recipientMatches = state.recipients;
    state.recipientsLoaded = true;
    renderSearchResults();
  }

  async function searchRecipients(query, requestId) {
    const normalizedQuery = normalizeSearch(query);
    if (!normalizedQuery) {
      if (requestId !== state.searchRequestId) return;
      state.isSearchingContacts = false;
      state.recipientMatches = state.recipients;
      state.recipientMatchesQuery = '';
      renderSearchResults();
      return;
    }

    const controller = new AbortController();
    state.searchAbortController = controller;
    state.isSearchingContacts = true;
    renderSearchResults();
    try {
      const result = await request(`/Messaging/Recipients?search=${encodeURIComponent(query)}`, { signal: controller.signal });
      if (requestId !== state.searchRequestId) return;
      state.recipientMatches = result.recipients || [];
      state.recipientMatchesQuery = normalizedQuery;
    } catch (error) {
      if (error?.name !== 'AbortError' && requestId === state.searchRequestId) {
        showError(error.message);
      }
    } finally {
      if (requestId === state.searchRequestId) {
        state.isSearchingContacts = false;
        renderSearchResults();
      }
    }
  }

  function renderSelectedFiles() {
    const count = elements.files.files?.length || 0;
    elements.fileLabel.textContent = count > 0
      ? `${count} file${count === 1 ? '' : 's'} selected`
      : 'Attach files';
  }

  async function uploadAttachments(messageId, submission) {
    const files = Array.from(elements.files.files || []);
    for (let index = 0; index < files.length; index += 1) {
      if (submission.uploadedFileIndexes.includes(index)) continue;
      const formData = new FormData();
      formData.append('file', files[index]);
      await request(`/Messaging/Messages/${encodeURIComponent(messageId)}/Attachments`, {
        method: 'POST',
        body: formData,
        headers: token ? { RequestVerificationToken: token } : {}
      });
      submission.uploadedFileIndexes.push(index);
    }
  }

  function createSubmission(body) {
    const key = activeDraftKey();
    if (state.pendingSubmission?.key === key && state.pendingSubmission.body === body) return state.pendingSubmission;
    state.pendingSubmission = {
      key,
      body,
      clientMessageId: clientMessageId(),
      messageId: null,
      uploadedFileIndexes: [],
      draftKeys: [key]
    };
    return state.pendingSubmission;
  }

  async function sendMessage() {
    const body = elements.messageBody.value.trim();
    if (!body || (!state.active && !state.draftTarget)) return;

    const submission = createSubmission(body);
    elements.sendButton.disabled = true;
    showError('');
    try {
      if (!submission.messageId) {
        if (state.active) {
          const result = await request(`/Messaging/Conversations/${encodeURIComponent(state.active.id)}/Messages`, {
            method: 'POST',
            body: JSON.stringify({ body, clientMessageId: submission.clientMessageId })
          });
          submission.messageId = result.message?.id;
        } else {
          const target = state.draftTarget;
          const result = await request('/Messaging/Conversations', {
            method: 'POST',
            body: JSON.stringify({
              contactKey: target.contactKey,
              subject: null,
              body,
              clientMessageId: submission.clientMessageId
            })
          });
          const created = result.conversation;
          submission.messageId = [...(created?.messages || [])]
            .reverse()
            .find(message => normalize(message.senderUserId) === currentUserId && message.body === body)?.id || null;
          state.active = created;
          state.draftTarget = null;
          submission.key = activeDraftKey();
          if (!submission.draftKeys.includes(submission.key)) submission.draftKeys.push(submission.key);
        }
      }

      if (!submission.messageId) throw new Error('The message was created, but its attachment target could not be determined.');
      await uploadAttachments(submission.messageId, submission);
      const sentConversationId = state.active?.id;
      submission.draftKeys.forEach(key => delete state.drafts[key]);
      writeSession('drafts', state.drafts);
      elements.messageBody.value = '';
      elements.files.value = '';
      renderSelectedFiles();
      state.pendingSubmission = null;
      if (sentConversationId) await loadConversation(sentConversationId, false);
      await refreshList();
      renderConversation(true);
    } catch (error) {
      showError(error.message);
    } finally {
      elements.sendButton.disabled = Boolean(state.active?.isClosed) || (!state.active && !state.draftTarget);
    }
  }

  async function notifyConversationAction(path, body) {
    if (!state.active) return;
    await request(`/Messaging/Conversations/${encodeURIComponent(state.active.id)}/${path}`, {
      method: 'POST',
      body: JSON.stringify(body)
    });
    await loadConversation(state.active.id, false);
    await refreshList();
  }

  function startPolling() {
    if (state.pollTimer) return;
    state.pollTimer = window.setInterval(() => {
      refreshList().catch(() => { });
    }, 45000);
  }

  function stopPolling() {
    if (!state.pollTimer) return;
    window.clearInterval(state.pollTimer);
    state.pollTimer = null;
  }

  async function startRealtime() {
    if (state.realtimeStarted) return;
    state.realtimeStarted = true;

    const connection = new window.signalR.HubConnectionBuilder()
      .withUrl('/messaginghub')
      .withAutomaticReconnect()
      .build();
    state.realtime = connection;
    const refreshForEvent = async event => {
      try {
        await refreshList();
        if (state.active && event?.conversationId === state.active.id) {
          const shouldScrollToBottom = isNearMessageBottom();
          await loadConversation(state.active.id, false, shouldScrollToBottom);
          if (!shouldScrollToBottom) elements.newMessages.hidden = false;
        }
      } catch (_) { }
    };
    connection.on('messageReceived', refreshForEvent);
    connection.on('conversationUpdated', refreshForEvent);
    connection.onreconnecting(startPolling);
    connection.onreconnected(stopPolling);
    connection.onclose(startPolling);
    try {
      await connection.start();
      stopPolling();
    } catch (error) {
      console.error('[messaging] SignalR connection start failed.', error);
      startPolling();
    }
  }

  async function openCommandCenter(trigger) {
    if (state.isOpen || state.isOpening) return;
    state.isOpening = true;
    state.lastTrigger = trigger || document.activeElement;
    try {
      root.hidden = false;
      root.setAttribute('aria-hidden', 'false');
      root.classList.add('is-open');
      document.body.classList.add('messaging-command-center-open');
      unreadBadges.forEach(badge => badge.closest('[data-messaging-open]')?.setAttribute('aria-expanded', 'true'));
      state.isOpen = true;
      markCommandCenterOpen();
      showError('');
      elements.window.focus({ preventScroll: true });
      await Promise.all([refreshList(), loadRecipients()]);
      const lastConversationId = readSession('last-conversation', '');
      if (!state.active && lastConversationId && state.conversations.some(conversation => conversation.id === lastConversationId)) {
        await loadConversation(lastConversationId, false);
      }
    } catch (error) {
      showError(error.message);
    } finally {
      state.isOpening = false;
    }
  }

  function closeCommandCenter() {
    if (!state.isOpen) return;
    saveDraft();
    if (state.active?.id) {
      state.scrollPositions[state.active.id] = elements.messages.scrollTop;
      writeSession('scroll-positions', state.scrollPositions);
    }
    if (state.isJourneyOpen) {
      state.isJourneyOpen = false;
      elements.journeyPanel.hidden = true;
      elements.grid.hidden = false;
      elements.journeyOpen?.setAttribute('aria-expanded', 'false');
      clearJourneyCirclesOpenMark();
    }
    root.classList.remove('is-open');
    root.setAttribute('aria-hidden', 'true');
    root.hidden = true;
    document.body.classList.remove('messaging-command-center-open');
    unreadBadges.forEach(badge => badge.closest('[data-messaging-open]')?.setAttribute('aria-expanded', 'false'));
    state.isOpen = false;
    clearCommandCenterOpenMark();
    state.lastTrigger?.focus?.({ preventScroll: true });
  }

  document.querySelectorAll('[data-messaging-open]').forEach(trigger => {
    trigger.addEventListener('click', () => openCommandCenter(trigger));
  });
  window.addEventListener('messaging:open', () => openCommandCenter(null));
  window.addEventListener('messaging:journey-open', () => openJourneyCircles());
  elements.close.addEventListener('click', closeCommandCenter);
  elements.journeyOpen?.addEventListener('click', () => openJourneyCircles());
  elements.journeyBack?.addEventListener('click', closeJourneyCircles);
  elements.journeyProfileForm?.addEventListener('submit', saveJourneyProfile);
  elements.search.addEventListener('input', () => {
    window.clearTimeout(state.searchTimer);
    const query = elements.search.value.trim();
    const requestId = ++state.searchRequestId;
    state.searchAbortController?.abort();
    state.isSearchingContacts = false;
    renderSearchResults();
    if (!normalizeSearch(query)) {
      state.recipientMatches = state.recipients;
      state.recipientMatchesQuery = '';
      return;
    }
    state.searchTimer = window.setTimeout(() => searchRecipients(query, requestId), 260);
  });
  elements.messageBody.addEventListener('input', () => {
    saveDraft();
    state.pendingSubmission = null;
  });
  elements.messageBody.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      elements.sendForm.requestSubmit();
    }
  });
  elements.files.addEventListener('change', renderSelectedFiles);
  elements.sendForm.addEventListener('dragover', event => {
    event.preventDefault();
    elements.sendForm.classList.add('is-dragging');
  });
  elements.sendForm.addEventListener('dragleave', () => elements.sendForm.classList.remove('is-dragging'));
  elements.sendForm.addEventListener('drop', event => {
    event.preventDefault();
    elements.sendForm.classList.remove('is-dragging');
    if (!event.dataTransfer?.files?.length) return;
    try {
      const transfer = new DataTransfer();
      Array.from(event.dataTransfer.files).forEach(file => transfer.items.add(file));
      elements.files.files = transfer.files;
      renderSelectedFiles();
    } catch (_) {
      showError('Use Attach files to choose files in this browser.');
    }
  });
  elements.sendForm.addEventListener('submit', event => {
    event.preventDefault();
    sendMessage();
  });
  elements.messages.addEventListener('scroll', () => {
    if (!state.active?.id) return;
    state.scrollPositions[state.active.id] = elements.messages.scrollTop;
    if (isNearMessageBottom()) elements.newMessages.hidden = true;
  });
  elements.newMessages.addEventListener('click', () => {
    elements.messages.scrollTop = elements.messages.scrollHeight;
    elements.newMessages.hidden = true;
  });
  elements.mute.addEventListener('click', () => {
    if (state.active) notifyConversationAction('Muted', { isMuted: !state.active.isMuted }).catch(error => showError(error.message));
  });
  elements.closeConversation.addEventListener('click', () => {
    if (state.active) notifyConversationAction('Closed', { isClosed: !state.active.isClosed }).catch(error => showError(error.message));
  });
  document.addEventListener('keydown', event => {
    if (!state.isOpen) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }
    if (event.key === 'Tab') {
      const focusable = Array.from(root.querySelectorAll(
        'button:not([disabled]), input:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
        .filter(element => !element.hidden && element.getClientRects().length > 0);
      if (!focusable.length) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!root.contains(document.activeElement)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }
  }, true);

  const launchUrl = new URL(window.location.href);
  const shouldOpenFromRoute = launchUrl.searchParams.get('openMessages') === '1';
  const shouldOpenJourneyFromRoute = launchUrl.searchParams.get('journeyCircles') === 'open';
  if (shouldOpenFromRoute || shouldOpenJourneyFromRoute) {
    launchUrl.searchParams.delete('openMessages');
    launchUrl.searchParams.delete('journeyCircles');
    window.history.replaceState({}, '', `${launchUrl.pathname}${launchUrl.search}${launchUrl.hash}`);
  }

  if (isCommandCenterMarkedOpen() || shouldOpenFromRoute || shouldOpenJourneyFromRoute) {
    openCommandCenter(null)
      .then(() => (shouldOpenJourneyFromRoute || isJourneyCirclesMarkedOpen()) ? openJourneyCircles() : undefined)
      .catch(error => showError(error.message));
  } else {
    refreshList().catch(() => { });
  }
  startRealtime();
})();
