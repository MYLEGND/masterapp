(() => {
    'use strict';

    const trigger = document.getElementById('legendFounderAiTrigger');
    const modalElement = document.getElementById('legendFounderAiModal');

    if (!trigger || !modalElement || typeof bootstrap === 'undefined') {
        return;
    }

    const UI_STORAGE_KEY = 'legendFounderAi.ui.v2';
    const DESIGN_TOKEN_URL = '/design/legend-design.tokens.json';
    const HISTORY_URL = modalElement.dataset.historyUrl;
    const HISTORY_REFRESH_MS = 20000;
    const MOBILE_QUERY = '(max-width: 820px)';

    const transcript = document.getElementById('legendFounderAiTranscript');
    const welcome = document.getElementById('legendFounderAiWelcome');
    const form = document.getElementById('legendFounderAiForm');
    const input = document.getElementById('legendFounderAiInput');
    const send = document.getElementById('legendFounderAiSend');
    const sendIcon = document.getElementById('legendFounderAiSendIcon');
    const newConversation = document.getElementById('legendFounderAiNew');
    const retryRequest = document.getElementById('legendFounderAiRetry');
    const history = document.getElementById('legendFounderAiHistory');
    const historyEmpty = document.getElementById('legendFounderAiHistoryEmpty');
    const conversationCount = document.getElementById('legendFounderAiConversationCount');
    const status = document.getElementById('legendFounderAiStatus');
    const conversationState = document.getElementById('legendFounderAiConversationState');
    const founderCommandConfirmed = document.getElementById(
        'legendFounderAiFounderCommandConfirmed'
    );
    const nativeOnly = document.getElementById(
        'legendFounderAiNativeOnly'
    );
    const externalAnsweringBlocked = document.getElementById('legendFounderAiExternalAnsweringBlocked');
    const sidebar = document.getElementById('legendFounderAiSidebar');
    const sidebarCollapse = document.getElementById('legendFounderAiSidebarCollapse');
    const sidebarScrim = document.getElementById('legendFounderAiSidebarScrim');
    const mobileMenu = document.getElementById('legendFounderAiMobileMenu');
    const modebar = document.getElementById('legendFounderAiModebar');
    const modebarHome = document.getElementById('legendFounderAiModebarHome');
    const mobileControls = document.getElementById('legendFounderAiMobileControls');

    const modeButtons = Array.from(
        modalElement.querySelectorAll('[data-legend-ai-mode]')
    );

    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

    const logoSource =
        modalElement.querySelector('.legend-founder-ai-logo')?.getAttribute('src') ||
        '/images/legend-ai/legendai.png';

    let busy = false;
    let activeRequest = null;
    let state = defaultState();
    let historyRequest = null;
    let historyTimer = null;
    let historySkip = 0;
    let historyHasMore = false;
    let accountGeneration = 0;
    let uiState = loadUiState();

    ensureActiveConversation();
    applySharedDesignTokens();
    syncControlPlacement();
    applyDesktopSidebarState();
    syncViewportHeight();
    setBusy(false);

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

    async function applySharedDesignTokens() {
        try {
            const response = await fetch(
                DESIGN_TOKEN_URL,
                {
                    credentials: 'same-origin',
                    cache: 'force-cache'
                }
            );

            if (!response.ok) {
                return;
            }

            const specification = await response.json();
            const colors = specification?.colors;
            if (!colors || typeof colors !== 'object') {
                return;
            }

            for (const [name, token] of Object.entries(colors)) {
                const value = token?.light;
                if (typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value)) {
                    modalElement.style.setProperty(
                        `--legend-design-${name}`,
                        value
                    );
                }
            }

            const sizes = specification?.sizes;
            if (sizes && typeof sizes === 'object') {
                for (const [name, value] of Object.entries(sizes)) {
                    if (typeof value === 'number' && Number.isFinite(value)) {
                        modalElement.style.setProperty(
                            `--legend-design-${name}`,
                            `${value}px`
                        );
                    }
                }
            }

            modalElement.dataset.designSource = 'legend-design.tokens.v1';
        } catch {
            // The component has safe CSS fallbacks, but production and iOS
            // normally consume the same versioned design-token resource.
        }
    }

    // The same controls have one DOM owner. On a compact viewport, move that
    // owner into the hamburger drawer instead of rendering a second mobile
    // mode/native-only implementation.
    function syncControlPlacement() {
        if (!modebar) {
            return;
        }

        const destination = isMobile()
            ? mobileControls
            : modebarHome;

        if (destination && modebar.parentElement !== destination) {
            destination.appendChild(modebar);
        }
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

    trigger.addEventListener('click', () => {
        syncViewportHeight();
        setSidebarOpen(false);
        syncControlPlacement();
        applyDesktopSidebarState();
        modal.show();
        renderAll({ forceBottom: true });
        focusComposer();
    });

    modalElement.addEventListener('shown.bs.modal', () => {
        syncViewportHeight();
        void refreshHistory().finally(scheduleHistoryRefresh);

        if (isMobile()) {
            input?.blur();
            sidebar?.setAttribute('aria-hidden', 'true');
        }
    });

    modalElement.addEventListener('hidden.bs.modal', () => {
        stopHistoryRefresh();
        setSidebarOpen(false);
        input?.blur();
    });

    document.addEventListener('visibilitychange', () => {
        if (document.hidden) stopHistoryRefresh();
        else void refreshHistory().finally(scheduleHistoryRefresh);
    });
    window.addEventListener('pagehide', clearAuthenticatedHistory);
    window.addEventListener('pageshow', () => void refreshHistory().finally(scheduleHistoryRefresh));

    window.addEventListener('resize', () => {
        syncViewportHeight();
        setSidebarOpen(false);
        syncControlPlacement();
        applyDesktopSidebarState();
    });

    window.visualViewport?.addEventListener(
        'resize',
        syncViewportHeight
    );

    function createId() {
        return window.crypto.randomUUID();
    }

    function newConversationRecord(
        mode = 'legend',
        nativeOnlyEnabled = false,
        externalAnsweringBlockedEnabled = false
    ) {
        const now = new Date().toISOString();

        return {
            id: createId(),
            mode,
            nativeOnly:
                mode === 'legend' &&
                nativeOnlyEnabled === true,
            externalAnsweringBlocked: mode === 'legend' && externalAnsweringBlockedEnabled === true,
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

    // Only canonical server history supplies prior turns. The legacy unscoped
    // localStorage transcript is deliberately neither read, imported nor deleted.
    function sortConversations() {
        state.conversations.sort((a, b) => (b.updatedUtc || '').localeCompare(a.updatedUtc || ''));
    }

    function stopHistoryRefresh() {
        window.clearTimeout(historyTimer);
        historyTimer = null;
        historyRequest?.abort();
        historyRequest = null;
    }

    function clearAuthenticatedHistory() {
        accountGeneration++;
        const request = activeRequest;
        activeRequest = null;
        request?.abort();
        stopHistoryRefresh();
        historySkip = 0;
        historyHasMore = false;
        state = defaultState();
        setBusy(false);
        if (input) input.value = '';
        renderAll();
    }

    async function readHistory(url, signal) {
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal });
        if (response.status === 401 || response.status === 403 || response.redirected) {
            clearAuthenticatedHistory();
            throw new Error('Conversation history is unavailable for this account.');
        }
        const result = await response.json();
        if (!response.ok || result.succeeded !== true) {
            throw new Error(result.errorMessage || 'Conversation history could not be loaded.');
        }
        return result;
    }

    function storedMessage(item) {
        if (!['Human', 'Assistant', 'Service'].includes(item.authorKind) ||
            (item.authorKind !== 'Human' && !item.responseProvenance)) {
            throw new Error('Conversation history could not be verified.');
        }
        return { ...item.responseProvenance, id: item.id, sentUtc: item.sentUtc,
            role: item.authorKind === 'Human' ? 'user' : item.authorKind === 'Assistant' ? 'assistant' : 'service',
            content: item.body, replyToMessageId: item.replyToMessageId };
    }

    async function loadConversationPage(conversation, signal, older = false, newer = false) {
        if (!conversation.persisted) return;
        const parameters = new URLSearchParams({ take: '60' });
        if (older && conversation.messages.length) {
            const first = conversation.messages[0];
            // Keep the original UTC wire string: Date conversion loses SQL ticks.
            parameters.set('beforeUtc', first.sentUtc);
            parameters.set('beforeMessageId', first.id);
        }
        const result = await readHistory(`${HISTORY_URL}/${conversation.id}?${parameters}`, signal);
        if (signal.aborted) return;
        const detail = result.conversation;
        const page = detail.messages.map(storedMessage);
        const pageIds = new Set(page.map(item => item.id));
        if (!older && !newer && conversation.messages.length && page.length &&
            !conversation.messages.some(item => pageIds.has(item.id))) {
            // Do not splice disconnected windows or strand their missing middle.
            conversation.hasNewer = true;
            return;
        }
        if (newer) conversation.messages = [];
        if (!older) conversation.hasNewer = false;
        conversation.messages = older ? [...page, ...conversation.messages.filter(item => !pageIds.has(item.id))]
            : [...conversation.messages.filter(item => !pageIds.has(item.id)), ...page];
        conversation.hasOlder = older || conversation.messages.length === page.length
            ? detail.hasOlderMessages === true : conversation.hasOlder;
        conversation.title = detail.subject || 'New conversation';
        conversation.updatedUtc = detail.lastMessageUtc;
        if (!older) conversation.lastMessageId = page.at(-1)?.id || null;
        const pending = conversation.pendingOperation;
        if (pending && page.some(item => item.id === pending.terminalId || (pending.userMessageId && item.replyToMessageId === pending.userMessageId))) conversation.pendingOperation = null;
    }

    async function refreshHistory({ more = false, older = false, newer = false } = {}) {
        if (busy || historyRequest || document.hidden || !modalElement.classList.contains('show')) return;
        const request = new AbortController();
        historyRequest = request;
        const active = activeConversation();
        try {
            if (!older) {
                const skip = more ? historySkip : 0;
                const result = await readHistory(`${HISTORY_URL}?take=50&skip=${skip}`, request.signal);
                if (request.signal.aborted) return;
                for (const row of result.conversations) {
                    let conversation = state.conversations.find(item => item.id === row.id);
                    if (!conversation) {
                        // History carries transcript provenance, not a new provider restriction.
                        // Keep existing local choices; newly discovered threads use the same defaults as native.
                        conversation = { ...newConversationRecord(), id: row.id,
                            persisted: true, messages: [], lastMessageId: null };
                        state.conversations.push(conversation);
                    }
                    conversation.persisted = true;
                    conversation.title = row.subject || 'New conversation';
                    conversation.updatedUtc = row.lastMessageUtc;
                }
                historySkip = more ? skip + result.conversations.length : Math.max(historySkip, result.conversations.length);
                historyHasMore = result.conversations.length === 50;
            }
            await loadConversationPage(active, request.signal, older, newer);
            if (!request.signal.aborted && state.activeConversationId === active.id) renderAll();
        } catch (error) {
            if (!request.signal.aborted && status) status.textContent = error.message;
        } finally {
            if (historyRequest === request) historyRequest = null;
        }
    }

    function scheduleHistoryRefresh() {
        window.clearTimeout(historyTimer);
        if (document.hidden || !modalElement.classList.contains('show')) return;
        historyTimer = window.setTimeout(async () => {
            await refreshHistory();
            scheduleHistoryRefresh();
        }, HISTORY_REFRESH_MS);
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
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();

        sortConversations();

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
        const conversation = newConversationRecord(
            requestedMode,
            false
        );
        state.conversations.unshift(conversation);
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();
        sortConversations();
        setSidebarOpen(false);
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
            newConversationRecord(
                current.mode,
                current.nativeOnly === true,
                current.externalAnsweringBlocked === true
            );

        state.conversations.unshift(conversation);
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();

        sortConversations();
        setSidebarOpen(false);
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

        stopHistoryRefresh();
        state.activeConversationId = id;
        void refreshHistory().finally(scheduleHistoryRefresh);

        sortConversations();
        setSidebarOpen(false);
        renderAll({ forceBottom: true });
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

        if (nativeOnly) {
            nativeOnly.checked =
                conversation.nativeOnly === true;
            nativeOnly.disabled =
                busy || conversation.mode !== 'legend';
            nativeOnly.closest(
                '.legend-founder-ai-native-only'
            )?.classList.toggle(
                'is-active',
                conversation.nativeOnly === true
            );
        }

        if (externalAnsweringBlocked) {
            externalAnsweringBlocked.checked = conversation.externalAnsweringBlocked === true;
            externalAnsweringBlocked.disabled = busy || conversation.mode !== 'legend' || conversation.nativeOnly === true;
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

            title.setAttribute('data-user-content', '');
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
        if (historyHasMore) {
            const more = document.createElement('button');
            more.type = 'button';
            more.className = 'legend-founder-ai-history-item';
            more.textContent = 'Load more conversations';
            more.addEventListener('click', () => void refreshHistory({ more: true }));
            history.appendChild(more);
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
        if (conversation.hasNewer) {
            const newer = document.createElement('button');
            newer.type = 'button';
            newer.className = 'btn btn-sm btn-outline-secondary';
            newer.textContent = 'Load newer messages';
            newer.addEventListener('click', () => void refreshHistory({ newer: true }));
            transcript.appendChild(newer);
        }
        if (conversation.hasOlder) {
            const older = document.createElement('button');
            older.type = 'button';
            older.className = 'btn btn-sm btn-outline-secondary';
            older.textContent = 'Load earlier messages';
            older.addEventListener('click', () => void refreshHistory({ older: true }));
            transcript.appendChild(older);
        }
        if (retryRequest) retryRequest.hidden = busy || !conversation.pendingOperation;

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
                    false,
                    message.responseAuthority,
                    message.stage,
                    message
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
        scroll = true,
        responseAuthority = null,
        stage = null,
        metadata = null
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

            logo.className =
                'legend-founder-ai-logo-image';
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

        // Conversation content already carries the server's response language.
        // The app-copy catalog may translate the authority label, never user
        // text or a completed/streamed model response that happens to match it.
        const body = document.createElement('span');
        if (role !== 'service') body.setAttribute('data-user-content', '');
        body.textContent = content;
        bubble.appendChild(body);

        if (role !== 'user') {
            const authority =
                document.createElement('div');

            authority.className =
                'legend-founder-ai-response-authority';

            const hasNamedAuthority =
                responseAuthority === 'LegendAi' ||
                responseAuthority === 'HostedFoundation' ||
                responseAuthority === 'LocalFoundation' ||
                responseAuthority === 'GovernedResearch' ||
                responseAuthority === 'OpenAITeacher' ||
                responseAuthority === 'SystemDiagnostic';

            if (responseAuthority === 'LegendAi') {
                authority.classList.add('is-native');
                authority.textContent =
                    'Legend® Ai';
            } else if (responseAuthority === 'LocalFoundation') {
                authority.classList.add('is-native');
                authority.textContent = 'LEGEND-controlled model';
            } else if (responseAuthority === 'HostedFoundation') {
                authority.classList.add('is-provider');
                authority.textContent = 'LEGEND · hosted foundation';
            } else if (
                responseAuthority === 'GovernedResearch'
            ) {
                authority.classList.add('is-native');
                authority.textContent =
                    'LEGEND governed research';
            } else if (
                responseAuthority === 'OpenAITeacher'
            ) {
                authority.classList.add('is-provider');
                authority.textContent =
                    'OpenAI';
            } else if (
                responseAuthority === 'SystemDiagnostic'
            ) {
                authority.textContent =
                    'System diagnostic';
            }

            if (hasNamedAuthority) {
                bubble.appendChild(authority);
            }
        }

        if (role !== 'user' && metadata) {
            const labels = [];
            if (metadata.reason === 'provider_output_incomplete') labels.push('Partial answer: output limit reached');
            else if (metadata.stage === 'response_partial') labels.push('Partial answer');
            const escalationLabels = {
                Restricted: 'Teacher material restricted from training',
                InsufficientEvidence: 'Teacher material lacks sufficient evidence'
            };
            if (Object.hasOwn(escalationLabels, metadata.escalationDisposition)) labels.push(escalationLabels[metadata.escalationDisposition]);
            const researchLabels = {
                Conclusion: 'Research completed',
                InsufficientEvidence: 'Research found insufficient evidence',
                UnresolvedConflict: 'Research found conflicting evidence',
                Failure: 'Research could not be completed'
            };
            const learningLabels = {
                Submitted: 'Teaching submitted for review',
                AwaitingCritic: 'Teaching submitted for review',
                InsufficientEvidence: 'Teaching needs more evidence'
            };
            if (Object.hasOwn(researchLabels, metadata.researchState)) labels.push(researchLabels[metadata.researchState]);
            if (metadata.escalationUsed === true) labels.push('Escalation used');
            if (Object.hasOwn(learningLabels, metadata.learningState)) labels.push(learningLabels[metadata.learningState]);
            if (metadata.modelAssistanceState === 'Applied' && typeof metadata.modelTrainingRunId === 'string' && metadata.modelTrainingRunId.trim()) labels.push('Promoted model applied');
            if (labels.length) {
                const status = document.createElement('div');
                status.className = 'legend-founder-ai-response-authority';
                for (const [index, label] of labels.entries()) {
                    if (index) status.appendChild(document.createTextNode(' · '));
                    const part = document.createElement('span');
                    part.textContent = label;
                    status.appendChild(part);
                }
                bubble.appendChild(status);
            }
        }

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

    }

    function setBusy(
        value,
        message = ''
    ) {
        busy = value;

        modalElement.classList.toggle(
            'is-responding',
            value
        );

        if (send) {
            // While a response is running this same control becomes Stop.
            // The composer stays editable so the next request can be prepared
            // without opening a parallel conversation or endpoint.
            send.disabled = !value && !input?.value.trim();
            send.setAttribute(
                'aria-label',
                value
                    ? 'Stop generating'
                    : 'Send message'
            );
        }

        if (sendIcon) {
            sendIcon.textContent = value ? '■' : '↑';
        }

        if (newConversation) {
            newConversation.disabled = value;
        }

        if (retryRequest) {
            retryRequest.disabled = value;
            retryRequest.hidden = !activeConversation().pendingOperation || value;
        }

        for (const button of modeButtons) {
            button.disabled = value;
        }

        if (nativeOnly) {
            nativeOnly.disabled =
                value ||
                activeConversation().mode !== 'legend';
        }

        if (externalAnsweringBlocked) {
            externalAnsweringBlocked.disabled = value || activeConversation().mode !== 'legend' || activeConversation().nativeOnly === true;
        }
        if (status) {
            status.textContent = message;
        }
    }

    function abortActiveRequest() {
        if (!busy || !activeRequest) {
            return;
        }

        status && (status.textContent =
            'Stopping the current response. Your next message remains in the composer.');
        activeRequest.abort();
    }

    function applyOperationalProgress(payload) {
        const update = payload?.progress;
        if (!status || !update?.message) return;
        status.textContent =
            payload.type === 'heartbeat' && Number.isFinite(payload.elapsedSeconds)
                ? `${update.message} · ${payload.elapsedSeconds}s`
                : update.message;
    }

    function structuredFailureMessage(result, fallback = '') {
        if (!result || typeof result !== 'object') {
            return fallback || 'Legend® Ai could not complete that response.';
        }

        // Preserve the authoritative summary as one exact catalog source.
        // Typed diagnostic fields remain on the response contract; app-copy
        // localization cannot match a summary concatenated with raw codes.
        return typeof result.error === 'string' && result.error.trim()
            ? result.error.trim()
            : fallback || 'Legend® Ai could not complete that response.';
    }

    async function consumeChatResultStream(response, signal) {
        if (!response.ok || !response.body) {
            const raw = await response.text().catch(() => '');
            let result = null;
            try { result = raw ? JSON.parse(raw) : null; } catch { }
            if (result && typeof result.succeeded === 'boolean') return result;
            throw new Error(structuredFailureMessage(result));
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let result = null;

        const consumeLine = line => {
            if (signal?.aborted) return;
            const trimmed = line.trim();
            if (!trimmed) return;

            let payload = null;
            try { payload = JSON.parse(trimmed); } catch { return; }

            if (payload?.type === 'progress') {
                applyOperationalProgress(payload);
                return;
            }

            if (payload?.type === 'heartbeat') {
                if (status && Number.isFinite(payload.elapsedSeconds)) {
                    status.textContent =
                        `Continuing the governed request · ${payload.elapsedSeconds}s`;
                }
                return;
            }

            if (payload?.type === 'result') {
                result = payload.result || null;
            }
        };

        try {
            while (!signal?.aborted) {
                const chunk = await reader.read();
                if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
                buffer += decoder.decode(
                    chunk.value || new Uint8Array(),
                    { stream: !chunk.done });

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

        if (!result) {
            throw new Error(
                'Legend® Ai ended the response stream before returning a structured result.');
        }

        return result;
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
        () => {
            resizeInput();

            if (!busy && send) {
                send.disabled = !input.value.trim();
            }
        }
    );

    input?.addEventListener(
        'focus',
        () => {
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

                if (busy) {
                    abortActiveRequest();
                } else {
                    form?.requestSubmit();
                }
            }
        }
    );

    sidebarCollapse?.addEventListener(
        'click',
        toggleDesktopSidebar
    );

    newConversation?.addEventListener(
        'click',
        startNewConversation
    );

    retryRequest?.addEventListener('click', () => {
        const conversation = activeConversation();
        if (!busy && conversation.pendingOperation) void executeConversationRequest(conversation, conversation.pendingOperation);
    });

    mobileMenu?.addEventListener(
        'click',
        () => {
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

    externalAnsweringBlocked?.addEventListener('change', () => {
        const current = activeConversation();
        if (busy || current.mode !== 'legend' || current.nativeOnly === true) {
            renderModes();
            return;
        }
        const conversation = newConversationRecord('legend', false, externalAnsweringBlocked.checked);
        state.conversations.unshift(conversation);
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();
        sortConversations();
        renderAll({ forceBottom: true });
    });

    nativeOnly?.addEventListener(
        'change',
        () => {
            if (busy) {
                renderModes();
                return;
            }

            const current = activeConversation();
            if (current.mode !== 'legend') {
                renderModes();
                return;
            }

            // A native-only boundary starts a clean thread so provider-backed
            // answers from an earlier conversation cannot contaminate the
            // direct LEGEND test context.
            const conversation = newConversationRecord(
                'legend',
                nativeOnly.checked,
                current.externalAnsweringBlocked === true
            );
            state.conversations.unshift(conversation);
            stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();
            sortConversations();
            setSidebarOpen(false);
            renderAll({ forceBottom: true });

            if (status) {
                status.textContent = conversation.nativeOnly
                    ? 'All external providers are blocked for this clean conversation.'
                    : 'Strict provider blocking is disabled for this clean conversation.';
            }

            focusComposer();
        }
    );

    async function executeConversationRequest(conversation, operation) {
        if (busy) return;
        const epoch = accountGeneration;
        stopHistoryRefresh();
        setBusy(true, 'Preparing a response…');
        const request = new AbortController();
        activeRequest = request;
        try {
            const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
            const response = await fetch(modalElement.dataset.chatUrl, {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/x-ndjson',
                    'RequestVerificationToken': token, 'X-Requested-With': 'XMLHttpRequest',
                    'X-Legend-Ai-Operation-Id': operation.id },
                // Retry uses this same immutable operation and effective request.
                body: operation.body, signal: request.signal
            });
            if (response.status === 401 || response.status === 403 || response.redirected) {
                clearAuthenticatedHistory();
                return;
            }
            const result = await consumeChatResultStream(response, request.signal);
            if (activeRequest !== request) return;
            if (result.failureKind === 'authorization') { clearAuthenticatedHistory(); return; }
            if (result.conversationId) {
                conversation.id = result.conversationId;
                if (result.userMessageId || result.messageId) conversation.persisted = true;
            }
            if (result.userMessageId) operation.userMessageId = result.userMessageId;
            if (result.messageId) {
                operation.terminalId = result.messageId;
                conversation.lastMessageId = result.messageId;
                conversation.updatedUtc = result.lastMessageUtc;
                conversation.messages = conversation.messages.filter(item => item.id !== result.messageId);
                conversation.messages.push({ ...result, id: result.messageId, sentUtc: result.lastMessageUtc,
                    role: result.succeeded ? 'assistant' : 'service', content: result.message || result.error || '' });
                conversation.pendingOperation = null;
            } else if (['FOUNDER_HISTORY_STALE', 'FOUNDER_HISTORY_REPLAY_MISMATCH', 'FOUNDER_HISTORY_FORBIDDEN', 'FOUNDER_HISTORY_CLOSED'].includes(result.reason)) {
                // A definite rejection is not an invitation to resubmit actions.
                conversation.pendingOperation = null;
            }
            if (status) status.textContent = result.succeeded
                ? (result.messageId ? '' : 'The response is missing its saved conversation receipt.')
                : structuredFailureMessage(result);
            renderAll({ forceBottom: true });
        } catch (error) {
            if (activeRequest === request && status) status.textContent = request.signal.aborted
                ? 'Response stopped. Check the saved outcome before sending again.'
                : error.message || 'The response could not be received. Check the saved outcome.';
        } finally {
            if (epoch !== accountGeneration) return;
            if (activeRequest === request) {
                activeRequest = null;
                setBusy(false, status?.textContent || '');
            }
            if (founderCommandConfirmed) founderCommandConfirmed.checked = false;
            await refreshHistory();
            scheduleHistoryRefresh();
        }
    }

    form?.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy) { abortActiveRequest(); return; }
        const text = input?.value.trim();
        if (!text) return;
        const conversation = activeConversation();
        if (conversation.hasNewer) {
            if (status) status.textContent = 'Load newer messages before sending a reply.';
            return;
        }
        if (conversation.pendingOperation) {
            if (status) status.textContent = 'Check the pending request before sending another message.';
            return;
        }
        // Only the current Human turn is submitted. Prior Assistant content,
        // permissions and canonical ordering are resolved by the server.
        const operation = { id: crypto.randomUUID(), body: JSON.stringify({
            mode: conversation.mode, nativeOnly: conversation.nativeOnly === true,
            externalAnsweringBlocked: conversation.externalAnsweringBlocked === true,
            sourceLanguageCode: null, conversationId: conversation.id,
            expectedLastMessageId: conversation.lastMessageId || null,
            founderCommandConfirmed: founderCommandConfirmed?.checked === true,
            messages: [{ role: 'user', content: text }]
        }) };
        conversation.pendingOperation = operation;
        input.value = '';
        resizeInput();
        await executeConversationRequest(conversation, operation);
        focusComposer();
    });

    send?.addEventListener(
        'click',
        event => {
            if (busy) {
                event.preventDefault();
                abortActiveRequest();
            }
        }
    );

    renderAll({ forceBottom: true });
})();
