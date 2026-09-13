(() => {
  // Source marker; translation still comes from the shared application catalog.
  const applicationCopy = value => value;
  const root = document.querySelector('[data-messaging-command-center]');
  if (!root) return;

  const currentUserId = (root.dataset.currentUserId || '').trim().toLowerCase();
  const currentParticipantType = (root.dataset.currentParticipantType || '').trim();
  const composePrompt = root.dataset.messagingComposePrompt || 'Choose an authorized contact to begin.';
  const storagePrefix = `masterapp.messaging.${participantIdentityKey(currentUserId, currentParticipantType) || 'current'}.`;
  const token = root.querySelector('#messagingAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const elements = {
    window: root.querySelector('.messaging-command-center-window'),
    close: root.querySelector('#messagingCommandCenterClose'),
    error: root.querySelector('#messagingError'),
    recipientScopeButtons: Array.from(root.querySelectorAll('[data-messaging-recipient-scope]')),
    recipientScopeUnreadBadges: Array.from(root.querySelectorAll('[data-messaging-recipient-scope-unread]')),
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
    journeyRecommendationBadge: root.querySelector('#messagingJourneyRecommendationBadge'),
    journeyTitleBadge: root.querySelector('#messagingJourneyTitleBadge'),
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
    recipientScope: root.querySelector('[data-messaging-recipient-scope][aria-pressed="true"]')?.dataset.messagingRecipientScope || '',
    active: null,
    draftTarget: null,
    drafts: readSession('drafts', {}),
    scrollPositions: readSession('scroll-positions', {}),
    searchTimer: null,
    searchRequestId: 0,
    inboxRequestId: 0,
    inboxFlight: null,
    inboxController: null,
    inboxDirty: false,
    detailFlights: new Map(),
    detailRevisions: new Map(),
    readFlights: new Map(),
    reactionFlights: new Map(),
    readAcknowledged: new Map(),
    requestedConversationId: null,
    navigationVersion: 0,
    isSearchingContacts: false,
    searchResultNodes: new Map(),
    searchStatusNode: null,
    pollTimer: null,
    realtime: null,
    realtimeStarted: false,
    presenceTimer: null,
    presenceFlight: null,
    presenceDirty: false,
    presenceGeneration: 0,
    presence: null,
    callSelection: null,
    callSelectionFlight: null,
    callSelectionController: null,
    callSelectionVersion: 0,
    isOpen: false,
    isOpening: false,
    isJourneyOpen: false,
    journeyDashboard: null,
    lastTrigger: null,
    pendingSubmission: null,
    pendingSubmissions: new Map()
  };
  syncRecipientScopeControls();

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

  function participantIdentityKey(userId, participantType) {
    const normalizedUserId = normalize(userId);
    const normalizedParticipantType = normalize(participantType);
    return normalizedUserId && normalizedParticipantType
      ? `${normalizedParticipantType}:${normalizedUserId}`
      : '';
  }

  function isCurrentParticipant(userId, participantType) {
    return participantIdentityKey(userId, participantType) ===
      participantIdentityKey(currentUserId, currentParticipantType);
  }

  function normalizeSearch(value) {
    return normalize(value)
      .replace(/[^\p{L}\p{N}]+/gu, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  function recipientScopeParticipantType() {
    return recipientScopeParticipantTypeFor(state.recipientScope);
  }

  function recipientScopeParticipantTypeFor(scope) {
    if (scope === 'Agents') return 'Agent';
    if (scope === 'Clients') return 'Client';
    return '';
  }

  function recipientScopeDescription() {
    if (state.recipientScope === 'Clients') {
      return {
        searchPlaceholder: 'Search your active or business clients',
        emptyConversations: 'No client conversations yet.',
        archivedConversations: 'Archived client conversations'
      };
    }
    if (state.recipientScope === 'Agents') {
      return {
        searchPlaceholder: 'Search active company agents',
        emptyConversations: 'No agent conversations yet.',
        archivedConversations: 'Archived agent conversations'
      };
    }
    return {
      searchPlaceholder: 'Search your servicing agent or Journey Circles connections',
      emptyConversations: 'No conversations yet.',
      archivedConversations: 'Archived conversations'
    };
  }

  function isConversationInRecipientScope(conversation) {
    const participantType = recipientScopeParticipantType();
    return !participantType || (conversation?.counterparty || currentCounterparty(conversation))?.participantType === participantType;
  }

  function syncRecipientScopeControls() {
    const description = recipientScopeDescription();
    elements.search.placeholder = description.searchPlaceholder;
    elements.search.setAttribute('aria-label', description.searchPlaceholder);
    elements.recipientScopeButtons.forEach(button => {
      const active = button.dataset.messagingRecipientScope === state.recipientScope;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-pressed', String(active));
    });
  }

  function setRecipientScope(scope) {
    if (!scope || scope === state.recipientScope) return false;

    saveDraft();
    state.recipientScope = scope;
    state.recipients = [];
    state.recipientMatches = [];
    state.recipientMatchesQuery = '';
    state.recipientsLoaded = false;
    state.searchRequestId += 1;
    state.navigationVersion += 1;
    state.requestedConversationId = null;
    cancelDetailRequests();
    state.pendingSubmission = null;
    elements.search.value = '';
    elements.newMessages.hidden = true;

    if (state.active && !isConversationInRecipientScope(state.active)) state.active = null;
    if (state.draftTarget && state.draftTarget.participantType !== recipientScopeParticipantType()) state.draftTarget = null;

    syncRecipientScopeControls();
    return true;
  }

  function recipientRequestUrl(search = '') {
    const query = new URLSearchParams();
    if (search) query.set('search', search);
    if (state.recipientScope) query.set('recipientScope', state.recipientScope);
    const queryString = query.toString();
    return queryString ? `/Messaging/Recipients?${queryString}` : '/Messaging/Recipients';
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

  function participantAvatarUrl(person) {
    return person?.avatarUrl || (person?.userId && person?.participantType
      ? `/Messaging/Participants/${encodeURIComponent(person.userId)}/Avatar?participantType=${encodeURIComponent(person.participantType)}` : '');
  }

  function createAvatar(person, loading = 'lazy') {
    const displayName = person?.displayName || 'Participant';
    const avatar = document.createElement('span');
    avatar.className = 'messaging-avatar';
    avatar.dataset.userContent = '';
    avatar.setAttribute('role', 'img');
    avatar.setAttribute('aria-label', `${displayName} profile image`);

    const fallback = createTextElement('span', 'messaging-avatar-fallback', initials(displayName));
    fallback.setAttribute('aria-hidden', 'true');
    avatar.append(fallback);

    const avatarUrl = participantAvatarUrl(person);
    if (!avatarUrl) return avatar;

    const image = document.createElement('img');
    image.src = avatarUrl;
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
      const error = new Error(data?.errorMessage || 'The messaging request could not be completed.');
      error.status = response.status;
      throw error;
    }
    return data;
  }

  function supportsJourneyCircles() {
    return Boolean(elements.journeyOpen && elements.journeyPanel && elements.journeyProfileForm);
  }

  function journeyPrivacyChoices() {
    return Array.from(
      elements.journeyProfileForm?.querySelectorAll('[data-journey-privacy-choice]') || []
    );
  }

  function syncJourneyPrivacyTiles() {
    const privacy = journeyFormField('PrivacyChoices');
    if (!(privacy instanceof HTMLSelectElement)) return;

    const selected = new Set(selectedValues(privacy));

    journeyPrivacyChoices().forEach(choice => {
      const value = choice.dataset.journeyPrivacyChoice || '';
      choice.setAttribute('aria-pressed', selected.has(value) ? 'true' : 'false');
    });
  }

  function toggleJourneyPrivacyChoice(choice) {
    const privacy = journeyFormField('PrivacyChoices');
    if (!(privacy instanceof HTMLSelectElement)) return;

    const value = choice.dataset.journeyPrivacyChoice || '';
    const option = Array.from(privacy.options).find(item => item.value === value);
    if (!option) return;

    option.selected = !option.selected;
    syncJourneyPrivacyTiles();
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
    const identity = document.createElement('div');
    identity.className = 'messaging-journey-card-identity';
    identity.append(
      createAvatar(profile),
      createTextElement('h5', '', profile?.displayName || 'Journey member'));
    card.append(identity);
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

  function verifyJourneyPreferences(expected, dashboard) {
    if (!expected) return;

    const returned = dashboard?.preferences;
    if (!returned) {
      throw new Error(
        'Journey Circles save verification failed: the server reported success but returned no preference record.'
      );
    }

    const checks = [
      ['ConsentAffirmed', 'consentAffirmed'],
      ['IsOptedIn', 'isOptedIn'],
      ['IsDiscoverable', 'isDiscoverable'],
      ['AllowSuggestions', 'allowSuggestions'],
      ['AllowConnectionRequests', 'allowConnectionRequests']
    ];

    const failures = checks
      .filter(([submittedName, responseName]) =>
        Boolean(expected[submittedName]) !== Boolean(returned[responseName]))
      .map(([submittedName, responseName]) =>
        `${submittedName}: submitted=${Boolean(expected[submittedName])}, returned=${Boolean(returned[responseName])}`);

    if (failures.length) {
      throw new Error(
        `Journey Circles did not persist these settings: ${failures.join('; ')}. ` +
        'The server response did not match the values submitted.'
      );
    }
  }

  async function runJourneyAction(url, data, successMessage, expectedPreferences = null) {
    setJourneyStatus('Saving…');
    try {
      const dashboard = await request(url, {
        method: 'POST',
        body: data,
        headers: { 'X-Journey-Circles-Modal': '1' }
      });

      verifyJourneyPreferences(expectedPreferences, dashboard);

      state.journeyDashboard = dashboard;
      renderJourneyDashboard();
      setJourneyStatus(successMessage);
      state.recipientsLoaded = false;
      await loadRecipients();
    } catch (error) {
      const detail = error instanceof Error && error.message
        ? error.message
        : 'Journey Circles failed for an unknown reason.';

      setJourneyStatus(detail, true);
      console.error('Journey Circles action failed.', {
        url,
        expectedPreferences,
        error
      });
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
    const recommendationCount = Array.isArray(dashboard.recommendations)
      ? dashboard.recommendations.length
      : 0;

    [
      elements.journeyRecommendationBadge,
      elements.journeyTitleBadge
    ].forEach(badge => {
      if (!badge) return;

      badge.textContent = String(recommendationCount);
      badge.setAttribute(
        'aria-label',
        `${recommendationCount} Journey Circles recommendation${recommendationCount === 1 ? '' : 's'}`
      );
    });
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
      syncJourneyPrivacyTiles();
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

    runJourneyAction(
      '/JourneyCircles/Profile',
      data,
      'Journey Circles profile saved.',
      {
        ConsentAffirmed: privacy.has('consent'),
        IsOptedIn: privacy.has('opt-in'),
        IsDiscoverable: privacy.has('discoverable'),
        AllowSuggestions: privacy.has('suggestions'),
        AllowConnectionRequests: privacy.has('requests')
      }
    );
  }

  function activeDraftKey() {
    if (state.active?.id) return `conversation:${state.active.id}`;
    if (state.draftTarget?.userId) {
      return `recipient:${participantIdentityKey(state.draftTarget.userId, state.draftTarget.participantType)}`;
    }
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
    elements.recipientScopeButtons.forEach(button => {
      const scope = button.dataset.messagingRecipientScope || '';
      const scopeLabel = scope === 'Clients' ? 'client' : 'agent';
      const scopedCount = state.conversations
        .filter(conversation => conversation.counterparty?.participantType === recipientScopeParticipantTypeFor(scope))
        .reduce((total, conversation) => total + (conversation.unreadCount || 0), 0);
      const badge = elements.recipientScopeUnreadBadges.find(candidate =>
        candidate.dataset.messagingRecipientScopeUnread === scope);
      const countLabel = scopedCount > 99 ? '99+' : String(scopedCount);

      button.setAttribute(
        'aria-label',
        `${scope} conversations${scopedCount > 0 ? `, ${scopedCount} unread message${scopedCount === 1 ? '' : 's'}` : ''}`);
      if (!badge) return;
      badge.textContent = countLabel;
      badge.hidden = scopedCount === 0;
      badge.setAttribute(
        'aria-label',
        scopedCount > 0
          ? `${scopedCount} unread ${scopeLabel} message${scopedCount === 1 ? '' : 's'}`
          : `0 unread ${scopeLabel} messages`);
    });
  }

  function isDirectCallChoice(conversation) {
    return conversation.conversationType !== 'Group' && conversation.conversationType !== 'Assistant' &&
      conversation.purpose !== 'FounderAI' && conversation.isClosed !== true && conversation.isArchivedMembership !== true;
  }

  function cancelCallSelection() {
    state.callSelectionVersion += 1;
    state.callSelectionController?.abort();
    state.callSelectionController = null;
    state.callSelection = null;
    const prompt = document.getElementById('messagingCallSelection');
    if (prompt) prompt.hidden = true;
    renderConversations();
    renderSearchResults();
  }

  function selectConversationForCurrentIntent(conversation) {
    if (state.callSelectionFlight) return;
    if (state.callSelection) startSelectedCall(conversation.id, conversation.counterparty);
    else loadConversation(conversation.id, true).catch(error => showError(error.message));
  }

  async function startSelectedCall(conversationId, recipient) {
    if (!state.callSelection || state.callSelectionFlight) return;
    const intent = state.callSelection;
    const version = state.callSelectionVersion;
    let failureVersion = version;
    const client = state.callClient;
    if (!client || state.realtime?.state !== 'Connected') { showError(applicationCopy('Calling is unavailable.')); return; }
    const controller = new AbortController();
    state.callSelectionController = controller;
    const deadline = window.setTimeout(() => controller.abort(), 10000);
    const flight = (async () => {
      let id = conversationId;
      if (!id) {
        if (!recipient?.contactKey) throw new Error(applicationCopy('This participant is unavailable for calling.'));
        // Reuse the existing authorized, unique direct-conversation owner. No
        // placeholder message or second calling endpoint is created.
        const result = await request('/Messaging/Conversations', { method: 'POST', signal: controller.signal,
          body: JSON.stringify({ contactKey: recipient.contactKey, body: null, subject: null, includeMessages: false }) });
        id = result?.conversation?.id;
        if (!id) throw new Error(applicationCopy('The conversation could not be opened.'));
      }
      if (controller.signal.aborted || version !== state.callSelectionVersion) return;
      window.clearTimeout(deadline);
      // The existing call UI owns cancellation once media preparation starts.
      // Retire the recipient picker before that handoff, not after permission.
      cancelCallSelection();
      failureVersion = state.callSelectionVersion;
      await client.start(id, intent.video, recipient?.displayName);
    })();
    state.callSelectionFlight = flight;
    try { await flight; }
    catch (error) {
      if (failureVersion === state.callSelectionVersion && state.callClient === client && !client.retired) showError(error.name === 'AbortError'
        ? applicationCopy('The call could not be started in time. Please try again.') : error.message);
    } finally {
      window.clearTimeout(deadline);
      if (state.callSelectionFlight === flight) state.callSelectionFlight = null;
      if (state.callSelectionController === controller) state.callSelectionController = null;
    }
  }

  function beginCallSelection(video) {
    if (state.callSelectionFlight) return;
    cancelCallSelection();
    state.callSelection = { video };
    const prompt = document.getElementById('messagingCallSelection');
    const label = document.getElementById('messagingCallSelectionLabel');
    if (prompt) prompt.hidden = false;
    if (label) label.textContent = video ? applicationCopy('Choose a person for FaceTime') : applicationCopy('Choose a person to call');
    elements.search.value = '';
    renderConversations();
    renderSearchResults();
    const version = state.callSelectionVersion;
    loadRecipients().then(() => {
      if (version === state.callSelectionVersion && state.callSelection) renderSearchResults();
    }).catch(error => { if (version === state.callSelectionVersion && state.callSelection) showError(error.message); });
    elements.search.focus({ preventScroll: true });
  }

  function createPresencePill(conversationId, person) {
    const pill = createTextElement('span', 'messaging-presence', '');
    pill.hidden = true;
    if (conversationId) pill.dataset.presenceConversation = conversationId;
    else if (person?.userId && person?.participantType) {
      pill.dataset.presenceUser = person.userId;
      pill.dataset.presenceType = person.participantType;
    }
    return pill;
  }

  function renderPresence() {
    const result = state.isOpen && !document.hidden ? state.presence : null;
    root.querySelectorAll('.messaging-presence').forEach(pill => {
      const entry = pill.dataset.presenceConversation
        ? result?.conversations?.find(item => item.conversationId === pill.dataset.presenceConversation)
        : result?.participants?.find(item => participantIdentityKey(item.userId, item.participantType) === participantIdentityKey(pill.dataset.presenceUser, pill.dataset.presenceType));
      const known = typeof entry?.isOnline === 'boolean';
      pill.hidden = !known;
      pill.classList.toggle('is-online', entry?.isOnline === true);
      pill.classList.toggle('is-offline', entry?.isOnline === false);
      pill.textContent = known ? (entry.isOnline ? applicationCopy('Online') : applicationCopy('Offline')) : '';
    });
  }

  function clearPresence() {
    state.presenceGeneration += 1;
    state.presence = null;
    renderPresence();
  }

  async function refreshPresence() {
    if (state.realtime?.state !== 'Connected') return;
    if (state.presenceFlight) { state.presenceDirty = true; return; }
    state.presenceDirty = false;
    const generation = state.presenceGeneration;
    const participants = new Map(), conversations = new Set();
    if (state.isOpen && !document.hidden) {
      // Query only messaging presentation targets, never profile/CRM surfaces.
      if (state.active?.id) conversations.add(state.active.id);
      root.querySelectorAll('.messaging-presence').forEach(pill => {
        const bounds = pill.parentElement?.getBoundingClientRect();
        if (!bounds || bounds.width <= 0 || bounds.height <= 0 || bounds.bottom <= 0 || bounds.top >= window.innerHeight) return;
        if (pill.dataset.presenceConversation) conversations.add(pill.dataset.presenceConversation);
        else if (pill.dataset.presenceUser) participants.set(participantIdentityKey(pill.dataset.presenceUser, pill.dataset.presenceType),
          { userId: pill.dataset.presenceUser, participantType: pill.dataset.presenceType });
      });
    }
    const request = { participants: [...participants.values()].slice(0, 50), conversationIds: [...conversations].slice(0, 50) };
    // No overlapping invokes: a slow transport expires the display, not the call.
    const deadline = window.setTimeout(clearPresence, 10000);
    const flight = state.realtime.invoke('Presence', request);
    state.presenceFlight = flight;
    try {
      const result = await flight;
      if (generation !== state.presenceGeneration) return;
      state.presence = result;
      renderPresence();
    } catch (error) {
      clearPresence();
      console.warn('[messaging] Presence refresh failed; status cleared.');
    } finally {
      window.clearTimeout(deadline);
      if (state.presenceFlight === flight) state.presenceFlight = null;
      if (state.presenceDirty) refreshPresence();
    }
  }

  function startPresence() {
    window.clearInterval(state.presenceTimer);
    clearPresence();
    refreshPresence();
    state.presenceTimer = window.setInterval(refreshPresence, 30000);
  }

  function stopPresence() {
    window.clearInterval(state.presenceTimer);
    state.presenceTimer = null;
    clearPresence();
  }

  function renderConversations() {
    elements.list.replaceChildren();
    const scopedConversations = state.conversations.filter(isConversationInRecipientScope)
      .filter(conversation => !state.callSelection || isDirectCallChoice(conversation));
    const scope = recipientScopeDescription();
    if (scopedConversations.length === 0) {
      elements.list.append(createTextElement('p', 'messaging-list-empty', scope.emptyConversations));
      return;
    }

    const activeConversations = scopedConversations.filter(conversation => conversation.isArchivedMembership !== true);
    const archivedConversations = scopedConversations.filter(conversation => conversation.isArchivedMembership === true);

    function appendConversation(conversation) {
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
      const title = createTextElement('span', 'messaging-conversation-title', conversation.displayTitle || conversation.counterparty?.displayName || 'Member');
      title.dataset.userContent = '';
      copy.append(title);
      const preview = createTextElement('span', 'messaging-conversation-preview', conversation.lastMessagePreview || conversation.subject || 'No messages yet.');
      if (conversation.lastMessagePreview || conversation.subject) preview.dataset.userContent = '';
      copy.append(preview, createPresencePill(conversation.id));
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
      button.addEventListener('click', () => selectConversationForCurrentIntent(conversation));
      elements.list.append(button);
    }

    activeConversations.forEach(appendConversation);

    if (archivedConversations.length > 0) {
      elements.list.append(
        createTextElement('p', 'messaging-list-empty', scope.archivedConversations)
      );
      archivedConversations.forEach(appendConversation);
    }
    renderPresence();
    refreshPresence();
  }

  function participantName(conversation, userId, participantType) {
    return conversation.participants?.find(participant =>
      participantIdentityKey(participant.userId, participant.participantType) === participantIdentityKey(userId, participantType))?.displayName || 'Participant';
  }

  function currentCounterparty(conversation) {
    return conversation?.participants?.find(participant =>
      !isCurrentParticipant(participant.userId, participant.participantType)) || conversation?.counterparty || null;
  }

  function setComposerState(target, isClosed) {
    const isArchivedMembership = state.active?.isArchivedMembership === true;
    const isAvailable =
      Boolean(target?.contactKey || state.active?.id) &&
      !isClosed &&
      !isArchivedMembership &&
      state.active?.isDetailPending !== true;

    elements.messageBody.disabled = !isAvailable;
    elements.files.disabled = !isAvailable;
    elements.sendButton.disabled = !isAvailable;
    elements.fileLabel.classList.toggle('is-disabled', !isAvailable);

    if (!target) elements.composeTarget.textContent = composePrompt;
    else if (isArchivedMembership) {
      elements.composeTarget.textContent =
        'Archived client membership. Conversation history is preserved, but messaging is read-only until membership is restored.';
    } else if (isClosed) {
      elements.composeTarget.textContent = 'This conversation is closed.';
    } else {
      elements.composeTarget.textContent = `Secure message to ${target.displayName || 'recipient'}.`;
    }
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

  async function setMessageReaction(conversationId, message, emoji) {
    const key = `${conversationId}:${message.id}`;
    const version = state.navigationVersion;
    state.reactionConfirmed ||= new Map();
    if (!state.reactionFlights.has(key)) state.reactionConfirmed.set(key,
      (state.active?.messages || []).find(item => item.id === message.id)?.reactions || []);
    const previous = state.reactionFlights.get(key) || Promise.resolve();
    const flight = previous.catch(() => {}).then(() => request(`/Messaging/Conversations/${encodeURIComponent(conversationId)}/Messages/${encodeURIComponent(message.id)}/Reaction`, {
      method: emoji ? 'PUT' : 'DELETE',
      ...(emoji ? { body: JSON.stringify({ emoji }) } : {})
    }));
    state.reactionFlights.set(key, flight);
    const before = (state.active?.messages || []).find(item => item.id === message.id)?.reactions || [];
    const pending = before.map(item => ({ ...item, count: item.count - (item.reactedByCurrentActor ? 1 : 0), reactedByCurrentActor: false })).filter(item => item.count > 0);
    if (emoji) {
      const existing = pending.find(item => item.emoji === emoji);
      if (existing) { existing.count++; existing.reactedByCurrentActor = true; }
      else pending.push({ emoji, count: 1, reactedByCurrentActor: true });
    }
    if (state.active?.id === conversationId) {
      state.active = { ...state.active, messages: state.active.messages.map(item => item.id === message.id ? { ...item, reactions: pending } : item) };
      renderConversation();
    }
    try {
      const result = await flight;
      if (state.active?.id !== conversationId || version !== state.navigationVersion) return;
      state.reactionConfirmed.set(key, result.reactions || []);
      if (state.reactionFlights.get(key) !== flight) return;
      state.active = { ...state.active, messages: state.active.messages.map(item =>
        item.id === message.id ? { ...item, reactions: result.reactions || [] } : item) };
      renderConversation();
    } catch (error) {
      if (state.active?.id === conversationId && version === state.navigationVersion && state.reactionFlights.get(key) === flight) {
        state.active = { ...state.active, messages: state.active.messages.map(item =>
          item.id === message.id && item.reactions === pending ? { ...item, reactions: state.reactionConfirmed.get(key) || [] } : item) };
        renderConversation();
        showError(error.message);
      }
    } finally {
      if (state.reactionFlights.get(key) === flight) {
        state.reactionFlights.delete(key);
        state.reactionConfirmed.delete(key);
      }
    }
  }

  let reactionBubbleSettings = null;
  function reserveReactionOverlap(group) {
    if (!reactionBubbleSettings || !group.isConnected) return;
    const height = group.getBoundingClientRect().height;
    const count = group.children.length;
    group.closest('.messaging-message-row')?.style.setProperty('--messaging-reaction-width', `${count * reactionBubbleSettings.touchTarget + Math.max(0, count - 1) * reactionBubbleSettings.itemSpacing}px`);
    const gutter = (reactionBubbleSettings.touchTarget - reactionBubbleSettings.height) / 2;
    const outside = height > 0 ? gutter + reactionBubbleSettings.height * reactionBubbleSettings.outsideFraction : 0;
    group.parentElement.style.setProperty('--messaging-reaction-reserve', `${outside}px`);
    group.parentElement.style.setProperty('--messaging-reaction-offset', `${outside}px`);
    group.parentElement.style.setProperty('--messaging-reaction-inside', `${Math.max(0, height - outside - gutter)}px`);
  }
  const reactionBubbleObserver = typeof ResizeObserver === 'function' ? new ResizeObserver(entries => {
    entries.forEach(({ target }) => { if (target.isConnected) reserveReactionOverlap(target); else reactionBubbleObserver.unobserve(target); });
  }) : null;
  const reactionToneKeys = ['default', 'light', 'mediumLight', 'medium', 'mediumDark', 'dark'];
  function toneVariant(entry, tone) { return entry.skinToneVariants?.[reactionToneKeys[tone]] || entry.baseEmoji; }
  async function loadReactionTone() {
    const result = await request('/Messaging/ReactionPreferences');
    if (!Number.isInteger(result?.preferredReactionSkinTone) || result.preferredReactionSkinTone < 0 || result.preferredReactionSkinTone > 5) throw new Error('Reaction preference unavailable');
    return result.preferredReactionSkinTone;
  }
  let reactionEmojiCatalogFlight;
  function loadReactionEmojiCatalog() {
    if (!reactionEmojiCatalogFlight) reactionEmojiCatalogFlight = fetch('/design/legend-reaction-emoji.json')
      .then(response => { if (!response.ok) throw new Error('Emoji catalog unavailable'); return response.json(); })
      .then(catalog => {
        if (catalog.schemaVersion !== 1 || !Array.isArray(catalog.entries)) throw new Error('Emoji catalog unavailable');
        return catalog.entries.filter(entry => typeof entry.emoji === 'string' && typeof entry.baseEmoji === 'string' && typeof entry.name === 'string' && Array.isArray(entry.keywords) && entry.skinToneVariants);
      }).catch(error => { reactionEmojiCatalogFlight = null; throw error; });
    return reactionEmojiCatalogFlight;
  }

  function createReactionEmojiPicker(select, close) {
    const picker = document.createElement('div');
    picker.className = 'messaging-emoji-picker';
    const search = document.createElement('input');
    search.type = 'search';
    search.className = 'messaging-emoji-search';
    search.placeholder = 'Search emoji';
    search.setAttribute('aria-label', 'Search emoji');
    const grid = document.createElement('div');
    grid.className = 'messaging-emoji-grid';
    grid.setAttribute('role', 'group');
    grid.setAttribute('aria-label', 'Emoji');
    const status = createTextElement('div', 'messaging-emoji-status', 'Loading emoji…');
    status.setAttribute('role', 'status');
    let entries = [], results = [], shown = 0, preferredTone = null, savingTone = false;
    const tones = document.createElement('div');
    tones.className = 'messaging-emoji-tones';
    tones.setAttribute('role', 'group');
    tones.setAttribute('aria-label', 'Saved skin tone');
    const preferenceStatus = document.createElement('div');
    preferenceStatus.className = 'messaging-emoji-status';
    preferenceStatus.setAttribute('role', 'status');
    function renderTones() {
      tones.replaceChildren();
      const hand = entries.find(entry => entry.emoji === '👍');
      reactionToneKeys.forEach((key, tone) => {
        const button = createTextElement('button', 'messaging-emoji-option', hand ? toneVariant(hand, tone) : '');
        button.type = 'button';
        button.setAttribute('aria-label', `${key} skin tone`);
        button.setAttribute('aria-pressed', String(preferredTone === tone));
        button.disabled = preferredTone === null || savingTone || !hand;
        button.addEventListener('click', async () => {
          savingTone = true; preferenceStatus.textContent = ''; renderTones(); render();
          try {
            const result = await request('/Messaging/ReactionPreferences', { method: 'PUT', body: JSON.stringify({ preferredReactionSkinTone: tone }) });
            if (result?.preferredReactionSkinTone !== tone) throw new Error('Reaction preference was not saved');
            preferredTone = tone;
          } catch { preferenceStatus.textContent = 'Skin tone could not be saved. Try again.'; }
          finally { savingTone = false; if (picker.isConnected) { renderTones(); render(); } }
        });
        tones.append(button);
      });
    }
    async function refreshPreference() {
      preferenceStatus.textContent = 'Loading saved skin tone…';
      try { preferredTone = await loadReactionTone(); preferenceStatus.textContent = ''; }
      catch {
        preferenceStatus.replaceChildren();
        const retry = createTextElement('button', 'messaging-emoji-retry', 'Reaction preference unavailable. Retry');
        retry.type = 'button'; retry.addEventListener('click', refreshPreference); preferenceStatus.append(retry);
      }
      if (picker.isConnected) { renderTones(); render(); }
    }
    function appendBatch() {
      const fragment = document.createDocumentFragment();
      results.slice(shown, shown + 120).forEach(entry => {
        const button = createTextElement('button', 'messaging-emoji-option', toneVariant(entry, preferredTone ?? 0));
        button.type = 'button';
        button.setAttribute('aria-label', entry.name);
        button.title = entry.name;
        button.disabled = savingTone;
        button.addEventListener('click', () => select(toneVariant(entry, preferredTone ?? 0)));
        fragment.append(button);
      });
      shown = Math.min(shown + 120, results.length);
      grid.append(fragment);
    }
    function render() {
      const query = search.value.trim();
      const exactBase = entries.find(entry => entry.emoji === query)?.baseEmoji;
      const words = query.normalize('NFKD').replace(/\p{M}+/gu, '').toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean);
      results = entries.filter(entry => entry.emoji === entry.baseEmoji).filter(entry => !query || entry.baseEmoji === exactBase ||
        (words.length && words.every(word => entry.keywords.some(keyword => keyword.includes(word)))));
      grid.replaceChildren();
      grid.scrollTop = 0;
      shown = 0;
      status.textContent = results.length ? '' : 'No emoji found';
      appendBatch();
    }
    async function load() {
      status.textContent = 'Loading emoji…';
      try {
        entries = await loadReactionEmojiCatalog();
        if (picker.isConnected) { renderTones(); render(); }
      } catch {
        status.replaceChildren();
        const retry = createTextElement('button', 'messaging-emoji-retry', 'Retry loading emoji');
        retry.type = 'button';
        retry.addEventListener('click', load);
        status.append(retry);
      }
    }
    search.addEventListener('input', render);
    search.addEventListener('keydown', event => {
      if (event.key === 'Escape') { event.preventDefault(); close(); }
    });
    grid.addEventListener('scroll', () => {
      if (shown < results.length && grid.scrollTop + grid.clientHeight >= grid.scrollHeight - 100) appendBatch();
    });
    picker.append(search, tones, preferenceStatus, status, grid);
    queueMicrotask(() => { load(); refreshPreference(); });
    requestAnimationFrame(() => { if (picker.isConnected) search.focus({ preventScroll: true }); });
    return picker;
  }

  function appendMessageInteractions(card, conversation, message, actions = card) {
    const reactions = document.createElement('div');
    reactions.className = 'messaging-reactions';
    (message.reactions || []).forEach(reaction => {
      const button = createTextElement('button', 'messaging-reaction', reaction.emoji);
      button.type = 'button';
      button.setAttribute('aria-pressed', String(reaction.reactedByCurrentActor));
      button.setAttribute('aria-label', `${reaction.emoji}, ${reaction.count} reactions`);
      button.addEventListener('click', () => setMessageReaction(conversation.id, message,
        reaction.reactedByCurrentActor ? null : reaction.emoji));
      reactions.append(button);
    });
    const menu = document.createElement('details');
    menu.className = 'messaging-message-actions';
    const trigger = createTextElement('summary', '', '⋯');
    trigger.setAttribute('aria-label', applicationCopy('Message actions'));
    trigger.setAttribute('title', applicationCopy('Message actions'));
    menu.append(trigger);
    const palette = document.createElement('div');
    palette.className = 'messaging-reaction-palette';
    palette.setAttribute('aria-label', 'Choose a reaction');
    let quickTone = null, quickCatalog = [];
    const quickButtons = [];
    (conversation.reactionOptions || []).forEach(emoji => {
      const button = createTextElement('button', 'messaging-reaction', emoji);
      button.type = 'button';
      button.setAttribute('aria-label', `React ${emoji}`);
      button.disabled = false;
      quickButtons.push({ button, emoji });
      button.addEventListener('click', () => {
        menu.open = false;
        const entry = quickCatalog.find(item => item.emoji === emoji);
        setMessageReaction(conversation.id, message, entry ? toneVariant(entry, quickTone) : emoji);
      });
      palette.append(button);
    });
    const plus = createTextElement('button', 'messaging-reaction', '+');
    plus.type = 'button';
    plus.setAttribute('aria-label', 'Choose another emoji');
    plus.addEventListener('click', event => {
      event.preventDefault(); event.stopPropagation(); menu.open = true;
      const picker = createReactionEmojiPicker(emoji => {
        menu.open = false;
        setMessageReaction(conversation.id, message, emoji);
      }, () => { menu.open = false; trigger.focus(); });
      palette.replaceWith(picker);
    });
    palette.append(plus);
    menu.append(palette);
    menu.addEventListener('toggle', async () => {
      if (!menu.open) return;
      try {
        [quickTone, quickCatalog] = await Promise.all([loadReactionTone(), loadReactionEmojiCatalog()]);
        if (!menu.isConnected) return;
        quickButtons.forEach(({ button, emoji }) => {
          const entry = quickCatalog.find(item => item.emoji === emoji);
          button.textContent = entry ? toneVariant(entry, quickTone) : emoji;
          button.disabled = false;
        });
      } catch { if (menu.isConnected) showError('Reaction preference unavailable. Reopen to retry.'); }
    });
    const media = Array.from(card.querySelectorAll('.messaging-shared-media, .messaging-attachment')).at(-1);
    const content = media?.parentElement?.matches('.messaging-shared-original') ? media.parentElement : media;
    if (content) {
      const anchor = document.createElement('div');
      anchor.className = 'messaging-reacted-content';
      content.replaceWith(anchor);
      anchor.append(content, reactions);
      actions.append(menu);
    } else { actions.append(menu); card.append(reactions); }
    reactionBubbleObserver?.observe(reactions);
    card.addEventListener('dblclick', event => {
      if (event.target.closest('a, button, input, summary, video, audio')) return;
      setMessageReaction(conversation.id, message, '❤️');
    });
    card.addEventListener('contextmenu', event => {
      if (event.target.closest('a, button, input, video, audio') || window.getSelection()?.toString()) return;
      event.preventDefault();
      menu.open = true;
      trigger.focus();
    });
    let hold;
    card.addEventListener('pointerdown', event => {
      if (event.pointerType !== 'touch' || event.target.closest('a, button, input, summary')) return;
      hold = window.setTimeout(() => { menu.open = true; }, 500);
    });
    ['pointerup', 'pointercancel', 'pointermove'].forEach(name => card.addEventListener(name, () => window.clearTimeout(hold)));
  }

  function appendLinkedText(container, text) {
    container.dataset.userContent = '';
    container.setAttribute('translate', 'no');
    const value = String(text || '');
    const pattern = /https?:\/\/[^\s<>]+/gi;
    let offset = 0;
    for (const match of value.matchAll(pattern)) {
      const urlText = match[0].replace(/[.,!?;:)]+$/, '');
      container.append(document.createTextNode(value.slice(offset, match.index)));
      const link = createTextElement('a', '', urlText);
      try {
        const url = new URL(urlText, window.location.href);
        if (!['http:', 'https:'].includes(url.protocol)) throw new Error('Unsupported link');
        link.href = url.href;
        if (url.origin !== window.location.origin) { link.target = '_blank'; link.rel = 'noopener noreferrer'; }
        container.append(link);
      } catch (_) { container.append(document.createTextNode(urlText)); }
      offset = match.index + urlText.length;
    }
    container.append(document.createTextNode(value.slice(offset)));
  }

  function appendSharedContent(card, content) {
    if (!content) return;
    const shared = document.createElement('div');
    shared.className = 'messaging-shared-content';
    if (content.status !== 'available') {
      shared.append(createTextElement('p', '', applicationCopy("This shared content is unavailable.")));
    } else {
      const originalUrl = `/Social/Posts/${encodeURIComponent(content.sourcePostId)}`;
      const contentName = content.contentType === 'Reel' ? 'Hac' : content.contentType || 'post';
      const author = createTextElement('a', 'messaging-shared-author', content.authorDisplayName || `Shared ${contentName}`);
      author.href = originalUrl;
      author.dataset.userContent = '';
      shared.append(author);
      if (content.body) {
        const body = createTextElement('p', 'messaging-message-body', '');
        appendLinkedText(body, content.body);
        shared.append(body);
      }
      (content.media || []).slice().sort((a, b) => a.displayOrder - b.displayOrder).forEach(asset => {
        if (!['Image', 'Video'].includes(asset.mediaKind)) return;
        const media = document.createElement(asset.mediaKind === 'Image' ? 'img' : 'video');
        media.src = `/Social/Media/${encodeURIComponent(asset.id)}`;
        media.className = 'messaging-shared-media';
        if (asset.mediaKind === 'Image') {
          media.alt = asset.accessibilityText || `Shared ${contentName} image`;
          media.loading = 'lazy';
          const open = createTextElement('a', 'messaging-shared-original', '');
          open.href = originalUrl;
          open.setAttribute('aria-label', `Open original ${contentName}`);
          open.append(media);
          shared.append(open);
        } else {
          media.controls = true;
          media.playsInline = true;
          media.preload = 'metadata';
          shared.append(media);
        }
        media.addEventListener('error', () => {
          media.replaceWith(createTextElement('p', '', applicationCopy("This media is unavailable. Open the original to check access.")));
        });
      });
      const openLabel = { Post: applicationCopy("Open original post"), Story: applicationCopy("Open original story"), Reel: applicationCopy("Open original Hac") }[content.contentType] || applicationCopy("Open original post");
      const link = createTextElement('a', 'messaging-shared-open', openLabel);
      link.href = originalUrl;
      shared.append(link);
    }
    card.append(shared);
  }

  function latestReadMessageIndex(conversation, messages) {
    const readers = (conversation.readReceipts?.readers || []).filter(reader =>
      !isCurrentParticipant(reader.userId, reader.participantType));
    let latest = -1;
    messages.forEach((message, index) => {
      if (!isCurrentParticipant(message.senderUserId, message.senderType)) return;
      const sent = parseUtcTimestamp(message.sentUtc)?.getTime();
      if (sent != null && readers.some(reader =>
        (parseUtcTimestamp(reader.readThroughUtc)?.getTime() || 0) >= sent)) latest = index;
    });
    return latest;
  }

  function renderConversation(shouldScrollToBottom = false) {
    const conversation = state.active;
    const target = conversation ? currentCounterparty(conversation) : state.draftTarget;
    const isClosed = conversation?.isClosed === true;
    const isDraft = !conversation && Boolean(target);
    state.originalMessageViews ??= new Set();
    if (state.originalViewConversationId !== conversation?.id) {
      state.originalMessageViews.clear();
      state.originalViewConversationId = conversation?.id;
    }

    elements.threadEmpty.hidden = Boolean(conversation || isDraft);
    elements.threadContent.hidden = !(conversation || isDraft);
    reactionBubbleObserver?.disconnect();
    elements.messages.replaceChildren();
    elements.mute.hidden = !conversation || conversation.isDetailPending === true;
    elements.closeConversation.hidden = !conversation || conversation.isDetailPending === true;

    if (!conversation && !isDraft) {
      setComposerState(null, false);
      restoreDraft();
      return;
    }

    elements.threadAvatar.replaceChildren(createAvatar(target, 'eager'));
    elements.threadTitle.textContent = conversation?.displayTitle || target?.displayName || 'Member';
    elements.threadTitle.dataset.userContent = '';
    elements.threadSubject.replaceChildren(createPresencePill(conversation?.id, target));
    if (conversation?.subject) {
      const subject = createTextElement('span', '', conversation.subject);
      subject.dataset.userContent = '';
      elements.threadSubject.append(subject);
    }
    renderPresence();
    refreshPresence();
    if (conversation) {
      elements.mute.textContent = conversation.isMuted ? 'Unmute' : 'Mute';
      elements.closeConversation.textContent = isClosed ? 'Reopen' : 'Close';

      if (conversation.hasOlderMessages) {
        const older = createTextElement('button', 'messaging-history-button', 'Load earlier messages');
        older.type = 'button';
        older.addEventListener('click', () => loadOlderMessages(older));
        elements.messages.append(older);
      }
      if (conversation.isDetailPending) {
        elements.messages.append(createTextElement('p', 'messaging-draft-intro', conversation.detailLoadFailed ? applicationCopy("Recent messages could not be loaded.") : applicationCopy("Loading recent messages…")));
        if (conversation.detailLoadFailed) {
          const retry = createTextElement('button', 'messaging-history-button', applicationCopy("Retry recent messages"));
          retry.type = 'button';
          retry.addEventListener('click', () => loadConversation(conversation.id, false).catch(error => showError(error.message)));
          elements.messages.append(retry);
        }
      }
      const visibleMessages = (conversation.messages || []).filter(message => !message.isDeleted);
      const latestReadIndex = latestReadMessageIndex(conversation, visibleMessages);
      let previousDay = '';
      visibleMessages.forEach((message, messageIndex) => {
        const label = dayLabel(message.sentUtc);
        if (label && label !== previousDay) {
          elements.messages.append(createTextElement('p', 'messaging-day-divider', label));
          previousDay = label;
        }

        const card = document.createElement('article');
        card.className = message.sharedContent ? 'messaging-message messaging-message-share' : 'messaging-message';
        const isOwn = isCurrentParticipant(message.senderUserId, message.senderType);
        if (isOwn) card.classList.add('is-own');
        const meta = document.createElement('div');
        meta.className = 'messaging-message-meta';
        if (!isOwn && conversation.conversationType === 'Group') {
          const sender = createTextElement('span', 'messaging-message-sender', participantName(conversation, message.senderUserId, message.senderType));
          sender.dataset.userContent = '';
          card.append(sender);
        }
        meta.append(createTextElement('time', '', formatMessageTime(message.sentUtc)));
        if (isOwn && messageIndex >= latestReadIndex) {
          const read = messageIndex === latestReadIndex;
          meta.append(createTextElement('span', `messaging-receipt is-${read ? 'read' : 'sent'}`, read ? 'Read' : 'Sent'));
        }
        if (message.editedUtc) meta.append(createTextElement('span', 'messaging-message-edited', 'Edited'));
        if (message.reply) {
          const reply = document.createElement('blockquote');
          reply.className = 'messaging-message-reply';
          if (message.reply.isDeleted) reply.textContent = applicationCopy('Message deleted');
          else {
            const author = createTextElement('strong', '', participantName(conversation, message.reply.senderUserId, message.reply.senderType));
            author.dataset.userContent = '';
            const excerpt = createTextElement('span', '', message.reply.body);
            excerpt.dataset.userContent = '';
            reply.append(author, excerpt);
          }
          card.append(reply);
        }
        if (message.body) {
          const body = createTextElement('p', 'messaging-message-body', '');
          const retainedOriginal = state.originalMessageViews.has(message.id) && message.translation && message.originalBody;
          appendLinkedText(body, retainedOriginal ? message.originalBody : message.body);
          card.append(body);
          if (message.translation && message.originalBody && message.originalBody !== message.body) {
            const toggle = createTextElement('button', 'messaging-translation-toggle', retainedOriginal ? applicationCopy('View translation') : applicationCopy('View original'));
            toggle.type = 'button';
            toggle.setAttribute('aria-pressed', String(Boolean(retainedOriginal)));
            toggle.addEventListener('click', () => {
              const showOriginal = toggle.getAttribute('aria-pressed') !== 'true';
              if (showOriginal) state.originalMessageViews.add(message.id);
              else state.originalMessageViews.delete(message.id);
              body.replaceChildren();
              appendLinkedText(body, showOriginal ? message.originalBody : message.body);
              toggle.setAttribute('aria-pressed', String(showOriginal));
              toggle.textContent = showOriginal ? applicationCopy('View translation') : applicationCopy('View original');
            });
            card.append(toggle);
          }
        }
        if (message.translationNotice) {
          card.append(createTextElement('p', 'messaging-message-body', message.translationNotice));
        }
        appendSharedContent(card, message.sharedContent);

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
              link.dataset.userContent = '';
              link.setAttribute('download', attachment.originalFileName || 'attachment');
              attachments.append(link);
            } else {
              const item = createTextElement('span', `messaging-attachment is-${normalize(status) || 'pending'}`, '');
              const fileName = createTextElement('span', '', attachment.originalFileName);
              fileName.dataset.userContent = '';
              item.append(fileName, document.createTextNode(' — '), createTextElement('span', '', status));
              attachments.append(item);
            }
          });
          card.append(attachments);
        }
        appendMessageInteractions(card, conversation, message, meta);
        const row = document.createElement('div');
        row.className = `messaging-message-row${isOwn ? ' is-own' : ''}${message.reactions?.length && !message.sharedContent ? ' has-reactions' : ''}`;
        row.append(card, meta);
        elements.messages.append(row);
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
    item._messagingTitle.dataset.userContent = '';
    item._messagingSubtitle.replaceChildren(createPresencePill(result.conversationId, result.person));
    if (result.person?.email) {
      const identity = createTextElement('span', '', `${roleLabel(result.person.participantType)} · ${result.person.email}`);
      identity.dataset.userContent = '';
      item._messagingSubtitle.append(identity);
    }
    if (result.unreadCount > 0) item._messagingSubtitle.append(createTextElement('span', 'messaging-unread-count', String(result.unreadCount)));
    item._messagingSubtitle.hidden = false;
    renderPresence();
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
    if (!query && !state.callSelection) {
      state.searchResultNodes.forEach(item => item.remove());
      state.searchResultNodes.clear();
      setSearchStatus('');
      elements.searchResults.hidden = true;
      return;
    }

    const matchingConversations = state.conversations
      .filter(conversation => conversation.isArchivedMembership !== true)
      .filter(conversation => !state.callSelection || isDirectCallChoice(conversation))
      .filter(isConversationInRecipientScope)
      .filter(conversation => matchesSearch(searchText(conversation), query))
      .sort((left, right) =>
        searchRank(left.counterparty, query, left) - searchRank(right.counterparty, query, right) ||
        (parseUtcTimestamp(right.lastMessageUtc)?.getTime() || 0) -
        (parseUtcTimestamp(left.lastMessageUtc)?.getTime() || 0));
    const existingCounterparties = new Set(matchingConversations.map(conversation =>
      participantIdentityKey(conversation.counterparty?.userId, conversation.counterparty?.participantType)));
    const existingConversationIds = new Set(matchingConversations.map(conversation => conversation.id));
    const recipientSource = query && state.recipientMatchesQuery === query
      ? state.recipientMatches
      : state.recipients;
    const matchingRecipients = recipientSource.filter(recipient =>
      !existingCounterparties.has(participantIdentityKey(recipient.userId, recipient.participantType)) &&
      !existingConversationIds.has(recipient.existingConversationId) &&
      matchesSearch([recipient.displayName, recipient.email].filter(Boolean).join(' '), query))
      .sort((left, right) =>
        searchRank(left, query) - searchRank(right, query) ||
        left.displayName.localeCompare(right.displayName));

    const results = [
      ...matchingConversations.map(conversation => ({
        key: searchResultKey('conversation', conversation.id, 'conversation'),
        person: conversation.counterparty,
        conversationId: conversation.id,
        unreadCount: conversation.unreadCount,
        title: conversation.displayTitle || conversation.counterparty?.displayName || 'Member',
        subtitle: `Existing conversation${conversation.unreadCount > 0 ? ` · ${conversation.unreadCount} unread` : ''}`,
        select: () => {
          elements.search.value = '';
          renderSearchResults();
          selectConversationForCurrentIntent(conversation);
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
          if (state.callSelection) { startSelectedCall(recipient.existingConversationId, recipient); return; }
          if (recipient.existingConversationId) {
            elements.search.value = '';
            renderSearchResults();
            loadConversation(recipient.existingConversationId, true).catch(error => showError(error.message));
            return;
          }
          selectDraftRecipient(recipient);
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

  function cancelDetailRequests(exceptId = null) {
    state.detailFlights.forEach((flight, id) => {
      if (id === exceptId) return;
      flight.controller?.abort();
      state.detailFlights.delete(id);
    });
  }

  function selectDraftRecipient(recipient) {
    cancelDetailRequests();
    state.navigationVersion += 1;
    state.requestedConversationId = null;
    state.active = null;
    state.draftTarget = recipient;
    state.pendingSubmission = null;
  }

  async function waitForSelectedDetail() {
    while (state.isOpen && state.requestedConversationId) {
      const selected = state.detailFlights.get(state.requestedConversationId);
      if (!selected) return;
      try { await selected; } catch (_) { }
      if (state.detailFlights.get(state.requestedConversationId) === selected) return;
    }
  }

  async function refreshList() {
    state.inboxDirty = true;
    if (state.inboxFlight) return state.inboxFlight;
    state.inboxFlight = (async () => {
      do {
        state.inboxDirty = false;
        await waitForSelectedDetail();
        const controller = new AbortController();
        state.inboxController = controller;
        let result;
        try { result = await request('/Messaging/Conversations', { signal: controller.signal, priority: 'low' }); }
        catch (error) {
          if (!controller.signal.aborted) throw error;
          state.inboxDirty = true;
          continue;
        } finally {
          if (state.inboxController === controller) state.inboxController = null;
        }
        if (controller.signal.aborted) { state.inboxDirty = true; continue; }
        state.conversations = result.conversations || [];
        setUnreadCount();
        renderConversations();
        renderSearchResults();
      } while (state.inboxDirty);
    })();
    try { await state.inboxFlight; }
    finally { state.inboxFlight = null; }
  }

  async function acknowledgeVisibleConversation(conversation) {
    const id = conversation.id;
    const latest = conversation.messages?.at(-1)?.id;
    if (!latest || state.readAcknowledged.get(id) === latest) return;
    if (state.readFlights.has(id)) {
      await state.readFlights.get(id);
      if (state.active?.id === id) return acknowledgeVisibleConversation(state.active);
      return;
    }
    const flight = request(`/Messaging/Conversations/${encodeURIComponent(id)}/Read?readThroughMessageId=${encodeURIComponent(latest)}`, { method: 'POST' });
    state.readFlights.set(id, flight);
    try {
      await flight;
      state.readAcknowledged.set(id, latest);
    } finally { state.readFlights.delete(id); }
  }

  function clearUnavailableConversation(conversationId, error) {
    if (![401, 403, 404, 410].includes(error?.status) || state.active?.id !== conversationId) return false;
    state.active = null;
    state.draftTarget = null;
    state.requestedConversationId = null;
    state.navigationVersion += 1;
    cancelDetailRequests();
    state.conversations = state.conversations.filter(conversation => conversation.id !== conversationId);
    state.readAcknowledged.delete(conversationId);
    removeSession('last-conversation');
    renderConversation();
    renderConversations();
    renderSearchResults();
    setUnreadCount();
    return true;
  }

  async function loadConversation(conversationId, markRead, shouldScrollToBottom = false, invalidate = false) {
    if (state.requestedConversationId !== conversationId) {
      state.requestedConversationId = conversationId;
      state.navigationVersion += 1;
      cancelDetailRequests(conversationId);
    }
    const version = state.navigationVersion;
    if (state.active?.id !== conversationId) elements.newMessages.hidden = true;
    if (state.active?.id) {
      state.scrollPositions[state.active.id] = elements.messages.scrollTop;
      writeSession('scroll-positions', state.scrollPositions);
    }
    if (state.active?.id !== conversationId) {
      saveDraft();
      const summary = state.conversations.find(conversation => conversation.id === conversationId);
      state.active = { ...summary, id: conversationId, messages: [], isDetailPending: true };
      state.draftTarget = null;
      renderConversation(shouldScrollToBottom);
      renderConversations();
    } else if (state.active.detailLoadFailed) {
      state.active.detailLoadFailed = false;
      renderConversation();
    }
    let flight = state.detailFlights.get(conversationId);
    if (invalidate) state.detailRevisions.set(conversationId, (state.detailRevisions.get(conversationId) || 0) + 1);
    const detailRevision = state.detailRevisions.get(conversationId) || 0;
    if (invalidate && flight) {
      // An event can arrive after the server captured the in-flight snapshot.
      // Coalesce the burst into a fresh read after that snapshot completes.
      try { await flight; } catch (_) { }
      if (version !== state.navigationVersion || state.requestedConversationId !== conversationId) return;
      return loadConversation(conversationId, markRead, shouldScrollToBottom);
    }
    if (!flight) {
      if (state.isOpen) state.inboxController?.abort();
      const controller = new AbortController();
      flight = request(`/Messaging/Conversations/${encodeURIComponent(conversationId)}?take=60`, { signal: controller.signal, priority: 'high' });
      flight.controller = controller;
      state.detailFlights.set(conversationId, flight);
      flight.finally(() => {
        if (state.detailFlights.get(conversationId) === flight) state.detailFlights.delete(conversationId);
      }).catch(() => {});
    }
    let result;
    try { result = await flight; }
    catch (error) {
      if (flight.controller?.signal.aborted || version !== state.navigationVersion ||
          state.requestedConversationId !== conversationId || detailRevision !== (state.detailRevisions.get(conversationId) || 0)) return;
      if (clearUnavailableConversation(conversationId, error)) throw error;
      if (state.active?.id === conversationId && state.active.isDetailPending) {
        state.active.detailLoadFailed = true;
        renderConversation();
      }
      throw error;
    }
    if (version !== state.navigationVersion || state.requestedConversationId !== conversationId ||
        detailRevision !== (state.detailRevisions.get(conversationId) || 0)) return;
    if (!isConversationInRecipientScope(result.conversation)) {
      state.active = null;
      state.requestedConversationId = null;
      renderConversation();
      return;
    }
    state.active = result.conversation;
    state.draftTarget = null;
    // A refresh cannot acknowledge or discard an uncertain send transaction.
    writeSession('last-conversation', conversationId);
    renderConversation(shouldScrollToBottom);
    renderConversations();
    if (markRead && state.isOpen && !document.hidden) {
      try { await acknowledgeVisibleConversation(state.active); }
      catch (error) { if (version === state.navigationVersion) showError(error.message); }
    }
  }

  async function loadOlderMessages(button) {
    const conversation = state.active;
    const oldestMessage = conversation?.messages?.[0];
    const oldest = oldestMessage?.sentUtc;
    if (!oldest || !conversation.hasOlderMessages) return;
    const version = state.navigationVersion;
    button.disabled = true;
    try {
      const result = await request(`/Messaging/Conversations/${encodeURIComponent(conversation.id)}?take=60&beforeUtc=${encodeURIComponent(oldest)}&beforeMessageId=${encodeURIComponent(oldestMessage.id)}`);
      if (version !== state.navigationVersion || state.active?.id !== conversation.id) return;
      const existing = state.active.messages || [];
      const ids = new Set(existing.map(message => message.id));
      const older = (result.conversation.messages || []).filter(message => !ids.has(message.id));
      const height = elements.messages.scrollHeight;
      const top = elements.messages.scrollTop;
      state.active = { ...state.active, messages: [...older, ...existing], hasOlderMessages: result.conversation.hasOlderMessages };
      renderConversation();
      elements.messages.scrollTop = top + elements.messages.scrollHeight - height;
    } catch (error) {
      if (version !== state.navigationVersion || state.active?.id !== conversation.id) return;
      clearUnavailableConversation(conversation.id, error);
      showError(error.message);
    }
    finally { button.disabled = false; }
  }

  async function loadRecipients() {
    if (state.recipientsLoaded) return;
    const scope = state.recipientScope;
    await waitForSelectedDetail();
    if (scope !== state.recipientScope) return;
    const result = await request(recipientRequestUrl(), { priority: 'low' });
    if (scope !== state.recipientScope) return;
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

    state.isSearchingContacts = true;
    renderSearchResults();
    try {
      const result = await request(recipientRequestUrl(query));
      if (requestId !== state.searchRequestId) return;
      state.recipientMatches = result.recipients || [];
      state.recipientMatchesQuery = normalizedQuery;
    } catch (error) {
      if (requestId === state.searchRequestId) {
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
    const files = submission.files;
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
    const retained = state.pendingSubmissions.get(key);
    const files = Array.from(elements.files.files || []);
    const sameFiles = retained && files.length === retained.files.length && files.every((file, index) => file === retained.files[index]);
    if (retained && retained.body === body && sameFiles) return retained;
    if (retained?.sending) throw new Error('The current message is still sending.');
    if (retained?.messageId && retained.uploadedFileIndexes.length < retained.files.length)
      throw new Error('Retry the pending attachment delivery before sending another message.');
    state.pendingSubmission = {
      key,
      body,
      conversationId: state.active?.id || null,
      target: state.draftTarget,
      files: Array.from(elements.files.files || []),
      clientMessageId: clientMessageId(),
      messageId: null,
      uploadedFileIndexes: [],
      draftKeys: [key]
    };
    state.pendingSubmissions.set(key, state.pendingSubmission);
    return state.pendingSubmission;
  }

  function offerPendingRetry(submission) {
    if (!submission || submission.sending) return;
    const retry = createTextElement('button', 'messaging-retry',
      submission.messageId ? 'Retry pending attachments' : 'Retry previous message');
    retry.type = 'button';
    retry.addEventListener('click', () => sendMessage(submission));
    elements.error.append(retry);
  }

  async function sendMessage(ownedSubmission = null) {
    const body = ownedSubmission?.body ?? elements.messageBody.value.trim();
    if (!body || (!state.active && !state.draftTarget)) return;

    let submission;
    try { submission = ownedSubmission || createSubmission(body); }
    catch (error) {
      showError(error.message);
      offerPendingRetry(state.pendingSubmissions.get(activeDraftKey()));
      return;
    }
    if (submission.sending) return;
    submission.sending = true;
    const navigationVersion = state.navigationVersion;
    elements.sendButton.disabled = true;
    showError('');
    try {
      if (!submission.messageId) {
        if (submission.conversationId) {
          const result = await request(`/Messaging/Conversations/${encodeURIComponent(submission.conversationId)}/Messages`, {
            method: 'POST',
            body: JSON.stringify({ body, clientMessageId: submission.clientMessageId })
          });
          submission.messageId = result.message?.id;
          if (state.active?.id === submission.conversationId && result.message) {
            const messages = state.active.messages || [];
            if (!messages.some(message => message.id === result.message.id)) messages.push(result.message);
            state.active = { ...state.active, messages };
            renderConversation(true);
          }
        } else {
          const target = submission.target;
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
            .find(message => isCurrentParticipant(message.senderUserId, message.senderType) && message.body === body)?.id || null;
          submission.conversationId = created?.id;
          if (created?.id) {
            submission.key = `conversation:${created.id}`;
            state.pendingSubmissions.set(submission.key, submission);
            if (!submission.draftKeys.includes(submission.key)) submission.draftKeys.push(submission.key);
          }
          if (navigationVersion === state.navigationVersion && state.draftTarget === target) {
            state.active = created;
            state.draftTarget = null;
            state.requestedConversationId = created?.id;
          }
        }
      }

      if (!submission.messageId) throw new Error('The message was created, but its attachment target could not be determined.');
      await uploadAttachments(submission.messageId, submission);
      const stillSelected = navigationVersion === state.navigationVersion && state.active?.id === submission.conversationId;
      const unchangedBody = elements.messageBody.value.trim() === submission.body;
      submission.draftKeys.forEach(key => {
        if (state.drafts[key] === submission.body) delete state.drafts[key];
        if (state.pendingSubmissions.get(key) === submission) state.pendingSubmissions.delete(key);
      });
      writeSession('drafts', state.drafts);
      if (stillSelected && unchangedBody) elements.messageBody.value = '';
      const selectedFiles = Array.from(elements.files.files || []);
      if (stillSelected && selectedFiles.length === submission.files.length && selectedFiles.every((file, index) => file === submission.files[index]))
        elements.files.value = '';
      renderSelectedFiles();
      if (state.pendingSubmission === submission) state.pendingSubmission = null;
      if (stillSelected) {
        saveDraft();
        renderConversation(true);
        loadConversation(submission.conversationId, false, false, true).catch(error => showError(error.message));
      }
      refreshList().catch(() => {});
    } catch (error) {
      submission.sending = false;
      showError(navigationVersion === state.navigationVersion ? error.message : `Previous message delivery: ${error.message}`);
      offerPendingRetry(submission);
    } finally {
      submission.sending = false;
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
      const refresh = state.isOpen && !document.hidden && state.active?.id
        ? loadConversation(state.active.id, false)
        : Promise.resolve();
      refresh.catch(() => {}).then(() => refreshList()).catch(() => {});
    }, 45000);
  }

  function stopPolling() {
    if (!state.pollTimer) return;
    window.clearInterval(state.pollTimer);
    state.pollTimer = null;
  }

  function attachCalling(connection) {
    const dialog = document.getElementById('legendBrowserCall');
    if (!dialog || !window.LegendBrowserCalling) return;
    const remote = document.getElementById('legendBrowserCallRemote');
    const local = document.getElementById('legendBrowserCallLocal');
    const name = document.getElementById('legendBrowserCallName');
    const status = document.getElementById('legendBrowserCallStatus');
    // A copied/duplicated tab must never inherit another media owner's device ID.
    const deviceId = crypto.randomUUID();
    let tone = null;
    let sound = '';
    let retired = false;
    const portrait = document.getElementById('legendBrowserCallPortrait');
    portrait.addEventListener('error', () => { portrait.hidden = true; });
    const stopTone = () => { tone?.pause(); tone = null; sound = ''; };
    const client = new window.LegendBrowserCalling({ connection, deviceId,
      isActor: (id, type, aliases) => isCurrentParticipant(id, type) || (aliases || []).some(alias => isCurrentParticipant(alias, type)),
      present: (call, caller) => {
        if (retired) call = null;
        if (!call) {
          stopTone(); portrait.removeAttribute('src'); portrait.hidden = true;
          dialog.querySelector('[data-legend-call-action="audio"]').hidden = true;
          for (const action of ['mute', 'camera', 'share']) dialog.querySelector(`[data-legend-call-action="${action}"]`).setAttribute('aria-pressed', 'false');
          if (dialog.open) dialog.close(); return;
        }
        const presentingIdentity = ['preparing', 'ringing'].includes(call.status);
        name.hidden = !presentingIdentity;
        name.textContent = caller ? call.calleeName : call.callerName;
        document.getElementById('legendBrowserCallInitials').hidden = !presentingIdentity;
        document.getElementById('legendBrowserCallInitials').textContent = initials(name.textContent || 'LEGEND');
        const peer = caller ? { userId: call.calleeUserId, participantType: call.calleeType } : { userId: call.callerUserId, participantType: call.callerType };
        const photo = participantAvatarUrl(peer);
        const wallpaper = (caller ? call.calleeWallpaperMode : call.callerWallpaperMode) === 'profile';
        portrait.classList.toggle('is-wallpaper', wallpaper);
        if (photo && portrait.getAttribute('src') !== photo) { portrait.hidden = false; portrait.src = photo; }
        if (!photo) portrait.removeAttribute('src');
        if (!photo || !presentingIdentity) portrait.hidden = true;
        status.textContent = ['preparing', 'ringing'].includes(call.status)
          ? (caller ? (call.receivedUtc ? applicationCopy('Ringing') : applicationCopy('Calling')) : applicationCopy('Incoming call'))
          : (call.status === 'active' ? applicationCopy('Connected') : applicationCopy('Connecting'));
        dialog.querySelector('[data-legend-call-action="accept"]').hidden = caller || call.status !== 'ringing';
        for (const action of ['mute', 'camera', 'share']) dialog.querySelector(`[data-legend-call-action="${action}"]`).hidden = ['preparing', 'ringing'].includes(call.status);
        if (!dialog.open) dialog.showModal();
        const next = ['preparing', 'ringing'].includes(call.status) ? (caller ? 'legend_ringback' : call.incomingRingtoneResource) : '';
        if (next !== sound) {
          stopTone(); sound = next;
          if (next && /^[a-z0-9_]+$/.test(next)) {
            tone = new Audio(`/_content/Shared/calling/${next}.wav`); tone.loop = true;
            const playing = tone; const callId = call.id;
            playing.play().catch(() => { if (tone === playing && client.call?.id === callId) status.textContent += ' · ' + applicationCopy('Tap the call to enable sound'); });
          }
        }
      },
      media: (incoming, outgoing, remoteScreenSharing) => {
        if (retired) { remote.srcObject = null; local.srcObject = null; return; }
        remote.classList.toggle('is-screen-sharing', Boolean(remoteScreenSharing));
        if (incoming) { remote.srcObject = incoming; remote.play().catch(() => {
          if (remote.srcObject !== incoming || !client.call) return;
          status.textContent = applicationCopy('Tap Enable call audio to hear the call.');
          dialog.querySelector('[data-legend-call-action="audio"]').hidden = false;
        }); }
        else if (!outgoing) remote.srcObject = null;
        local.srcObject = outgoing;
      },
      warning: message => { if (!retired && client.call) status.textContent = applicationCopy(message); },
      failure: message => { if (retired) return; showError(message); if (!state.isOpen) openCommandCenter().then(() => showError(message)); }
    });
    state.callClient = client;
    dialog.addEventListener('click', event => {
      if (retired) return;
      if (tone?.paused) tone.play().catch(() => {});
      const button = event.target.closest('[data-legend-call-action]');
      if (!button) return;
      const action = button.dataset.legendCallAction;
      if (action === 'audio') {
        const stream = remote.srcObject; const callId = client.call?.id;
        remote.play().then(() => { if (remote.srcObject === stream && client.call?.id === callId) button.hidden = true; }).catch(() => { if (remote.srcObject === stream && client.call?.id === callId) status.textContent = applicationCopy('The browser blocked call audio. Check its audio permissions.'); });
        return;
      }
      const callId = client.call?.id;
      Promise.resolve().then(() => { if (!retired && client.call?.id === callId) return client[action](); }).then(() => {
        if (retired || client.call?.id !== callId) return;
        if (action === 'mute') button.setAttribute('aria-pressed', String(client.stream?.getAudioTracks().every(track => !track.enabled) || false));
        if (action === 'camera') button.setAttribute('aria-pressed', String(client.stream?.getVideoTracks().every(track => !track.enabled) || false));
        if (action === 'share') button.setAttribute('aria-pressed', String(Boolean(client.display)));
      }).catch(error => {
        if (retired || client.call?.id !== callId) return;
        if (action === 'share' || action === 'camera') status.textContent = error.message;
        else client.fail(error);
      });
    });
    dialog.addEventListener('cancel', event => { event.preventDefault(); client.end().catch(error => showError(error.message)); });
    for (const [id, video] of [['messagingChooseVoiceCall', false], ['messagingChooseVideoCall', true]]) {
      document.getElementById(id)?.addEventListener('click', () => { if (!retired) beginCallSelection(video); });
    }
    document.getElementById('messagingCancelCallSelection')?.addEventListener('click', cancelCallSelection);
    for (const [id, video] of [['messagingVoiceCall', false], ['messagingVideoCall', true]]) {
      document.getElementById(id)?.addEventListener('click', () => {
        if (!retired && state.active?.id) client.start(state.active.id, video, elements.threadTitle.textContent).catch(error => showError(error.message));
      });
    }
    connection.onreconnected(() => {
      if (retired) return;
      const scope = client.scope();
      client.sync().catch(error => { if (!retired) client.fail(error, scope); });
    });
    connection.onclose(() => { if (client.call) client.fail(new Error(applicationCopy('The call connection was lost.'))); });
    const authChannel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('legend-session-retirement') : null;
    const retire = () => {
      if (retired) return;
      retired = true; cancelCallSelection(); client.retire();
      document.getElementById('legendCallingPreferences')?.close();
      connection.stop().catch(() => {});
    };
    window.addEventListener('storage', event => { if (event.key === 'legend-session-retirement' && event.newValue) retire(); });
    if (authChannel) authChannel.onmessage = event => { if (event.data === 'signed-out') retire(); };
    document.addEventListener('submit', event => {
      const form = event.target;
      if (form instanceof HTMLFormElement && new URL(form.action, location.href).origin === location.origin && new URL(form.action, location.href).pathname.toLowerCase() === '/account/logout') {
        authChannel?.postMessage('signed-out');
        try { localStorage.setItem('legend-session-retirement', crypto.randomUUID()); } catch { /* BroadcastChannel remains available when storage is blocked. */ }
        retire();
      }
    }, true);
    window.addEventListener('pagehide', retire);
    window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });

    const preferences = document.getElementById('legendCallingPreferences');
    const preferenceForm = document.getElementById('legendCallingPreferencesForm');
    const notice = document.getElementById('legendCallingPreferencesStatus');
    const ringtone = document.getElementById('legendCallingRingtone');
    const wallpaper = document.getElementById('legendCallingWallpaper');
    const fill = (select, choices, selected) => {
      select.replaceChildren(...choices.map(choice => new Option(applicationCopy(choice.label), choice.id, false, choice.id === selected)));
    };
    document.getElementById('messagingCallingProfile')?.addEventListener('click', async () => {
      if (retired) return;
      try {
        const result = await client.command('preferences');
        if (retired) return;
        fill(ringtone, result.ringtones || [], result.preferences.ringtoneId);
        fill(wallpaper, result.wallpapers || [], result.preferences.wallpaperMode);
        notice.textContent = ''; preferences.showModal();
      } catch (error) { if (!retired) showError(error.message); }
    });
    preferenceForm.addEventListener('submit', async event => {
      event.preventDefault(); if (retired) return;
      const submit = preferenceForm.querySelector('[type=submit]'); submit.disabled = true;
      try {
        await client.command('preferences', { preferences: { ringtoneId: ringtone.value, wallpaperMode: wallpaper.value } });
        if (!retired) notice.textContent = applicationCopy('Calling preferences saved for this account on all devices.');
      } catch (error) { if (!retired) notice.textContent = error.message; }
      finally { submit.disabled = false; }
    });
    document.getElementById('legendCallingPreferencesClose').addEventListener('click', () => preferences.close());
    const photoLink = [...document.querySelectorAll('a[href]')].find(link => {
      const url = new URL(link.href, location.href);
      return url.origin === location.origin && ['/profile', '/account/manageprofile'].includes(url.pathname.toLowerCase());
    });
    const editPhoto = document.getElementById('legendCallingEditPhoto');
    editPhoto.hidden = !photoLink;
    editPhoto.addEventListener('click', () => { if (!retired) photoLink?.click(); });
  }

  async function startRealtime() {
    if (state.realtimeStarted) return;
    if (!window.signalR?.HubConnectionBuilder) {
      startPolling();
      return;
    }
    state.realtimeStarted = true;

    const connection = new window.signalR.HubConnectionBuilder()
      .withUrl('/messaginghub')
      .withAutomaticReconnect()
      .build();
    state.realtime = connection;
    const refreshForEvent = async (event, incomingMessage = false) => {
      try {
        if (state.active && event?.conversationId === state.active.id &&
            (!state.requestedConversationId || state.requestedConversationId === state.active.id)) {
          const shouldScrollToBottom = isNearMessageBottom();
          const viewed = incomingMessage && state.isOpen && !document.hidden && shouldScrollToBottom;
          await loadConversation(state.active.id, viewed, shouldScrollToBottom, true);
          if (!shouldScrollToBottom) elements.newMessages.hidden = false;
        }
        await refreshList();
      } catch (_) { }
    };
    attachCalling(connection);
    connection.on('messageReceived', event => refreshForEvent(event, true));
    connection.on('conversationUpdated', event => refreshForEvent(event));
    connection.onreconnecting(() => { stopPresence(); startPolling(); });
    connection.onreconnected(() => { stopPolling(); startPresence(); });
    connection.onclose(() => { stopPresence(); startPolling(); });
    try {
      await connection.start();
      stopPolling();
      startPresence();
      state.callClient?.sync().catch(error => showError(error.message));
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
      clearPresence();
      refreshPresence();
      markCommandCenterOpen();
      showError('');
      elements.window.focus({ preventScroll: true });
      // The retained thread and shell are already visible. Detail does not wait
      // for inbox enumeration or the authorized-contact directory.
      const version = state.navigationVersion;
      const lastConversationId = state.active?.id || readSession('last-conversation', '');
      const known = state.conversations.find(conversation => conversation.id === lastConversationId);
      if (lastConversationId && (!known || isConversationInRecipientScope(known)) && !state.draftTarget) {
        loadConversation(lastConversationId, false).catch(error => {
          if (state.isOpen && state.requestedConversationId === lastConversationId) showError(error.message);
        });
      }
      refreshList().catch(error => {
        if (state.isOpen && version === state.navigationVersion) showError(error.message);
      });
      loadRecipients().catch(error => {
        if (state.isOpen && version === state.navigationVersion) showError(error.message);
      });
    } catch (error) {
      showError(error.message);
    } finally {
      state.isOpening = false;
    }
  }

  function closeCommandCenter() {
    cancelCallSelection();
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
    clearPresence();
    cancelDetailRequests();
    state.navigationVersion += 1;
    state.requestedConversationId = null;
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

  if (supportsJourneyCircles()) {
    loadJourneyDashboard().catch(error => {
      console.error(
        'Journey Circles recommendation count failed to load.',
        error
      );
    });
  }
  elements.journeyProfileForm?.addEventListener('click', event => {
    const choice = event.target.closest('[data-journey-privacy-choice]');
    if (!(choice instanceof HTMLButtonElement)) return;
    toggleJourneyPrivacyChoice(choice);
  });

  elements.journeyProfileForm?.addEventListener('submit', saveJourneyProfile);
  elements.recipientScopeButtons.forEach(button => {
    button.addEventListener('click', () => {
      const scope = button.dataset.messagingRecipientScope || '';
      if (!setRecipientScope(scope)) return;

      renderConversations();
      renderSearchResults();
      renderConversation();
      loadRecipients().catch(error => showError(error.message));
    });
  });
  elements.search.addEventListener('input', () => {
    window.clearTimeout(state.searchTimer);
    const query = elements.search.value.trim();
    const requestId = ++state.searchRequestId;
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
  // Both web hosts publish the same design document already used by native.
  fetch('/design/legend-design.tokens.json', { credentials: 'same-origin', cache: 'no-cache' })
    .then(response => { if (!response.ok) throw new Error('Design unavailable'); return response.json(); })
    .then(design => {
      for (const [role, key] of [['incoming-background', 'navy'], ['incoming-text', 'onNavy'],
        ['outgoing-background', 'gold'], ['outgoing-text', 'onGold'], ['timestamp', 'chatTimestamp'], ['surface', 'surface'],
        ['presence-online-text', 'presenceOnlineText'], ['presence-online-fill', 'presenceOnlineFill'],
        ['presence-offline-text', 'presenceOfflineText'], ['presence-offline-fill', 'presenceOfflineFill']]) {
        const color = design.colors?.[key]?.light;
        if (/^#[0-9a-f]{6}$/i.test(color || '')) root.style.setProperty(`--messaging-${role}`, color);
      }
      for (const [group, prefix] of [['messageBubble', 'bubble'], ['contactCard', 'card']]) {
        for (const [key, css] of [['horizontalPadding','padding-x'], ['verticalPadding','padding-y'], ['cornerRadius','radius'], ['minimumHeight','min-height'], ['metadataGap','metadata-gap'], ['bodySize','body-size'], ['timestampSize','timestamp-size']]) {
          const value = design.messaging?.[group]?.[key];
          if (Number.isFinite(value)) root.style.setProperty(`--messaging-${prefix}-${css}`, `${value}px`);
        }
      }
      const callPortrait = design.sizes?.callPortrait;
      if (Number.isFinite(callPortrait) && callPortrait > 0) document.getElementById('legendBrowserCall')?.style.setProperty('--legend-call-portrait', `${callPortrait}px`);
      const bubble = design.messaging?.reactionBubble;
      if (bubble) {
        reactionBubbleSettings = bubble;
        for (const [key, css] of [['height','height'], ['touchTarget','touch-target'], ['horizontalPadding','padding'], ['itemSpacing','gap'], ['borderWidth','border'], ['trailingInset','trailing'], ['emojiSize','emoji-size']]) {
          if (Number.isFinite(bubble[key])) root.style.setProperty(`--messaging-reaction-${css}`, `${bubble[key]}px`);
        }
        for (const [key, css] of [['ownFillColor','own-fill'], ['otherFillColor','other-fill'], ['borderColor','border-color']]) {
          const color = design.colors?.[bubble[key]]?.light;
          if (/^#[0-9a-f]{6}$/i.test(color || '')) root.style.setProperty(`--messaging-reaction-${css}`, color);
        }
        if (Number.isFinite(bubble.ownFillOpacity)) root.style.setProperty('--messaging-reaction-own-opacity', `${bubble.ownFillOpacity * 100}%`);
        if (Number.isFinite(bubble.borderOpacity)) root.style.setProperty('--messaging-reaction-border-opacity', `${bubble.borderOpacity * 100}%`);
        root.style.setProperty('--messaging-reaction-reserve', '0px');
        root.style.setProperty('--messaging-reaction-inside', '0px');
        root.style.setProperty('--messaging-reaction-offset', '0px');
      }
      root.querySelectorAll('.messaging-reactions').forEach(reserveReactionOverlap);
      const semantic = design.platformSemanticColors;
      for (const [status, key] of [['read', 'success'], ['sent', 'danger']]) {
        const color = semantic?.[key]?.android;
        if (/^#[0-9a-f]{6}$/i.test(color || '')) root.style.setProperty(`--messaging-receipt-${status}`, color);
      }
    }).catch(() => {});
  document.addEventListener('visibilitychange', () => { clearPresence(); refreshPresence(); });
  window.addEventListener('pagehide', stopPresence);
  window.addEventListener('legend-signalr-ready', startRealtime);
  startRealtime();
})();
