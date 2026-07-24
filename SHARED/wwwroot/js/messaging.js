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
    closeConversation: root.querySelector('#messagingCloseConversation')
  };
  const unreadBadges = Array.from(document.querySelectorAll('[data-messaging-unread-badge]'));
  const state = {
    conversations: [],
    recipients: [],
    recipientMatches: [],
    recipientsLoaded: false,
    active: null,
    draftTarget: null,
    drafts: readSession('drafts', {}),
    scrollPositions: readSession('scroll-positions', {}),
    searchTimer: null,
    searchAbortController: null,
    searchRequestId: 0,
    isSearchingContacts: false,
    pollTimer: null,
    realtime: null,
    realtimeStarted: false,
    isOpen: false,
    isOpening: false,
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
    const isAvailable = Boolean(target) && !isClosed;
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

  function renderSearchResults() {
    const query = normalize(elements.search.value);
    elements.searchResults.replaceChildren();
    if (!query) {
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
    const matchingRecipients = state.recipientMatches.filter(recipient =>
      !existingCounterparties.has(normalize(recipient.userId)) &&
      matchesSearch([recipient.displayName, recipient.email].filter(Boolean).join(' '), query))
      .sort((left, right) =>
        searchRank(left, query) - searchRank(right, query) ||
        left.displayName.localeCompare(right.displayName));

    matchingConversations.forEach(conversation => {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'messaging-search-result';
      item.append(createAvatar(conversation.counterparty));
      const copy = document.createElement('span');
      copy.append(createTextElement('strong', '', conversation.counterparty?.displayName || 'Conversation'));
      copy.append(createTextElement(
        'small',
        '',
        `Existing conversation${conversation.unreadCount > 0 ? ` · ${conversation.unreadCount} unread` : ''}`));
      item.append(copy);
      item.addEventListener('click', () => {
        elements.search.value = '';
        renderSearchResults();
        loadConversation(conversation.id, true).catch(error => showError(error.message));
      });
      elements.searchResults.append(item);
    });

    matchingRecipients.forEach(recipient => {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'messaging-search-result';
      item.append(createAvatar(recipient));
      const copy = document.createElement('span');
      copy.append(createTextElement('strong', '', recipient.displayName));
      if (recipient.email) copy.append(createTextElement('small', '', recipient.email));
      item.append(copy);
      item.addEventListener('click', () => {
        state.active = null;
        state.draftTarget = recipient;
        state.pendingSubmission = null;
        elements.search.value = '';
        renderSearchResults();
        renderConversations();
        renderConversation();
        elements.messageBody.focus({ preventScroll: true });
      });
      elements.searchResults.append(item);
    });

    if (state.isSearchingContacts) {
      elements.searchResults.append(createTextElement('p', 'messaging-search-empty', 'Searching authorized contacts…'));
    } else if (!matchingConversations.length && !matchingRecipients.length) {
      elements.searchResults.append(createTextElement('p', 'messaging-search-empty', 'No authorized conversations or recipients found.'));
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
    state.recipientMatches = state.recipients;
    state.recipientsLoaded = true;
    renderSearchResults();
  }

  async function searchRecipients(query) {
    const requestId = ++state.searchRequestId;
    state.searchAbortController?.abort();
    if (!query) {
      state.isSearchingContacts = false;
      state.recipientMatches = state.recipients;
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
    } catch (error) {
      if (error?.name !== 'AbortError' && requestId === state.searchRequestId) {
        showError(error.message);
        state.recipientMatches = [];
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
              targetUserId: target.userId,
              targetParticipantType: target.participantType,
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

  async function ensureSignalR() {
    if (window.signalR) return true;
    const existing = document.querySelector('script[data-messaging-signalr]');
    if (existing) {
      return new Promise(resolve => {
        existing.addEventListener('load', () => resolve(Boolean(window.signalR)), { once: true });
        existing.addEventListener('error', () => resolve(false), { once: true });
      });
    }
    return new Promise(resolve => {
      const script = document.createElement('script');
      script.src = 'https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.5/signalr.min.js';
      script.crossOrigin = 'anonymous';
      script.dataset.messagingSignalr = 'true';
      script.onload = () => resolve(Boolean(window.signalR));
      script.onerror = () => resolve(false);
      document.head.append(script);
    });
  }

  async function startRealtime() {
    if (state.realtimeStarted) return;
    state.realtimeStarted = true;
    if (!await ensureSignalR()) {
      startPolling();
      return;
    }

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
    } catch (_) {
      startPolling();
    }
  }

  async function ensureDashboardStyles() {
    const existing = document.querySelector('link[href*="dashboard-home-shared.css"]');
    if (existing) {
      if (existing.sheet || existing.dataset.messagingDashboardReady === 'true') return;
      await new Promise(resolve => {
        existing.addEventListener('load', resolve, { once: true });
        existing.addEventListener('error', resolve, { once: true });
      });
      return;
    }
    await new Promise(resolve => {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = '/_content/Shared/css/dashboard-home-shared.css';
      link.dataset.messagingDashboardSource = 'true';
      link.onload = () => {
        link.dataset.messagingDashboardReady = 'true';
        resolve();
      };
      link.onerror = resolve;
      document.head.append(link);
    });
  }

  async function openCommandCenter(trigger) {
    if (state.isOpen || state.isOpening) return;
    state.isOpening = true;
    state.lastTrigger = trigger || document.activeElement;
    try {
      await ensureDashboardStyles();
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
  elements.close.addEventListener('click', closeCommandCenter);
  elements.search.addEventListener('input', () => {
    window.clearTimeout(state.searchTimer);
    const query = elements.search.value.trim();
    renderSearchResults();
    state.searchTimer = window.setTimeout(() => searchRecipients(query), 260);
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

  if (isCommandCenterMarkedOpen()) {
    openCommandCenter(null).catch(() => { });
  } else {
    ensureDashboardStyles().then(() => refreshList()).catch(() => { });
  }
  startRealtime();
})();
