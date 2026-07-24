(() => {
  const root = document.querySelector('[data-messaging-workspace]');
  if (!root) return;

  const currentUserId = (root.dataset.currentUserId || '').trim().toLowerCase();
  const token = root.querySelector('#messagingAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const elements = {
    error: root.querySelector('#messagingError'),
    newConversation: root.querySelector('#messagingNewConversation'),
    composer: root.querySelector('#messagingConversationComposer'),
    cancelConversation: root.querySelector('#messagingCancelConversation'),
    recipient: root.querySelector('#messagingRecipient'),
    subject: root.querySelector('#messagingSubject'),
    initialBody: root.querySelector('#messagingInitialBody'),
    startConversation: root.querySelector('#messagingStartConversation'),
    search: root.querySelector('#messagingSearch'),
    includeClosed: root.querySelector('#messagingIncludeClosed'),
    list: root.querySelector('#messagingConversationList'),
    threadEmpty: root.querySelector('#messagingThreadEmpty'),
    threadContent: root.querySelector('#messagingThreadContent'),
    threadTitle: root.querySelector('#messagingThreadTitle'),
    threadSubject: root.querySelector('#messagingThreadSubject'),
    messages: root.querySelector('#messagingMessages'),
    sendForm: root.querySelector('#messagingSendForm'),
    messageBody: root.querySelector('#messagingMessageBody'),
    files: root.querySelector('#messagingFiles'),
    sendButton: root.querySelector('#messagingSendButton'),
    mute: root.querySelector('#messagingMuteConversation'),
    close: root.querySelector('#messagingCloseConversation')
  };
  const state = { conversations: [], active: null, searchTimer: null };

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

  function formatDate(value) {
    if (!value) return '';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
  }

  function requestHeaders(json) {
    const headers = { 'X-Requested-With': 'XMLHttpRequest' };
    if (token) headers.RequestVerificationToken = token;
    if (json) headers['Content-Type'] = 'application/json';
    return headers;
  }

  async function request(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'same-origin',
      ...options,
      headers: { ...requestHeaders(Boolean(options.body && typeof options.body === 'string')), ...(options.headers || {}) }
    });
    let data = null;
    try { data = await response.json(); } catch (_) { }
    if (!response.ok) {
      throw new Error(data?.errorMessage || 'The messaging request could not be completed.');
    }
    return data;
  }

  function createTextElement(tag, className, value) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    element.textContent = value || '';
    return element;
  }

  function renderConversations() {
    elements.list.replaceChildren();
    if (state.conversations.length === 0) {
      elements.list.append(createTextElement('p', 'messaging-thread-empty', 'No conversations found.'));
      return;
    }

    state.conversations.forEach(conversation => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'messaging-conversation-item';
      if (state.active?.id === conversation.id) button.classList.add('is-active');
      button.dataset.conversationId = conversation.id;
      button.append(createTextElement('span', 'messaging-conversation-title', conversation.counterparty?.displayName || 'Conversation'));
      if (conversation.subject) button.append(createTextElement('span', 'messaging-conversation-subject', conversation.subject));
      if (conversation.lastMessagePreview) button.append(createTextElement('span', 'messaging-conversation-preview', conversation.lastMessagePreview));
      if (conversation.unreadCount > 0) button.append(createTextElement('span', 'messaging-unread-count', String(conversation.unreadCount)));
      button.addEventListener('click', () => loadConversation(conversation.id, true));
      elements.list.append(button);
    });
  }

  function participantName(conversation, userId) {
    return conversation.participants?.find(x => (x.userId || '').toLowerCase() === (userId || '').toLowerCase())?.displayName || 'Participant';
  }

  function renderConversation() {
    const conversation = state.active;
    if (!conversation) {
      elements.threadEmpty.hidden = false;
      elements.threadContent.hidden = true;
      return;
    }

    elements.threadEmpty.hidden = true;
    elements.threadContent.hidden = false;
    const counterparty = conversation.participants?.find(x => (x.userId || '').toLowerCase() !== currentUserId);
    elements.threadTitle.textContent = counterparty?.displayName || 'Conversation';
    elements.threadSubject.textContent = conversation.subject || '';
    elements.mute.textContent = conversation.isMuted ? 'Unmute' : 'Mute';
    elements.close.textContent = conversation.isClosed ? 'Reopen' : 'Close';
    elements.messageBody.disabled = Boolean(conversation.isClosed);
    elements.files.disabled = Boolean(conversation.isClosed);
    elements.sendButton.disabled = Boolean(conversation.isClosed);
    elements.messages.replaceChildren();

    (conversation.messages || []).forEach(message => {
      const card = document.createElement('article');
      card.className = 'messaging-message';
      if ((message.senderUserId || '').toLowerCase() === currentUserId) card.classList.add('is-own');
      const meta = document.createElement('div');
      meta.className = 'messaging-message-meta';
      meta.append(createTextElement('span', 'messaging-message-sender', participantName(conversation, message.senderUserId)));
      meta.append(createTextElement('time', '', formatDate(message.sentUtc)));
      card.append(meta);
      if (message.isDeleted) {
        card.append(createTextElement('p', 'messaging-message-body', 'This message was deleted.'));
      } else {
        card.append(createTextElement('p', 'messaging-message-body', message.body));
      }
      if (message.attachments?.length) {
        const attachments = document.createElement('div');
        attachments.className = 'messaging-attachments';
        message.attachments.forEach(attachment => {
          if (attachment.canDownload) {
            const link = document.createElement('a');
            link.className = 'messaging-attachment';
            link.href = `/Messaging/Attachments/${encodeURIComponent(attachment.id)}`;
            link.textContent = attachment.originalFileName;
            attachments.append(link);
          } else {
            attachments.append(createTextElement(
              'span',
              'messaging-attachment is-pending',
              `${attachment.originalFileName} (${attachment.scanStatus})`));
          }
        });
        card.append(attachments);
      }
      elements.messages.append(card);
    });
    elements.messages.scrollTop = elements.messages.scrollHeight;
  }

  async function refreshList() {
    const parameters = new URLSearchParams();
    const search = elements.search.value.trim();
    if (search) parameters.set('search', search);
    if (elements.includeClosed.checked) parameters.set('includeClosed', 'true');
    const result = await request(`/Messaging/Conversations${parameters.size ? `?${parameters}` : ''}`);
    state.conversations = result.conversations || [];
    renderConversations();
  }

  async function loadConversation(conversationId, markRead) {
    const result = await request(`/Messaging/Conversations/${encodeURIComponent(conversationId)}`);
    state.active = result.conversation;
    renderConversation();
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
    const result = await request('/Messaging/Recipients');
    elements.recipient.replaceChildren();
    const initial = document.createElement('option');
    initial.value = '';
    initial.textContent = 'Choose a recipient';
    elements.recipient.append(initial);
    (result.recipients || []).forEach(recipient => {
      const option = document.createElement('option');
      option.value = recipient.userId;
      option.dataset.participantType = recipient.participantType;
      option.textContent = `${recipient.displayName}${recipient.email ? ` — ${recipient.email}` : ''}`;
      elements.recipient.append(option);
    });
  }

  async function uploadAttachments(messageId) {
    const files = Array.from(elements.files.files || []);
    for (const file of files) {
      const formData = new FormData();
      formData.append('file', file);
      await request(`/Messaging/Messages/${encodeURIComponent(messageId)}/Attachments`, {
        method: 'POST',
        body: formData,
        headers: token ? { RequestVerificationToken: token } : {}
      });
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

  async function startRealtime() {
    if (!window.signalR) return;
    const connection = new window.signalR.HubConnectionBuilder()
      .withUrl('/messaginghub')
      .withAutomaticReconnect()
      .build();
    const refreshForEvent = async event => {
      try {
        await refreshList();
        if (state.active && event?.conversationId === state.active.id) {
          await loadConversation(state.active.id, false);
        }
      } catch (_) { }
    };
    connection.on('messageReceived', refreshForEvent);
    connection.on('conversationUpdated', refreshForEvent);
    const connect = async () => {
      try { await connection.start(); }
      catch (_) { window.setTimeout(connect, 4000); }
    };
    await connect();
  }

  elements.newConversation.addEventListener('click', async () => {
    showError('');
    try {
      await loadRecipients();
      elements.composer.hidden = false;
      elements.recipient.focus();
    } catch (error) {
      showError(error.message);
    }
  });
  elements.cancelConversation.addEventListener('click', () => { elements.composer.hidden = true; });
  elements.startConversation.addEventListener('click', async () => {
    const selected = elements.recipient.selectedOptions[0];
    if (!selected?.value || !elements.initialBody.value.trim()) {
      showError('Choose a recipient and enter a message.');
      return;
    }
    elements.startConversation.disabled = true;
    showError('');
    try {
      const result = await request('/Messaging/Conversations', {
        method: 'POST',
        body: JSON.stringify({
          targetUserId: selected.value,
          targetParticipantType: selected.dataset.participantType,
          subject: elements.subject.value.trim() || null,
          body: elements.initialBody.value.trim(),
          clientMessageId: clientMessageId()
        })
      });
      elements.composer.hidden = true;
      elements.initialBody.value = '';
      elements.subject.value = '';
      await refreshList();
      await loadConversation(result.conversation.id, false);
    } catch (error) {
      showError(error.message);
    } finally {
      elements.startConversation.disabled = false;
    }
  });
  elements.search.addEventListener('input', () => {
    window.clearTimeout(state.searchTimer);
    state.searchTimer = window.setTimeout(() => refreshList().catch(error => showError(error.message)), 250);
  });
  elements.includeClosed.addEventListener('change', () => refreshList().catch(error => showError(error.message)));
  elements.sendForm.addEventListener('submit', async event => {
    event.preventDefault();
    if (!state.active || !elements.messageBody.value.trim()) return;
    elements.sendButton.disabled = true;
    showError('');
    try {
      const result = await request(`/Messaging/Conversations/${encodeURIComponent(state.active.id)}/Messages`, {
        method: 'POST',
        body: JSON.stringify({ body: elements.messageBody.value.trim(), clientMessageId: clientMessageId() })
      });
      await uploadAttachments(result.message.id);
      elements.messageBody.value = '';
      elements.files.value = '';
      await loadConversation(state.active.id, false);
      await refreshList();
    } catch (error) {
      showError(error.message);
    } finally {
      elements.sendButton.disabled = Boolean(state.active?.isClosed);
    }
  });
  elements.mute.addEventListener('click', () => {
    if (state.active) notifyConversationAction('Muted', { isMuted: !state.active.isMuted }).catch(error => showError(error.message));
  });
  elements.close.addEventListener('click', () => {
    if (state.active) notifyConversationAction('Closed', { isClosed: !state.active.isClosed }).catch(error => showError(error.message));
  });

  refreshList().catch(error => showError(error.message));
  startRealtime();
})();
