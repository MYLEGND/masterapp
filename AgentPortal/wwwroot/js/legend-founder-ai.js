(() => {
    'use strict';

    const trigger = document.getElementById('legendFounderAiTrigger');
    const modalElement = document.getElementById('legendFounderAiModal');

    if (!trigger || !modalElement || typeof bootstrap === 'undefined') {
        return;
    }

    const DESIGN_TOKEN_URL = '/design/legend-design.tokens.json';
    const HISTORY_URL = modalElement.dataset.historyUrl;
    const HISTORY_REFRESH_MS = 20000;
    const MOBILE_QUERY = '(max-width: 900px)';

    const transcript = document.getElementById('legendFounderAiTranscript');
    const welcome = document.getElementById('legendFounderAiWelcome');
    const form = document.getElementById('legendFounderAiForm');
    const input = document.getElementById('legendFounderAiInput');
    const send = document.getElementById('legendFounderAiSend');
    const sendIcon = document.getElementById('legendFounderAiSendIcon');
    const composerMenu = document.getElementById('legendFounderAiComposerMenu');
    const mobileMenu = document.getElementById('legendFounderAiMobileMenu');
    const voiceFocus = document.getElementById('legendFounderAiVoiceFocus');
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
    const sidebarScrim = document.getElementById('legendFounderAiSidebarScrim');
    const modebar = document.getElementById('legendFounderAiModebar');
    const mobileControls = document.getElementById('legendFounderAiMobileControls');
    const mobileSheet = modalElement.querySelector('[data-legend-mobile-sheet]');
    const statusDetail = status?.querySelector('.legend-founder-ai-thinking-detail');
    const settingsPanel = document.getElementById('legendFounderAiSettingsPanel');
    const settingsBody = document.getElementById('legendFounderAiSettingsBody');
    const settingsTitle = document.getElementById('legendFounderAiSettingsTitle');
    const settingsClose = document.getElementById('legendFounderAiSettingsClose');
    const settingButtons = Array.from(modalElement.querySelectorAll('[data-legend-ai-setting]'));

    const businessSettings = document.getElementById('legendBusinessAssistantSettings');
    const businessWorkspace = document.getElementById('legendBusinessAssistantWorkspace');
    const businessProvider = document.getElementById('legendBusinessAssistantProvider');
    const businessReadiness = document.getElementById('legendBusinessAssistantReadiness');
    const businessStart = document.getElementById('legendBusinessAssistantStart');
    const businessFields = document.getElementById('legendBusinessAssistantConfigurationFields');
    const businessCredential = document.getElementById('legendBusinessAssistantCredential');
    const businessSave = document.getElementById('legendBusinessAssistantSave');
    const businessSaveStatus = document.getElementById('legendBusinessAssistantSaveStatus');
    const businessInspectModels = document.getElementById('legendBusinessAssistantInspectModels');
    const businessGenerateImage = document.getElementById('legendBusinessAssistantGenerateImage');
    const businessImageReferences = document.getElementById('legendBusinessAssistantImageReferences');
    const businessSettingFields = [
        ['projectId', 'API project ID', 'text'], ['organizationId', 'Organization ID (optional)', 'text'],
        ['model', 'Reviewed model ID', 'text'],
        ['reasoningEffort', 'Reviewed reasoning effort: low, medium, or high (default low)', 'text'],
        ['ownerBudgetAccountId', 'Workspace budget account ID', 'text'],
        ['fundingBudgetAccountId', 'Shared funding account ID', 'text'],
        ['monthlyCashAllocation', 'Monthly cash allocation', 'number'],
        ['monthlyServiceAllocation', 'Monthly service allocation', 'number'],
        ['maximumCashPerTurn', 'Maximum cash reserved per request', 'number'],
        ['maximumServicePerTurn', 'Maximum service reserved per request', 'number'],
        ['maximumOutputTokens', 'Maximum output tokens', 'number'],
        ['maximumInputCharacters', 'Maximum input characters', 'number'],
        ['costCeilingEvidenceSha256', 'Reviewed cost-bound evidence SHA-256', 'text'],
        ['costCeilingExpiresUtc', 'Cost-bound expiry (UTC)', 'text'],
        ['enabled', 'Enable requests after funding admission', 'checkbox']
        ,['imageModel', 'Reviewed image model ID (optional)', 'text'],
        ['imageQuality', 'Image quality: low, medium, or high', 'text'],
        ['imagesEnabled', 'Enable image requests within the same reviewed funding limits', 'checkbox'],
        ['transcriptionModel', 'Reviewed dictation model (gpt-transcribe)', 'text'],
        ['dictationEnabled', 'Enable dictation within reviewed funding limits (up to 60 seconds)', 'checkbox']
    ];
    function renderBusinessSettings(config) {
        if (!businessFields) return;
        businessFields.replaceChildren();
        businessCredential.value = '';
        for (const [name, label, type] of businessSettingFields) {
            const row = document.createElement('label');
            row.textContent = label;
            row.className = 'd-block mb-2';
            const input = document.createElement('input');
            input.type = type;
            input.dataset.assistantSetting = name;
            input.className = type === 'checkbox' ? 'form-check-input ms-2' : 'form-control';
            if (type === 'checkbox') input.checked = config?.[name] === true;
            else input.value = config?.[name] ?? '';
            if (type === 'number') { input.min = '0'; input.step = name.includes('Tokens') || name.includes('Characters') ? '1' : '0.000001'; }
            input.autocomplete = 'off';
            row.append(input); businessFields.append(row);
        }
    }
    let businessConfiguration = null;
    let businessSelectionGeneration = 0;

    async function refreshBusinessConfiguration() {
        cancelDictation();
        const generation = ++businessSelectionGeneration;
        businessConfiguration = null;
        businessStart.disabled = true;
        if (businessSave) businessSave.disabled = true;
        renderBusinessSettings(null);
        if (businessImageReferences) businessImageReferences.replaceChildren();
        if (businessGenerateImage) { businessGenerateImage.checked = false; businessGenerateImage.disabled = true; }
        if (!businessWorkspace.value) return;
        const query = new URLSearchParams({ workspace: businessWorkspace.value, provider: businessProvider.value });
        try {
            const result = await readHistory(`/founder/legend-ai/business/configuration?${query}`, undefined, false);
            if (generation !== businessSelectionGeneration) return;
            businessConfiguration = result;
            const config = result.configuration;
            renderBusinessSettings(config);
            businessSave.disabled = false;
            businessReadiness.textContent = `${businessProvider.selectedOptions[0].text}: ${result.readiness.replaceAll('_', ' ')}. ` +
                (config ? `Project: ${config.projectId}. Monthly cash allocation: ${config.monthlyCashAllocation}; service allocation: ${config.monthlyServiceAllocation}. ` : '') +
                `Provider credit balance: ${result.creditBalanceStatus}. Funding is checked before each request. No automatic fallback.`;
            businessStart.disabled = !config || !config.enabled || result.readiness !== 'funding_admission_required';
            businessGenerateImage.disabled = businessStart.disabled || !config?.imagesEnabled;
            const media = await readHistory(`/founder/legend-ai/business/media?${new URLSearchParams({ workspace: businessWorkspace.value })}`, undefined, false).catch(() => []);
            if (generation !== businessSelectionGeneration) return;
            for (const item of media) {
                const option = document.createElement('option'); option.value = item.id;
                option.textContent = `${item.contentType} · ${item.createdUtc} · ${item.id}`;
                businessImageReferences.append(option);
            }
        } catch (error) {
            if (generation === businessSelectionGeneration) businessReadiness.textContent = error.message || 'Connection status is unavailable.';
        }
    }
    businessSave?.addEventListener('click', async () => {
        if (!businessConfiguration || !businessWorkspace.value) return;
        const generation = businessSelectionGeneration;
        const configuration = { provider: businessProvider.value, serviceTier: 'default' };
        for (const [name, , type] of businessSettingFields) {
            const input = businessFields.querySelector(`[data-assistant-setting="${name}"]`);
            configuration[name] = type === 'checkbox' ? input.checked : type === 'number' ? Number(input.value) : input.value.trim();
        }
        configuration.organizationId ||= null;
        configuration.reasoningEffort ||= null;
        configuration.imageModel ||= null;
        configuration.imageQuality ||= null;
        configuration.transcriptionModel ||= null;
        businessSave.disabled = true;
        businessSaveStatus.textContent = 'Saving settings…';
        const credential = businessCredential.value || null;
        businessCredential.value = '';
        try {
            const response = await fetch('/founder/legend-ai/business/configuration', {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json',
                    'RequestVerificationToken': form.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
                body: JSON.stringify({ workspace: businessWorkspace.value, configuration,
                    expectedRevision: businessConfiguration.revision, credential })
            });
            if (generation !== businessSelectionGeneration) return;
            if (!response.ok) {
                const error = await response.json().catch(() => null);
                throw new Error(error?.error || 'Settings could not be saved. Reload the connection before trying again.');
            }
            businessSaveStatus.textContent = 'Settings saved. Provider readiness and funding are checked independently.';
            await refreshBusinessConfiguration();
        } catch (error) {
            if (generation === businessSelectionGeneration) businessSaveStatus.textContent = error.message;
        } finally {
            if (generation === businessSelectionGeneration) businessSave.disabled = false;
        }
    });
    businessInspectModels?.addEventListener('click', async () => {
        if (!businessConfiguration || !businessWorkspace.value) return;
        const generation = businessSelectionGeneration;
        businessInspectModels.disabled = true;
        businessSaveStatus.textContent = 'Checking configured model metadata…';
        try {
            const response = await fetch('/founder/legend-ai/business/model-access', {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json',
                    'RequestVerificationToken': form.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
                body: JSON.stringify({ workspace: businessWorkspace.value })
            });
            const result = await response.json();
            if (generation !== businessSelectionGeneration) return;
            businessSaveStatus.textContent = response.ok
                ? `${result.status.replaceAll('_', ' ')}. ${(result.models || []).map(item => `${item.model}: ${item.status.replaceAll('_', ' ')}`).join('; ')} ${result.limitation || ''}`
                : result.error || 'Model access could not be verified.';
        } catch {
            if (generation === businessSelectionGeneration) businessSaveStatus.textContent = 'Model access could not be verified. No generation was requested.';
        } finally { businessInspectModels.disabled = false; }
    });
    businessSettings?.addEventListener('toggle', async () => {
        if (!businessSettings.open || businessWorkspace.options.length > 1) return;
        try {
            const options = await readHistory('/founder/legend-ai/business/workspaces', undefined, false);
            for (const item of options) {
                const option = document.createElement('option');
                option.value = item.workspace;
                option.textContent = item.label;
                businessWorkspace.append(option);
            }
            if (!options.length) businessReadiness.textContent = 'No authorized business or agent workspace is available for this account.';
        } catch (error) { businessReadiness.textContent = error.message || 'Workspaces are unavailable.'; }
    });
    businessWorkspace?.addEventListener('change', refreshBusinessConfiguration);
    businessProvider?.addEventListener('change', refreshBusinessConfiguration);
    businessStart?.addEventListener('click', () => {
        if (busy || !businessConfiguration || businessStart.disabled) return;
        const conversation = newConversationRecord('business');
        conversation.assistantWorkspaceKey = businessConfiguration.workspaceKey;
        conversation.assistantProvider = businessProvider.value;
        conversation.assistantConfigurationRevision = businessConfiguration.revision;
        state.conversations.unshift(conversation);
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        historySkip = 0;
        scheduleHistoryRefresh();
        renderAll({ forceBottom: true });
        refreshHistory();
    });

    const modelSelection = document.getElementById('legendFounderAiModelSelection');
    const modelStatus = document.getElementById('legendFounderAiModelStatus');
    let chatModels = [];

    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

    const logoSource =
        modalElement.querySelector('.legend-founder-ai-logo')?.getAttribute('src') ||
        '/images/legend-ai/legendai.png';
    const userAvatarSource = (modalElement.dataset.userAvatarUrl || '').trim();
    const userInitials = (modalElement.dataset.userInitials || 'U').trim().slice(0, 2).toUpperCase();

    let busy = false;
    let activeRequest = null;
    let state = defaultState();
    let historyRequest = null;
    let historyTimer = null;
    let historySkip = 0;
    let historyHasMore = false;
    let accountGeneration = 0;

    ensureActiveConversation();
    applySharedDesignTokens();
    syncControlPlacement();
    syncViewportHeight();
    setBusy(false);

    function isMobile() {
        return window.matchMedia(MOBILE_QUERY).matches;
    }

    function syncViewportHeight() {
        const viewport = window.visualViewport;
        const height = viewport?.height || window.innerHeight;
        const top = viewport?.offsetTop || 0;
        const bottomInset = Math.max(
            0,
            window.innerHeight - height - top
        );

        modalElement.style.setProperty(
            '--legend-ai-viewport-height',
            `${Math.round(height)}px`
        );
        modalElement.style.setProperty(
            '--legend-ai-visual-bottom',
            `${Math.round(bottomInset)}px`
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

    // Conversation/provider controls have one canonical home in the sidebar.
    // The mobile hamburger exposes that same sidebar; never duplicate controls
    // across the chat canvas and navigation rail.
    function syncControlPlacement() {
        if (modebar && mobileControls && modebar.parentElement !== mobileControls) {
            mobileControls.appendChild(modebar);
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

    function syncMobileSheetPresentation(
        open = modalElement.classList.contains('show')
    ) {
        if (!mobileSheet) {
            return;
        }

        const shouldOpen = isMobile() && open;
        mobileSheet.classList.toggle('open', shouldOpen);

        if (shouldOpen) {
            window.LegendModal?.registerMobileSheet?.(mobileSheet);
            return;
        }

        mobileSheet.dataset.legendSheetSnap = 'full';
        mobileSheet.style.removeProperty('--legend-mobile-sheet-drag-y');
        mobileSheet.removeAttribute('data-legend-sheet-dragging');
    }

    function expandMobileSheetForComposer() {
        if (!isMobile() || !mobileSheet) {
            return;
        }

        mobileSheet.dataset.legendSheetSnap = 'full';
        mobileSheet.style.removeProperty('--legend-mobile-sheet-drag-y');
        mobileSheet.removeAttribute('data-legend-sheet-dragging');
    }

    function setSidebarOpen(open) {
        if (!isMobile()) {
            modalElement.classList.remove('is-sidebar-open');
            sidebar?.removeAttribute('aria-hidden');
            mobileMenu?.setAttribute('aria-expanded', 'false');
            return;
        }

        modalElement.classList.toggle('is-sidebar-open', open);
        sidebar?.setAttribute(
            'aria-hidden',
            open ? 'false' : 'true'
        );
        mobileMenu?.setAttribute(
            'aria-expanded',
            open ? 'true' : 'false'
        );
    }

    trigger.addEventListener('click', () => {
        syncViewportHeight();
        setSidebarOpen(false);
        syncControlPlacement();
        modal.show();
        renderAll({ forceBottom: true });
        focusComposer();
    });

    modalElement.addEventListener('pointerdown', event => {
        if (event.target === modalElement) {
            modal.hide();
        }
    });

    modalElement.addEventListener('shown.bs.modal', () => {
        syncViewportHeight();
        syncMobileSheetPresentation(true);
        void refreshHistory().finally(scheduleHistoryRefresh);

        if (isMobile()) {
            input?.blur();
            sidebar?.setAttribute('aria-hidden', 'true');
        }
    });

    modalElement.addEventListener('hidden.bs.modal', () => {
        stopHistoryRefresh();
        syncMobileSheetPresentation(false);
        setSidebarOpen(false);
        closeSettingsPanel();
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
        syncMobileSheetPresentation();
        setSidebarOpen(false);
        syncControlPlacement();
    });

    window.visualViewport?.addEventListener('scroll', syncViewportHeight);

    window.visualViewport?.addEventListener(
        'resize',
        () => {
            syncViewportHeight();
            resizeInput();
        }
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
        cancelDictation();
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

    async function readHistory(url, signal, requireHistoryResult = true) {
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal });
        if (response.status === 401 || response.status === 403 || response.redirected) {
            clearAuthenticatedHistory();
            throw new Error('Conversation history is unavailable for this account.');
        }
        const result = await response.json();
        if (!response.ok || (requireHistoryResult && result.succeeded !== true)) {
            throw new Error(result.errorMessage || result.error || 'Requested information could not be loaded.');
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
        if (conversation.assistantWorkspaceKey) parameters.set('assistantWorkspaceKey', conversation.assistantWorkspaceKey);
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
        if (!older && conversation.modelSelection == null) {
            const selected = [...page].reverse().find(item => item.modelSelection)?.modelSelection;
            if (selected) conversation.modelSelection = selected;
        }
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
                const result = await readHistory(`${HISTORY_URL}?take=50&skip=${skip}&assistantWorkspaceKey=${encodeURIComponent(active.assistantWorkspaceKey || '')}`, request.signal);
                if (request.signal.aborted) return;
                for (const row of result.conversations) {
                    let conversation = state.conversations.find(item => item.id === row.id);
                    if (!conversation) {
                        // History carries transcript provenance, not a new provider restriction.
                        // Keep existing local choices; newly discovered threads use the same defaults as native.
                        conversation = { ...newConversationRecord(active.assistantWorkspaceKey ? 'business' : 'legend'), id: row.id,
                            assistantWorkspaceKey: row.assistantWorkspaceKey, assistantProvider: row.assistantProvider,
                            assistantConfigurationRevision: row.assistantConfigurationRevision,
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
            if (!request.signal.aborted && status) updateThinkingStatus(error.message || '', false);
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

    function startNewConversation() {
        cancelDictation();
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

        conversation.assistantWorkspaceKey = current.assistantWorkspaceKey;
        conversation.assistantProvider = current.assistantProvider;
        conversation.assistantConfigurationRevision = current.assistantConfigurationRevision;

        state.conversations.unshift(conversation);
        stopHistoryRefresh();
        state.activeConversationId = conversation.id;
        scheduleHistoryRefresh();

        sortConversations();
        setSidebarOpen(false);
        renderAll({ forceBottom: true });

        if (status) {
            updateThinkingStatus('', false);
        }

        focusComposer();
    }

    function openConversation(id) {
        cancelDictation();
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
        if (dictation && dictation.conversationId !== state.activeConversationId) cancelDictation();
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

        if (modelSelection) {
            modelSelection.replaceChildren(new Option('Auto', 'auto'));
            if (conversation.mode === 'business') {
                const configured = businessConfiguration?.configuration?.model;
                modelSelection.replaceChildren(new Option(configured || 'Workspace model', 'auto'));
                modelSelection.disabled = true;
            } else {
                for (const model of chatModels) {
                    const option = new Option(model.label + (model.configured ? '' : ' · Not configured'), model.id);
                    option.disabled = !model.configured;
                    modelSelection.add(option);
                }
                const selected = conversation.modelSelection || (conversation.mode === 'teacher'
                    ? chatModels.find(item => item.provider === 'openai' && item.configured)?.id : 'auto');
                if (selected && !Array.from(modelSelection.options).some(option => option.value === selected)) {
                    const unavailable = new Option('Selected model unavailable', selected);
                    unavailable.disabled = true;
                    modelSelection.add(unavailable);
                }
                modelSelection.value = selected || 'auto';
                modelSelection.disabled = busy;
            }
        }

        if (nativeOnly) {
            nativeOnly.checked =
                conversation.nativeOnly === true;
            nativeOnly.disabled =
                busy || conversation.mode === 'business';
            nativeOnly.closest(
                '.legend-founder-ai-native-only'
            )?.classList.toggle(
                'is-active',
                conversation.nativeOnly === true
            );
        }

        if (externalAnsweringBlocked) {
            externalAnsweringBlocked.checked = conversation.externalAnsweringBlocked === true;
            externalAnsweringBlocked.disabled = busy || conversation.mode === 'business' || conversation.nativeOnly === true;
            externalAnsweringBlocked.closest(
                '.legend-founder-ai-native-only'
            )?.classList.toggle(
                'is-active',
                conversation.externalAnsweringBlocked === true
            );
        }

        if (input) {
            input.placeholder =
                `Message ${window.LEGEND_BRAND?.registeredTitle ?? ""} Ai…`;
        }
    }

    function renderHistory() {
        if (!history || !historyEmpty) {
            return;
        }

        history.replaceChildren();

        const conversations =
            state.conversations.filter(item => (item.assistantWorkspaceKey || null) === (activeConversation().assistantWorkspaceKey || null))
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
                    `${window.LEGEND_BRAND?.registeredTitle ?? ""} Ai`
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

        if (status && busy) {
            transcript.appendChild(status);
            status.hidden = false;
        }
    }

    function appendAssistantInline(parent, text, depth = 0) {
        if (depth > 8) {
            parent.appendChild(document.createTextNode(text));
            return;
        }
        let plain = '';
        const flush = () => {
            if (plain) parent.appendChild(document.createTextNode(plain));
            plain = '';
        };
        const closing = (token, from) => {
            for (let at = from; at < text.length; at++) {
                let escapes = 0;
                for (let i = at - 1; i >= 0 && text[i] === '\\'; i--) escapes++;
                // A bold delimiter inside literal inline code cannot close bold.
                if (token[0] !== '`' && text[at] === '`' && escapes % 2 === 0) {
                    const run = /^`+/.exec(text.slice(at))[0];
                    const end = closing(run, at + run.length);
                    if (end !== -1) { at = end + run.length - 1; continue; }
                    at += run.length - 1;
                    continue;
                }
                if (!text.startsWith(token, at)) continue;
                const exactCodeRun = token[0] !== '`' || (text[at - 1] !== '`' && text[at + token.length] !== '`');
                if (exactCodeRun && (token[0] === '`' || escapes % 2 === 0)) return at;
            }
            return -1;
        };
        for (let i = 0; i < text.length;) {
            if (text[i] === '\\' && /[\\`*{}\[\]()#+\-.!_>]/.test(text[i + 1] || '')) {
                plain += text[i + 1];
                i += 2;
                continue;
            }
            const reviewLink = /^\[([^\]\r\n]{1,120})\]\((\/founder\/legend-ai\/actions\/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)/.exec(text.slice(i));
            if (reviewLink) {
                flush();
                const link = document.createElement('a');
                link.href = reviewLink[2];
                link.textContent = reviewLink[1];
                link.target = '_blank';
                link.rel = 'noopener';
                parent.appendChild(link);
                i += reviewLink[0].length;
                continue;
            }
            let token = null;
            let tag = null;
            if (text[i] === '`') {
                token = /^`+/.exec(text.slice(i))[0];
                tag = 'code';
            } else if (text.startsWith('**', i) || text.startsWith('__', i)) {
                token = text.slice(i, i + 2);
                tag = 'strong';
            }
            if (token) {
                const end = closing(token, i + token.length);
                const value = end < 0 ? '' : text.slice(i + token.length, end);
                if (value && (tag === 'code' || (!/^\s|\s$/.test(value)))) {
                    flush();
                    const node = document.createElement(tag);
                    if (tag === 'code') node.textContent = value;
                    else appendAssistantInline(node, value, depth + 1);
                    parent.appendChild(node);
                    i = end + token.length;
                    continue;
                }
                plain += token;
                i += token.length;
                continue;
            }
            plain += text[i++];
        }
        flush();
    }

    function renderAssistantContent(body, content) {
        const text = String(content ?? '');
        body.className = 'legend-founder-ai-content';
        // Exact machine-readable JSON is content, not Markdown. Never interpret
        // formatting tokens inside its string values.
        if (/^\s*(?:[\[{"]|-?\d|true\b|false\b|null\b)/.test(text)) {
            try {
                JSON.parse(text);
                const pre = document.createElement('pre');
                const code = document.createElement('code');
                code.textContent = text;
                pre.appendChild(code);
                body.appendChild(pre);
                return;
            } catch { /* Ordinary prose or incomplete JSON stays literal below. */ }
        }
        const lines = text.split('\n');
        const fence = line => /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
        const heading = line => /^ {0,3}(#{1,6})[ \t]+(.+?)\s*$/.exec(line);
        const item = line => /^ {0,3}(?:([-+*])|([0-9]{1,9})[.)])[ \t]+(.*)$/.exec(line);
        for (let i = 0; i < lines.length;) {
            if (!lines[i].trim()) { i++; continue; }
            const block = fence(lines[i]);
            if (block) {
                const marker = block[1];
                const closing = new RegExp('^ {0,3}' + marker[0] + '{' + marker.length + ',}[ \\t\\r]*$');
                const codeLines = [];
                i++;
                while (i < lines.length && !closing.test(lines[i])) codeLines.push(lines[i++]);
                if (i < lines.length) i++;
                const pre = document.createElement('pre');
                const code = document.createElement('code');
                code.textContent = codeLines.join('\n');
                pre.appendChild(code);
                body.appendChild(pre);
                continue;
            }
            const title = heading(lines[i]);
            if (title) {
                const node = document.createElement('h' + title[1].length);
                appendAssistantInline(node, title[2]);
                body.appendChild(node);
                i++;
                continue;
            }
            const entry = item(lines[i]);
            if (entry) {
                const ordered = !!entry[2];
                const list = document.createElement(ordered ? 'ol' : 'ul');
                if (ordered) list.start = Number(entry[2]);
                while (i < lines.length) {
                    const next = item(lines[i]);
                    if (!next || !!next[2] !== ordered) break;
                    const row = document.createElement('li');
                    appendAssistantInline(row, next[3]);
                    list.appendChild(row);
                    i++;
                }
                body.appendChild(list);
                continue;
            }
            const paragraph = [lines[i++]];
            while (i < lines.length && lines[i].trim() && !fence(lines[i]) && !heading(lines[i]) && !item(lines[i])) {
                paragraph.push(lines[i++]);
            }
            const node = document.createElement('p');
            appendAssistantInline(node, paragraph.join('\n'));
            body.appendChild(node);
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
        if (metadata?.pending === true) {
            message.classList.add('is-pending');
            message.setAttribute('aria-label', 'Message sent; waiting for Legend AI');
        }

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
        const body = document.createElement(role === 'assistant' ? 'div' : 'span');
        if (role !== 'service') body.setAttribute('data-user-content', '');
        if (role === 'assistant') renderAssistantContent(body, content);
        else body.textContent = content;
        bubble.appendChild(body);
        if (role === 'assistant' && metadata?.stage === 'business_image_saved' && typeof metadata.reference === 'string') {
            const url = new URL(metadata.reference, window.location.origin);
            if (url.origin === window.location.origin && /^\/founder\/legend-ai\/business\/media\/[0-9a-f-]{36}$/i.test(url.pathname) &&
                /^(business|agent):[0-9a-f]{32}$/.test(url.searchParams.get('workspace') || '')) {
                const image = document.createElement('img');
                image.src = url.href; image.alt = 'Generated image saved privately to this workspace'; image.className = 'img-fluid';
                image.loading = 'lazy'; bubble.append(image);
                const download = document.createElement('a');
                url.searchParams.set('download', 'true'); download.href = url.href;
                download.textContent = 'Download image'; download.className = 'd-block'; bubble.append(download);
            }
        }

        // Provider/model provenance remains on the governed response contract for
        // diagnostics and audit, but ordinary chat presentation shows only the answer.
        message.appendChild(bubble);

        if (role === 'user') {
            const userMark = document.createElement('span');
            userMark.className = 'legend-founder-ai-user-mark';
            userMark.setAttribute('aria-hidden', 'true');

            const fallback = document.createElement('span');
            fallback.className = 'legend-founder-ai-user-initials';
            fallback.textContent = userInitials;
            userMark.appendChild(fallback);

            if (userAvatarSource) {
                const avatar = document.createElement('img');
                avatar.className = 'legend-founder-ai-user-avatar';
                avatar.hidden = true;
                avatar.src = userAvatarSource;
                avatar.alt = '';
                avatar.addEventListener('load', () => {
                    avatar.hidden = false;
                    fallback.hidden = true;
                }, { once: true });
                avatar.addEventListener('error', () => {
                    avatar.hidden = true;
                    fallback.hidden = false;
                }, { once: true });
                userMark.prepend(avatar);
            }

            message.appendChild(userMark);
        }

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

        if (modelSelection) modelSelection.disabled = value || activeConversation().mode === 'business';

        if (nativeOnly) {
            nativeOnly.disabled =
                value ||
                activeConversation().mode === 'business';
        }

        if (externalAnsweringBlocked) {
            externalAnsweringBlocked.disabled = value || activeConversation().mode === 'business' || activeConversation().nativeOnly === true;
        }
        updateThinkingStatus(message, value);
    }

    function updateThinkingStatus(message = '', visible = busy) {
        if (!status) return;
        if (visible && transcript && status.parentElement !== transcript) transcript.appendChild(status);
        status.hidden = !visible;
        status.dataset.message = message;
        if (statusDetail) statusDetail.textContent = message;
        if (visible) scrollToBottom();
    }

    function abortActiveRequest() {
        if (!busy || !activeRequest) {
            return;
        }

        updateThinkingStatus('Stopping the current response. Your next message remains in the composer.', true);
        activeRequest.abort();
    }

    function applyOperationalProgress(payload) {
        const update = payload?.progress;
        if (!update?.message) return;
        const detail = payload.type === 'heartbeat' && Number.isFinite(payload.elapsedSeconds)
            ? update.message + ' · ' + payload.elapsedSeconds + 's'
            : update.message;
        updateThinkingStatus(detail, true);
    }

    function structuredFailureMessage(result, fallback = '') {
        if (!result || typeof result !== 'object') {
            return fallback || `${window.LEGEND_BRAND?.registeredTitle ?? ""} Ai could not complete that response.`;
        }

        // Preserve the authoritative summary as one exact catalog source.
        // Typed diagnostic fields remain on the response contract; app-copy
        // localization cannot match a summary concatenated with raw codes.
        return typeof result.error === 'string' && result.error.trim()
            ? result.error.trim()
            : fallback || `${window.LEGEND_BRAND?.registeredTitle ?? ""} Ai could not complete that response.`;
    }

    async function consumeChatResultStream(response, signal, onAnswerDelta) {
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
                if (payload.progress?.stage === 'answer_delta' && typeof payload.progress.answerDelta === 'string') {
                    onAnswerDelta?.(payload.progress.answerDelta);
                    return;
                }
                applyOperationalProgress(payload);
                return;
            }

            if (payload?.type === 'heartbeat') {
                if (status && Number.isFinite(payload.elapsedSeconds)) {
                    updateThinkingStatus('Continuing the governed request · ' + payload.elapsedSeconds + 's', true);
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
                `${window.LEGEND_BRAND?.registeredTitle ?? ""} Ai ended the response stream before returning a structured result.`);
        }

        return result;
    }

    function resizeInput() {
        if (!input) {
            return;
        }

        const viewportHeight =
            window.visualViewport?.height ||
            window.innerHeight;
        const maximumHeight = isMobile()
            ? Math.max(
                72,
                Math.min(
                    112,
                    Math.round(viewportHeight * 0.20)
                )
            )
            : 180;

        input.style.height = 'auto';

        const requiredHeight = input.scrollHeight;
        input.style.height =
            `${Math.min(
                requiredHeight,
                maximumHeight
            )}px`;
        input.style.overflowY =
            requiredHeight > maximumHeight
                ? 'auto'
                : 'hidden';
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
            expandMobileSheetForComposer();
            window.setTimeout(
                () => {
                    syncViewportHeight();
                    resizeInput();
                },
                40
            );
        }
    );

    input?.addEventListener(
        'keydown',
        event => {
            if (
                event.key === 'Enter' &&
                !event.shiftKey &&
                !isMobile()
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
        () => setSidebarOpen(!modalElement.classList.contains('is-sidebar-open'))
    );

    composerMenu?.addEventListener(
        'click',
        () => {
            if (isMobile()) {
                setSidebarOpen(true);
                return;
            }

            sidebar?.scrollTo({ top: 0, behavior: 'smooth' });
            newConversation?.focus({ preventScroll: true });
        }
    );

    const voiceStatus = document.getElementById('legendFounderAiVoiceStatus');
    const voiceCancel = document.getElementById('legendFounderAiVoiceCancel');
    let dictation = null;
    function stopMicrophone(session) {
        clearTimeout(session.timer);
        session.stream?.getTracks().forEach(track => track.stop());
        if (session.node) { session.node.port.onmessage = null; session.node.disconnect(); }
        session.source?.disconnect();
        void session.context?.close().catch(() => {});
    }
    function cancelDictation() {
        if (!dictation) return;
        const session = dictation; dictation = null;
        session.abort.abort(); stopMicrophone(session);
        session.chunks.length = 0;
        voiceCancel.hidden = true; voiceFocus.setAttribute('aria-pressed', 'false');
        voiceFocus.setAttribute('aria-label', 'Record dictation');
        voiceStatus.textContent = session.phase === 'transcribing'
            ? 'Transcription cancelled locally. OpenAI may already have received the audio; its cost reservation remains pending. Typed text is unchanged.'
            : 'Recording discarded before upload. Typed text is unchanged.';
    }
    async function finishDictation() {
        const session = dictation;
        if (!session || session.phase !== 'recording') return;
        session.phase = 'transcribing'; stopMicrophone(session);
        voiceFocus.setAttribute('aria-pressed', 'false');
        voiceFocus.setAttribute('aria-label', 'Transcription in progress');
        voiceStatus.textContent = 'Transcribing with the selected OpenAI project… No message has been sent.';
        try {
            const { encodeDictationWave, appendDictation } = await import('/js/legend-ai-dictation.mjs');
            if (dictation !== session || session.accountGeneration !== accountGeneration ||
                activeConversation().id !== session.conversationId) return;
            const wave = encodeDictationWave(session.chunks, session.sampleRate);
            session.chunks.length = 0;
            let binary = '';
            for (let i = 0; i < wave.length; i += 8192) binary += String.fromCharCode(...wave.subarray(i, i + 8192));
            const response = await fetch('/founder/legend-ai/business/dictation', {
                method: 'POST', credentials: 'same-origin', signal: session.abort.signal,
                headers: { 'Content-Type': 'application/json',
                    'RequestVerificationToken': form.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
                body: JSON.stringify({ workspace: session.workspace, configurationRevision: session.revision,
                    operationId: session.operationId, audioBase64: btoa(binary) })
            });
            if (response.status === 401 || response.status === 403 || response.redirected) { clearAuthenticatedHistory(); return; }
            const result = await response.json();
            if (!response.ok) throw new Error(result.error || 'Transcription failed. Check dictation configuration and funding.');
            if (dictation !== session || session.accountGeneration !== accountGeneration || activeConversation().id !== session.conversationId ||
                businessConfiguration?.revision !== session.revision) return;
            input.value = appendDictation(input.value, result.transcript, input.maxLength);
            input.dispatchEvent(new Event('input', { bubbles: true }));
            voiceStatus.textContent = 'Transcript added. Review it, then press Send. Raw audio was not saved by LEGEND.';
        } catch (error) {
            if (dictation === session) voiceStatus.textContent = error.message || 'Dictation failed. Typed text is unchanged.';
        } finally {
            if (dictation === session) {
                dictation = null; voiceCancel.hidden = true;
                voiceFocus.setAttribute('aria-label', 'Record dictation');
            }
        }
    }
    voiceFocus?.addEventListener('click', async () => {
        if (dictation) { if (dictation.phase === 'recording') await finishDictation(); return; }
        const conversation = activeConversation();
        if (busy || conversation.pendingOperation) { voiceStatus.textContent = 'Finish the pending request before recording.'; return; }
        if (conversation.mode !== 'business' || conversation.nativeOnly || conversation.externalAnsweringBlocked ||
            businessConfiguration?.workspaceKey !== conversation.assistantWorkspaceKey ||
            businessConfiguration?.revision !== conversation.assistantConfigurationRevision ||
            businessConfiguration?.configuration?.dictationEnabled !== true || conversation.assistantProvider !== 'openai') {
            voiceStatus.textContent = 'Select a business conversation with reviewed OpenAI dictation and funding enabled in its settings. You can continue typing.'; return;
        }
        if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia || !window.AudioWorkletNode) {
            voiceStatus.textContent = 'Microphone recording is unavailable here. Use an HTTPS browser with microphone access, or type your message.'; return;
        }
        const session = { phase: 'permission', chunks: [], samples: 0, abort: new AbortController(),
            workspace: conversation.assistantWorkspaceKey, revision: conversation.assistantConfigurationRevision,
            conversationId: conversation.id, accountGeneration, operationId: crypto.randomUUID() };
        dictation = session; voiceCancel.hidden = false;
        voiceStatus.textContent = 'Allow microphone access to record. Audio goes to your selected OpenAI project when you stop or reach 60 seconds. Cancel before transcription discards the local recording.';
        try {
            const AudioContextType = window.AudioContext || window.webkitAudioContext;
            session.context = new AudioContextType();
            await session.context.resume();
            const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1 }, video: false });
            if (dictation !== session) { stream.getTracks().forEach(track => track.stop()); return; }
            session.stream = stream;
            await session.context.audioWorklet.addModule('/js/legend-ai-microphone-worklet.js');
            if (dictation !== session) return;
            session.sampleRate = session.context.sampleRate;
            session.source = session.context.createMediaStreamSource(stream);
            session.node = new AudioWorkletNode(session.context, 'legend-microphone-capture');
            session.node.port.onmessage = event => {
                if (dictation !== session || session.phase !== 'recording') return;
                const remaining = session.sampleRate * 60 - session.samples;
                const chunk = event.data.subarray(0, remaining);
                session.chunks.push(chunk); session.samples += chunk.length;
                if (session.samples >= session.sampleRate * 60) void finishDictation();
            };
            session.source.connect(session.node); session.node.connect(session.context.destination);
            session.phase = 'recording';
            voiceFocus.setAttribute('aria-pressed', 'true'); voiceFocus.setAttribute('aria-label', 'Stop recording and transcribe');
            voiceStatus.textContent = 'Recording microphone · press Stop to transcribe, or Cancel to discard (60 seconds maximum).';
            session.timer = setTimeout(() => void finishDictation(), 60000);
            stream.getAudioTracks()[0].addEventListener('ended', () => { if (dictation === session && session.phase === 'recording') cancelDictation(); });
        } catch (error) {
            if (dictation === session) {
                cancelDictation();
                voiceStatus.textContent = error.name === 'NotAllowedError'
                    ? 'Microphone access was denied. Allow it in browser settings and try again, or type your message.'
                    : 'Microphone could not start. Check that a microphone is connected and available, or type your message.';
            }
        }
    });
    voiceCancel?.addEventListener('click', cancelDictation);
    modalElement.addEventListener('hidden.bs.modal', cancelDictation);
    window.addEventListener('pagehide', cancelDictation);
    document.addEventListener('visibilitychange', () => { if (document.hidden) cancelDictation(); });

    transcript?.addEventListener(
        'click',
        event => {
            const suggestion =
                event.target.closest?.('[data-legend-ai-suggestion]');

            if (!suggestion || busy) {
                return;
            }

            const prompt =
                suggestion.dataset.legendAiSuggestion?.trim();

            if (!prompt || !input) {
                return;
            }

            input.value = prompt;
            resizeInput();
            if (send) send.disabled = false;
            form?.requestSubmit();
        }
    );

    sidebarScrim?.addEventListener(
        'click',
        () =>
            setSidebarOpen(false)
    );

    function closeSettingsPanel() {
        if (settingsPanel) settingsPanel.hidden = true;
    }

    function settingsAction(label, detail, action) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'legend-founder-ai-settings-action';
        const copy = document.createElement('span');
        const strong = document.createElement('strong');
        const small = document.createElement('small');
        strong.textContent = label;
        small.textContent = detail;
        copy.append(strong, small);
        button.appendChild(copy);
        button.addEventListener('click', action);
        return button;
    }

    function openSettingsPanel(kind) {
        if (!settingsPanel || !settingsBody || !settingsTitle) return;
        settingsBody.replaceChildren();
        const current = activeConversation();

        if (kind === 'model') {
            settingsTitle.textContent = 'Model';
            settingsBody.append(
                settingsAction('Choose model', 'Choose Auto or a configured model in this conversation’s header.', () => { closeSettingsPanel(); modelSelection?.focus(); })
            );
        } else if (kind === 'tools') {
            settingsTitle.textContent = 'Tools & data';
            settingsBody.append(
                settingsAction(
                    current.nativeOnly ? 'Allow governed providers' : 'Block all external providers',
                    current.nativeOnly ? 'Start a clean LEGEND conversation with governed provider access restored.' : 'Start a clean native-only conversation with external answering, research, and translation disabled.',
                    () => {
                        closeSettingsPanel();
                        if (!nativeOnly || current.mode === 'business') return;
                        nativeOnly.checked = !current.nativeOnly;
                        nativeOnly.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                ),
                settingsAction(
                    current.externalAnsweringBlocked ? 'Allow external answering' : 'Block external answering',
                    'Keep authorized research and translation available while controlling external answer generation.',
                    () => {
                        closeSettingsPanel();
                        if (!externalAnsweringBlocked || current.mode === 'business' || current.nativeOnly) return;
                        externalAnsweringBlocked.checked = !current.externalAnsweringBlocked;
                        externalAnsweringBlocked.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                )
            );
        } else if (kind === 'behavior') {
            settingsTitle.textContent = 'Behavior';
            settingsBody.append(
                settingsAction(
                    founderCommandConfirmed?.checked ? 'Disable governed action permission' : 'Allow governed action',
                    'Authorize only this request’s bounded governed action. Release approval remains separate.',
                    () => {
                        if (!founderCommandConfirmed) return;
                        founderCommandConfirmed.checked = !founderCommandConfirmed.checked;
                        closeSettingsPanel();
                    }
                )
            );
        } else {
            settingsTitle.textContent = 'History';
            settingsBody.append(
                settingsAction('New conversation', 'Start a clean conversation while preserving prior account history.', () => {
                    closeSettingsPanel();
                    startNewConversation();
                })
            );
        }

        settingsPanel.hidden = false;
        settingsClose?.focus({ preventScroll: true });
    }

    settingButtons.forEach(button => {
        button.addEventListener('click', () => openSettingsPanel(button.dataset.legendAiSetting || 'history'));
    });
    settingsClose?.addEventListener('click', closeSettingsPanel);

    modelSelection?.addEventListener('change', () => {
        if (busy || activeConversation().mode === 'business') return;
        activeConversation().modelSelection = modelSelection.value;
        // A model is a per-turn selection, never a new identity or transcript.
        renderModes();
    });
    async function loadChatModels() {
        try {
            const response = await fetch(modalElement.dataset.modelsUrl, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) throw new Error('model_catalog_unavailable');
            const catalog = await response.json();
            chatModels = Array.isArray(catalog.models) ? catalog.models : [];
            if (modelStatus) modelStatus.textContent = 'Configured models. Provider access and funding are checked separately.';
            renderModes();
        } catch {
            if (modelStatus) modelStatus.textContent = 'Model list unavailable. Retry opening LEGEND AI.';
        }
    }
    loadChatModels();

    externalAnsweringBlocked?.addEventListener('change', () => {
        const current = activeConversation();
        if (busy || current.mode === 'business' || current.nativeOnly === true) {
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
            if (current.mode === 'business') {
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
                updateThinkingStatus(
                    conversation.nativeOnly
                        ? 'All external providers are blocked for this clean conversation.'
                        : 'Strict provider blocking is disabled for this clean conversation.',
                    false
                );
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
        let provisionalAnswer = null;
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
            const result = await consumeChatResultStream(response, request.signal, delta => {
                if (activeRequest !== request || epoch !== accountGeneration || !transcript) return;
                if (!provisionalAnswer) {
                    provisionalAnswer = document.createElement('article');
                    provisionalAnswer.className = 'legend-founder-ai-message is-assistant';
                    provisionalAnswer.setAttribute('aria-label', 'Response in progress; not yet saved');
                    transcript.appendChild(provisionalAnswer);
                }
                // Provider text is untrusted. Render provisional text without HTML.
                provisionalAnswer.appendChild(document.createTextNode(delta));
                scrollToBottom();
            });
            if (activeRequest !== request) return;
            if (result.failureKind === 'authorization') { clearAuthenticatedHistory(); return; }
            if (result.conversationId) {
                conversation.id = result.conversationId;
                if (result.userMessageId || result.messageId) conversation.persisted = true;
            }
            if (result.userMessageId) {
                operation.userMessageId = result.userMessageId;
                const optimisticUser = conversation.messages.find(item => item.id === operation.localUserMessageId);
                if (optimisticUser) {
                    optimisticUser.id = result.userMessageId;
                    optimisticUser.pending = false;
                }
            }
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
            if (status) updateThinkingStatus(
                result.succeeded
                    ? (result.messageId ? '' : 'The response is missing its saved conversation receipt.')
                    : structuredFailureMessage(result),
                false
            );
            renderAll({ forceBottom: true });
        } catch (error) {
            if (activeRequest === request && status) updateThinkingStatus(
                request.signal.aborted
                    ? 'Response stopped. Check the saved outcome before sending again.'
                    : error.message || 'The response could not be received. Check the saved outcome.',
                false
            );
        } finally {
            provisionalAnswer?.remove();
            if (epoch !== accountGeneration) return;
            if (activeRequest === request) {
                activeRequest = null;
                setBusy(false);
            }
            if (founderCommandConfirmed) founderCommandConfirmed.checked = false;
            await refreshHistory();
            scheduleHistoryRefresh();
        }
    }

    form?.addEventListener('submit', async event => {
        event.preventDefault();
        if (dictation) { voiceStatus.textContent = 'Stop or cancel dictation before sending.'; return; }
        if (busy) { abortActiveRequest(); return; }
        const text = input?.value.trim();
        if (!text) return;
        const conversation = activeConversation();
        if (conversation.hasNewer) {
            if (status) updateThinkingStatus('Load newer messages before sending a reply.', false);
            return;
        }
        if (conversation.pendingOperation) {
            if (status) updateThinkingStatus('Check the pending request before sending another message.', false);
            return;
        }
        // Only the current Human turn is submitted. Prior Assistant content,
        // permissions and canonical ordering are resolved by the server.
        const operationId = crypto.randomUUID();
        const localUserMessageId = `pending-user-${operationId}`;
        const operation = { id: operationId, localUserMessageId, body: JSON.stringify({
            mode: conversation.mode, nativeOnly: conversation.nativeOnly === true,
            modelSelection: conversation.mode === 'business' ? null : (modelSelection?.value || 'auto'),
            pagePath: window.location.pathname,
            assistantWorkspaceKey: conversation.assistantWorkspaceKey || null,
            assistantProvider: conversation.assistantProvider || null,
            assistantConfigurationRevision: conversation.assistantConfigurationRevision || null,
            generateImage: conversation.mode === 'business' && businessConfiguration?.workspaceKey === conversation.assistantWorkspaceKey && businessGenerateImage.checked,
            referenceImageIds: conversation.mode === 'business' && businessConfiguration?.workspaceKey === conversation.assistantWorkspaceKey && businessGenerateImage.checked
                ? Array.from(businessImageReferences.selectedOptions, option => option.value) : [],
            externalAnsweringBlocked: conversation.externalAnsweringBlocked === true,
            sourceLanguageCode: null, conversationId: conversation.id,
            expectedLastMessageId: conversation.lastMessageId || null,
            founderCommandConfirmed: founderCommandConfirmed?.checked === true,
            messages: [{ role: 'user', content: text }]
        }) };
        conversation.pendingOperation = operation;
        conversation.messages.push({
            id: localUserMessageId,
            sentUtc: new Date().toISOString(),
            role: 'user',
            content: text,
            pending: true
        });
        conversation.updatedUtc = new Date().toISOString();
        updateConversationTitle(conversation);
        input.value = '';
        resizeInput();
        renderAll({ forceBottom: true });
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
