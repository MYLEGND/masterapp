(() => {
    'use strict';

    const trigger = document.getElementById('legendFounderAiTrigger');
    const modalElement = document.getElementById('legendFounderAiModal');

    if (!trigger || !modalElement || typeof bootstrap === 'undefined') {
        return;
    }

    const STORAGE_KEY = 'legendFounderAi.conversations.v1';
    const UI_STORAGE_KEY = 'legendFounderAi.ui.v2';
    const MAX_CONVERSATIONS = 30;
    const MAX_MESSAGES = 30;
    const MOBILE_QUERY = '(max-width: 820px)';

    const transcript = document.getElementById('legendFounderAiTranscript');
    const welcome = document.getElementById('legendFounderAiWelcome');
    const form = document.getElementById('legendFounderAiForm');
    const input = document.getElementById('legendFounderAiInput');
    const send = document.getElementById('legendFounderAiSend');
    const newConversation = document.getElementById('legendFounderAiNew');
    const clearHistory = document.getElementById('legendFounderAiClearHistory');
    const history = document.getElementById('legendFounderAiHistory');
    const historyEmpty = document.getElementById('legendFounderAiHistoryEmpty');
    const conversationCount = document.getElementById('legendFounderAiConversationCount');
    const status = document.getElementById('legendFounderAiStatus');
    const subtitle = document.getElementById('legendFounderAiSubtitle');
    const conversationState = document.getElementById('legendFounderAiConversationState');
    const founderCommandConfirmed = document.getElementById(
        'legendFounderAiFounderCommandConfirmed'
    );
    const sidebar = document.getElementById('legendFounderAiSidebar');
    const sidebarCollapse = document.getElementById('legendFounderAiSidebarCollapse');
    const sidebarScrim = document.getElementById('legendFounderAiSidebarScrim');
    const mobileMenu = document.getElementById('legendFounderAiMobileMenu');
    const mobileNew = document.getElementById('legendFounderAiMobileNew');

    const modeButtons = Array.from(
        modalElement.querySelectorAll('[data-legend-ai-mode]')
    );

    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

    const logoSource =
        modalElement.querySelector('.legend-founder-ai-logo')?.getAttribute('src') ||
        '/images/legend-ai/legendai.png';

    let busy = false;
    let state = loadState();
    let uiState = loadUiState();
    let lastTranscriptScrollTop = 0;

    ensureActiveConversation();
    applyDesktopSidebarState();
    syncViewportHeight();

    function isMobile() {
        return window.matchMedia(MOBILE_QUERY).matches;
    }

    function loadUiState() {
        try {
            const raw = window.localStorage.getItem(UI_STORAGE_KEY);
            const parsed = raw ? JSON.parse(raw) : null;

            return {
                sidebarCollapsed:
                    parsed?.sidebarCollapsed === true
            };
        } catch {
            return {
                sidebarCollapsed: false
            };
        }
    }

    function saveUiState() {
        try {
            window.localStorage.setItem(
                UI_STORAGE_KEY,
                JSON.stringify(uiState)
            );
        } catch {
            // UI preference persistence is optional.
        }
    }

    function syncViewportHeight() {
        const height =
            window.visualViewport?.height ||
            window.innerHeight;

        modalElement.style.setProperty(
            '--legend-ai-viewport-height',
            `${Math.round(height)}px`
        );
    }

    function focusComposer() {
        if (isMobile()) {
            return;
        }

        window.setTimeout(
            () => input?.focus({ preventScroll: true }),
            80
        );
    }

    function applyDesktopSidebarState() {
        if (isMobile()) {
            modalElement.classList.remove('is-sidebar-collapsed');
            sidebarCollapse?.setAttribute('aria-expanded', 'true');
            return;
        }

        modalElement.classList.toggle(
            'is-sidebar-collapsed',
            uiState.sidebarCollapsed
        );

        sidebarCollapse?.setAttribute(
            'aria-expanded',
            uiState.sidebarCollapsed ? 'false' : 'true'
        );

        if (sidebarCollapse) {
            sidebarCollapse.title =
                uiState.sidebarCollapsed
                    ? 'Expand sidebar'
                    : 'Collapse sidebar';

            sidebarCollapse.setAttribute(
                'aria-label',
                uiState.sidebarCollapsed
                    ? 'Expand conversation sidebar'
                    : 'Collapse conversation sidebar'
            );
        }
    }

    function toggleDesktopSidebar() {
        if (isMobile()) {
            return;
        }

        uiState.sidebarCollapsed = !uiState.sidebarCollapsed;
        saveUiState();
        applyDesktopSidebarState();
    }

    function setSidebarOpen(open) {
        if (!isMobile()) {
            modalElement.classList.remove('is-sidebar-open');
            mobileMenu?.setAttribute('aria-expanded', 'false');
            sidebar?.removeAttribute('aria-hidden');
            return;
        }

        modalElement.classList.toggle('is-sidebar-open', open);
        mobileMenu?.setAttribute(
            'aria-expanded',
            open ? 'true' : 'false'
        );
        sidebar?.setAttribute(
            'aria-hidden',
            open ? 'false' : 'true'
        );
    }

    function setReadingMode(active) {
        const next =
            isMobile() &&
            active === true &&
            !modalElement.classList.contains('is-sidebar-open');

        modalElement.classList.toggle('is-reading', next);
    }

    trigger.addEventListener('click', () => {
        syncViewportHeight();
        setReadingMode(false);
        setSidebarOpen(false);
        applyDesktopSidebarState();
        modal.show();
        renderAll({ forceBottom: true });
        focusComposer();
    });

    modalElement.addEventListener('shown.bs.modal', () => {
        syncViewportHeight();

        if (isMobile()) {
            input?.blur();
            sidebar?.setAttribute('aria-hidden', 'true');
        }
    });

    modalElement.addEventListener('hidden.bs.modal', () => {
        setReadingMode(false);
        setSidebarOpen(false);
        input?.blur();
    });

    window.addEventListener('resize', () => {
        syncViewportHeight();
        setReadingMode(false);
        setSidebarOpen(false);
        applyDesktopSidebarState();
    });

    window.visualViewport?.addEventListener(
        'resize',
        syncViewportHeight
    );

    function createId() {
        if (
            window.crypto &&
            typeof window.crypto.randomUUID === 'function'
        ) {
            return window.crypto.randomUUID();
        }

        return (
            Date.now().toString(36) +
            '-' +
            Math.random().toString(36).slice(2)
        );
    }

    function newConversationRecord(mode = 'legend') {
        const now = new Date().toISOString();

        return {
            id: createId(),
            mode,
            title: 'New conversation',
            createdUtc: now,
            updatedUtc: now,
            messages: []
        };
    }

    function defaultState() {
        const conversation = newConversationRecord();

        return {
            activeConversationId: conversation.id,
            conversations: [conversation]
        };
    }

    function loadState() {
        try {
            const raw =
                window.localStorage.getItem(STORAGE_KEY);

            if (!raw) {
                return defaultState();
            }

            const parsed = JSON.parse(raw);

            if (
                !parsed ||
                !Array.isArray(parsed.conversations)
            ) {
                return defaultState();
            }

            parsed.conversations =
                parsed.conversations
                    .filter(
                        conversation =>
                            conversation &&
                            typeof conversation.id === 'string' &&
                            Array.isArray(conversation.messages)
                    )
                    .slice(0, MAX_CONVERSATIONS);

            if (parsed.conversations.length === 0) {
                return defaultState();
            }

            return parsed;
        } catch {
            return defaultState();
        }
    }

    function saveState() {
        try {
            state.conversations =
                state.conversations
                    .sort(
                        (a, b) =>
                            new Date(b.updatedUtc) -
                            new Date(a.updatedUtc)
                    )
                    .slice(0, MAX_CONVERSATIONS);

            window.localStorage.setItem(
                STORAGE_KEY,
                JSON.stringify(state)
            );
        } catch {
            // Browser conversation persistence is optional.
        }
    }

    function ensureActiveConversation() {
        const found =
            state.conversations.find(
                conversation =>
                    conversation.id ===
                    state.activeConversationId
            );

        if (found) {
            return found;
        }

        const conversation = newConversationRecord();

        state.conversations.unshift(conversation);
        state.activeConversationId = conversation.id;

        saveState();

        return conversation;
    }

    function activeConversation() {
        return ensureActiveConversation();
    }

    function setMode(nextMode) {
        if (busy) {
            return;
        }

        if (
            nextMode !== 'legend' &&
            nextMode !== 'teacher'
        ) {
            if (status) {
                status.textContent =
                    'Conversation mode is invalid. Select Legend® Ai or OpenAI Teacher.';
            }
            return;
        }

        const requestedMode = nextMode;

        const current = activeConversation();

        if (current.mode === requestedMode) {
            return;
        }

        // One browser conversation has exactly one responder identity. Never
        // relabel an existing Legend® Ai transcript as OpenAI Teacher (or the
        // reverse), because that would feed one AI's prior responses to the
        // other under the wrong role. A mode change starts a clean thread while
        // preserving both histories independently.
        const conversation = newConversationRecord(requestedMode);
        state.conversations.unshift(conversation);
        state.activeConversationId = conversation.id;
        saveState();
        setSidebarOpen(false);
        setReadingMode(false);
        renderAll({ forceBottom: true });

        if (status) {
            status.textContent = '';
        }

        focusComposer();
    }

    function startNewConversation() {
        if (busy) {
            return;
        }

        const current = activeConversation();
        const conversation =
            newConversationRecord(current.mode);

        state.conversations.unshift(conversation);
        state.activeConversationId = conversation.id;

        saveState();
        setSidebarOpen(false);
        setReadingMode(false);
        renderAll({ forceBottom: true });

        if (status) {
            status.textContent = '';
        }

        focusComposer();
    }

    function openConversation(id) {
        if (busy) {
            return;
        }

        const conversation =
            state.conversations.find(
                item => item.id === id
            );

        if (!conversation) {
            return;
        }

        state.activeConversationId = id;

        saveState();
        setSidebarOpen(false);
        setReadingMode(false);
        renderAll({ forceBottom: true });
        focusComposer();
    }

    function clearAllHistory() {
        if (busy) {
            return;
        }

        state = defaultState();

        try {
            window.localStorage.removeItem(STORAGE_KEY);
        } catch {
        }

        saveState();
        setSidebarOpen(false);
        setReadingMode(false);
        renderAll({ forceBottom: true });

        if (status) {
            status.textContent = '';
        }

        focusComposer();
    }

    function updateConversationTitle(conversation) {
        if (conversation.title !== 'New conversation') {
            return;
        }

        const firstUser =
            conversation.messages.find(
                message => message.role === 'user'
            );

        if (!firstUser) {
            return;
        }

        let title =
            firstUser.content
                .replace(/\s+/g, ' ')
                .trim();

        if (title.length > 42) {
            title =
                title.slice(0, 39) + '…';
        }

        conversation.title =
            title || 'New conversation';
    }

    function isTranscriptNearBottom() {
        if (!transcript) {
            return true;
        }

        return (
            transcript.scrollHeight -
            transcript.scrollTop -
            transcript.clientHeight
        ) < 72;
    }

    function renderAll({ forceBottom = false } = {}) {
        const wasNearBottom =
            isTranscriptNearBottom();

        const priorScrollTop =
            transcript?.scrollTop || 0;

        renderModes();
        renderHistory();
        renderConversation();

        if (!transcript) {
            return;
        }

        if (forceBottom || wasNearBottom) {
            scrollToBottom();
        } else {
            transcript.scrollTop = priorScrollTop;
        }

        lastTranscriptScrollTop =
            transcript.scrollTop;
    }

    function renderModes() {
        const conversation = activeConversation();

        for (const button of modeButtons) {
            const active =
                button.dataset.legendAiMode ===
                conversation.mode;

            button.classList.toggle(
                'is-active',
                active
            );

            button.setAttribute(
                'aria-selected',
                active ? 'true' : 'false'
            );
        }

        if (subtitle) {
            subtitle.textContent =
                conversation.mode === 'teacher'
                    ? 'Direct OpenAI Teacher · LEGEND native inference bypassed'
                    : 'Legend® Ai · governed native intelligence first';
        }

        if (input) {
            input.placeholder =
                conversation.mode === 'teacher'
                    ? 'Message the OpenAI Teacher…'
                    : 'Message Legend® Ai…';
        }
    }

    function renderHistory() {
        if (!history || !historyEmpty) {
            return;
        }

        history.replaceChildren();

        const conversations =
            [...state.conversations]
                .sort(
                    (a, b) =>
                        new Date(b.updatedUtc) -
                        new Date(a.updatedUtc)
                );

        if (conversationCount) {
            conversationCount.textContent =
                String(conversations.length);
        }

        if (conversations.length === 0) {
            historyEmpty.classList.add(
                'is-visible'
            );
            return;
        }

        historyEmpty.classList.remove(
            'is-visible'
        );

        for (const conversation of conversations) {
            const button =
                document.createElement('button');

            button.type = 'button';
            button.className =
                'legend-founder-ai-history-item';

            if (
                conversation.id ===
                state.activeConversationId
            ) {
                button.classList.add('is-active');
            }

            const mainCopy =
                document.createElement('span');

            mainCopy.className =
                'legend-founder-ai-history-main';

            const title =
                document.createElement('span');

            title.className =
                'legend-founder-ai-history-title';

            title.textContent =
                conversation.title ||
                'New conversation';

            const preview =
                document.createElement('span');

            preview.className =
                'legend-founder-ai-history-preview';

            const last =
                conversation.messages[
                    conversation.messages.length - 1
                ];

            preview.textContent =
                last?.content ||
                (
                    conversation.mode === 'teacher'
                        ? 'OpenAI Teacher'
                        : 'Legend® Ai'
                );

            const time =
                document.createElement('span');

            time.className =
                'legend-founder-ai-history-time';

            time.textContent =
                formatRelativeTime(
                    conversation.updatedUtc
                );

            mainCopy.appendChild(title);
            mainCopy.appendChild(preview);

            button.appendChild(mainCopy);
            button.appendChild(time);

            button.addEventListener(
                'click',
                () =>
                    openConversation(
                        conversation.id
                    )
            );

            history.appendChild(button);
        }
    }

    function formatRelativeTime(iso) {
        const timestamp =
            new Date(iso).getTime();

        if (Number.isNaN(timestamp)) {
            return '';
        }

        const elapsed =
            Date.now() - timestamp;

        const minutes =
            Math.floor(elapsed / 60000);

        if (minutes < 1) {
            return 'Now';
        }

        if (minutes < 60) {
            return `${minutes}m`;
        }

        const hours =
            Math.floor(minutes / 60);

        if (hours < 24) {
            return `${hours}h`;
        }

        const days =
            Math.floor(hours / 24);

        if (days < 7) {
            return `${days}d`;
        }

        return new Date(timestamp)
            .toLocaleDateString(
                undefined,
                {
                    month: 'short',
                    day: 'numeric'
                }
            );
    }

    function renderConversation() {
        if (!transcript) {
            return;
        }

        const conversation =
            activeConversation();

        transcript.replaceChildren();

        if (conversation.messages.length === 0) {
            if (welcome) {
                transcript.appendChild(
                    welcome.cloneNode(true)
                );
            }
        } else {
            for (const message of conversation.messages) {
                appendBubble(
                    message.role,
                    message.content,
                    false
                );
            }
        }

        if (conversationState) {
            conversationState.textContent =
                conversation.title ||
                'New conversation';
        }
    }

    function appendBubble(
        role,
        content,
        scroll = true
    ) {
        if (!transcript) {
            return;
        }

        const message =
            document.createElement('article');

        message.className =
            `legend-founder-ai-message ${
                role === 'user'
                    ? 'is-user'
                    : 'is-assistant'
            }`;

        if (role !== 'user') {
            const mark =
                document.createElement('span');

            mark.className =
                'legend-founder-ai-message-mark';

            const logo =
                document.createElement('img');

            logo.src = logoSource;
            logo.alt = '';
            logo.setAttribute(
                'aria-hidden',
                'true'
            );

            mark.appendChild(logo);
            message.appendChild(mark);
        }

        const bubble =
            document.createElement('div');

        bubble.className =
            'legend-founder-ai-bubble';

        bubble.textContent = content;

        message.appendChild(bubble);
        transcript.appendChild(message);

        if (scroll) {
            scrollToBottom();
        }
    }

    function scrollToBottom() {
        if (!transcript) {
            return;
        }

        transcript.scrollTop =
            transcript.scrollHeight;

        lastTranscriptScrollTop =
            transcript.scrollTop;

        setReadingMode(false);
    }

    function setBusy(
        value,
        message = ''
    ) {
        busy = value;

        if (send) {
            send.disabled = value;
        }

        if (input) {
            input.disabled = value;
        }

        if (newConversation) {
            newConversation.disabled = value;
        }

        if (mobileNew) {
            mobileNew.disabled = value;
        }

        if (clearHistory) {
            clearHistory.disabled = value;
        }

        for (const button of modeButtons) {
            button.disabled = value;
        }

        if (status) {
            status.textContent = message;
        }
    }

    function applyOperationalProgress(payload) {
        const update = payload?.progress;
        if (!status || !update?.message) return;
        status.textContent =
            payload.type === 'heartbeat' && Number.isFinite(payload.elapsedSeconds)
                ? `${update.message} · ${payload.elapsedSeconds}s`
                : update.message;
    }

    async function consumeProgressStream(response, signal) {
        if (!response.ok || !response.body) return;
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        const consumeLine = line => {
            const trimmed = line.trim();
            if (!trimmed) return;
            try {
                const payload = JSON.parse(trimmed);
                if (payload?.type === 'progress' || payload?.type === 'heartbeat')
                    applyOperationalProgress(payload);
            } catch { }
        };

        try {
            while (!signal?.aborted) {
                const chunk = await reader.read();
                buffer += decoder.decode(chunk.value || new Uint8Array(), { stream: !chunk.done });
                let newline = buffer.indexOf('\n');
                while (newline >= 0) {
                    consumeLine(buffer.slice(0, newline));
                    buffer = buffer.slice(newline + 1);
                    newline = buffer.indexOf('\n');
                }
                if (chunk.done) break;
            }
        } finally {
            try { await reader.cancel(); } catch { }
        }
    }

    function progressUrlFor(chatUrl, operationId) {
        const url = new URL(chatUrl, window.location.href);
        url.pathname = url.pathname.replace(/\/chat\/?$/i, `/progress/${operationId}`);
        return url.toString();
    }

    function resizeInput() {
        if (!input) {
            return;
        }

        input.style.height = 'auto';

        input.style.height =
            `${Math.min(
                input.scrollHeight,
                isMobile() ? 138 : 152
            )}px`;
    }

    input?.addEventListener(
        'input',
        resizeInput
    );

    input?.addEventListener(
        'focus',
        () => {
            setReadingMode(false);
            window.setTimeout(
                syncViewportHeight,
                40
            );
        }
    );

    input?.addEventListener(
        'keydown',
        event => {
            if (
                event.key === 'Enter' &&
                !event.shiftKey
            ) {
                event.preventDefault();

                if (!busy) {
                    form?.requestSubmit();
                }
            }
        }
    );

    transcript?.addEventListener(
        'scroll',
        () => {
            if (!isMobile()) {
                return;
            }

            const current =
                transcript.scrollTop;

            const delta =
                current -
                lastTranscriptScrollTop;

            const nearBottom =
                isTranscriptNearBottom();

            if (nearBottom) {
                setReadingMode(false);
            } else if (
                delta > 6 &&
                current > 44
            ) {
                setReadingMode(true);
                setSidebarOpen(false);
                input?.blur();
            } else if (delta < -6) {
                setReadingMode(false);
            }

            lastTranscriptScrollTop =
                current;
        },
        { passive: true }
    );

    sidebarCollapse?.addEventListener(
        'click',
        toggleDesktopSidebar
    );

    newConversation?.addEventListener(
        'click',
        startNewConversation
    );

    mobileNew?.addEventListener(
        'click',
        startNewConversation
    );

    clearHistory?.addEventListener(
        'click',
        clearAllHistory
    );

    mobileMenu?.addEventListener(
        'click',
        () => {
            setReadingMode(false);

            setSidebarOpen(
                !modalElement.classList
                    .contains('is-sidebar-open')
            );
        }
    );

    sidebarScrim?.addEventListener(
        'click',
        () =>
            setSidebarOpen(false)
    );

    for (const button of modeButtons) {
        button.addEventListener(
            'click',
            () => {
                const requestedMode =
                    button.dataset.legendAiMode;

                if (requestedMode) {
                    setMode(requestedMode);
                }
            }
        );
    }

    form?.addEventListener(
        'submit',
        async event => {
            event.preventDefault();

            if (busy || !input) {
                return;
            }

            const text =
                input.value.trim();

            if (!text) {
                return;
            }

            const conversation =
                activeConversation();

            conversation.messages.push({
                role: 'user',
                content: text
            });

            if (
                conversation.messages.length >
                MAX_MESSAGES
            ) {
                conversation.messages.splice(
                    0,
                    conversation.messages.length -
                    MAX_MESSAGES
                );
            }

            conversation.updatedUtc =
                new Date().toISOString();

            updateConversationTitle(conversation);
            saveState();

            input.value = '';
            resizeInput();
            setReadingMode(false);
            renderAll({ forceBottom: true });

            setBusy(
                true,
                ''
            );

            try {
                const token =
                    form.querySelector(
                        'input[name="__RequestVerificationToken"]'
                    )?.value || '';

                const operationId = crypto.randomUUID();
                const progressAbort = new AbortController();
                const progressPromise =
                    fetch(
                        progressUrlFor(modalElement.dataset.chatUrl, operationId),
                        {
                            method: 'GET',
                            credentials: 'same-origin',
                            headers: {
                                'Accept': 'application/x-ndjson',
                                'X-Requested-With': 'XMLHttpRequest'
                            },
                            signal: progressAbort.signal
                        }
                    )
                    .then(response => consumeProgressStream(response, progressAbort.signal))
                    .catch(error => {
                        if (error?.name !== 'AbortError')
                            console.warn('Legend AI progress stream ended early.', error);
                    });

                const response =
                    await fetch(
                        modalElement.dataset.chatUrl,
                        {
                            method: 'POST',
                            credentials: 'same-origin',
                            headers: {
                                'Content-Type': 'application/json',
                                'Accept': 'application/json',
                                'RequestVerificationToken': token,
                                'X-Requested-With': 'XMLHttpRequest',
                                'X-Legend-Ai-Operation-Id': operationId
                            },
                            body: JSON.stringify({
                                mode: conversation.mode,
                                conversationId: conversation.id,
                                founderCommandConfirmed:
                                    founderCommandConfirmed?.checked === true,
                                messages: conversation.messages
                            })
                        }
                    );

                const rawResponse = await response.text();
                let result = null;

                try {
                    result = rawResponse ? JSON.parse(rawResponse) : null;
                } catch {
                    result = null;
                } finally {
                    progressAbort.abort();
                    try { await progressPromise; } catch { }
                }

                if (
                    !response.ok ||
                    !result?.succeeded
                ) {
                    throw new Error(
                        result?.error ||
                        (
                            rawResponse &&
                            rawResponse.length < 600
                                ? rawResponse
                                : 'Legend® Ai could not complete that response.'
                        )
                    );
                }

                conversation.messages.push({
                    role: 'assistant',
                    content:
                        result.message
                });

                if (
                    conversation.messages.length >
                    MAX_MESSAGES
                ) {
                    conversation.messages.splice(
                        0,
                        conversation.messages.length -
                        MAX_MESSAGES
                    );
                }

                conversation.updatedUtc =
                    new Date().toISOString();

                saveState();
                renderAll({ forceBottom: true });
                setBusy(false, '');
                focusComposer();
            } catch (error) {
                setBusy(
                    false,
                    error instanceof Error
                        ? error.message
                        : 'Legend® Ai could not complete that response.'
                );

                focusComposer();
            } finally {
                if (founderCommandConfirmed) {
                    founderCommandConfirmed.checked = false;
                }
            }
        }
    );

    renderAll({ forceBottom: true });
})();
