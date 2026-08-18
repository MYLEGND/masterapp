(() => {
    'use strict';

    const trigger =
        document.getElementById(
            'legendFounderAiTrigger'
        );

    const modalElement =
        document.getElementById(
            'legendFounderAiModal'
        );

    if (
        !trigger ||
        !modalElement ||
        typeof bootstrap === 'undefined'
    ) {
        return;
    }

    const STORAGE_KEY =
        'legendFounderAi.conversations.v1';

    const MAX_CONVERSATIONS = 30;
    const MAX_MESSAGES = 30;

    const transcript =
        document.getElementById(
            'legendFounderAiTranscript'
        );

    const welcome =
        document.getElementById(
            'legendFounderAiWelcome'
        );

    const form =
        document.getElementById(
            'legendFounderAiForm'
        );

    const input =
        document.getElementById(
            'legendFounderAiInput'
        );

    const send =
        document.getElementById(
            'legendFounderAiSend'
        );

    const newConversation =
        document.getElementById(
            'legendFounderAiNew'
        );

    const clearHistory =
        document.getElementById(
            'legendFounderAiClearHistory'
        );

    const history =
        document.getElementById(
            'legendFounderAiHistory'
        );

    const historyEmpty =
        document.getElementById(
            'legendFounderAiHistoryEmpty'
        );

    const status =
        document.getElementById(
            'legendFounderAiStatus'
        );

    const subtitle =
        document.getElementById(
            'legendFounderAiSubtitle'
        );

    const conversationState =
        document.getElementById(
            'legendFounderAiConversationState'
        );

    const modeButtons =
        Array.from(
            document.querySelectorAll(
                '[data-legend-ai-mode]'
            )
        );

    const modal =
        bootstrap.Modal.getOrCreateInstance(
            modalElement
        );

    let busy = false;

    let state = loadState();

    ensureActiveConversation();

    trigger.addEventListener(
        'click',
        () => {
            modal.show();

            renderAll();

            window.setTimeout(
                () => input?.focus(),
                160
            );
        }
    );

    function createId() {
        if (
            window.crypto &&
            typeof window.crypto.randomUUID ===
                'function'
        ) {
            return window.crypto.randomUUID();
        }

        return (
            Date.now().toString(36) +
            '-' +
            Math.random()
                .toString(36)
                .slice(2)
        );
    }

    function newConversationRecord(
        mode = 'legend'
    ) {
        const now =
            new Date().toISOString();

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
        const conversation =
            newConversationRecord();

        return {
            activeConversationId:
                conversation.id,

            conversations: [
                conversation
            ]
        };
    }

    function loadState() {
        try {
            const raw =
                window.localStorage.getItem(
                    STORAGE_KEY
                );

            if (!raw) {
                return defaultState();
            }

            const parsed =
                JSON.parse(raw);

            if (
                !parsed ||
                !Array.isArray(
                    parsed.conversations
                )
            ) {
                return defaultState();
            }

            parsed.conversations =
                parsed.conversations
                    .filter(
                        conversation =>
                            conversation &&
                            typeof conversation.id ===
                                'string' &&
                            Array.isArray(
                                conversation.messages
                            )
                    )
                    .slice(
                        0,
                        MAX_CONVERSATIONS
                    );

            if (
                parsed.conversations.length ===
                0
            ) {
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
                            new Date(
                                b.updatedUtc
                            ) -
                            new Date(
                                a.updatedUtc
                            )
                    )
                    .slice(
                        0,
                        MAX_CONVERSATIONS
                    );

            window.localStorage.setItem(
                STORAGE_KEY,
                JSON.stringify(state)
            );
        } catch {
            // Browser history persistence is
            // optional. Conversation continues.
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

        const conversation =
            newConversationRecord();

        state.conversations.unshift(
            conversation
        );

        state.activeConversationId =
            conversation.id;

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

        const conversation =
            activeConversation();

        conversation.mode =
            nextMode === 'teacher'
                ? 'teacher'
                : 'legend';

        conversation.updatedUtc =
            new Date().toISOString();

        saveState();
        renderAll();

        input?.focus();
    }

    function startNewConversation() {
        if (busy) {
            return;
        }

        const current =
            activeConversation();

        const conversation =
            newConversationRecord(
                current.mode
            );

        state.conversations.unshift(
            conversation
        );

        state.activeConversationId =
            conversation.id;

        saveState();
        renderAll();

        if (status) {
            status.textContent = '';
        }

        input?.focus();
    }

    function openConversation(id) {
        if (busy) {
            return;
        }

        const conversation =
            state.conversations.find(
                item =>
                    item.id === id
            );

        if (!conversation) {
            return;
        }

        state.activeConversationId = id;

        saveState();
        renderAll();

        input?.focus();
    }

    function clearAllHistory() {
        if (busy) {
            return;
        }

        const replacement =
            defaultState();

        state = replacement;

        try {
            window.localStorage.removeItem(
                STORAGE_KEY
            );
        } catch {
        }

        saveState();
        renderAll();

        if (status) {
            status.textContent = '';
        }

        input?.focus();
    }

    function updateConversationTitle(
        conversation
    ) {
        if (
            conversation.title !==
            'New conversation'
        ) {
            return;
        }

        const firstUser =
            conversation.messages.find(
                message =>
                    message.role === 'user'
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
                title.slice(0, 39) +
                '…';
        }

        conversation.title =
            title || 'New conversation';
    }

    function renderAll() {
        renderModes();
        renderHistory();
        renderConversation();
    }

    function renderModes() {
        const conversation =
            activeConversation();

        for (
            const button
            of modeButtons
        ) {
            const active =
                button.dataset
                    .legendAiMode ===
                conversation.mode;

            button.classList.toggle(
                'is-active',
                active
            );

            button.setAttribute(
                'aria-selected',
                active
                    ? 'true'
                    : 'false'
            );
        }

        if (subtitle) {
            subtitle.textContent =
                conversation.mode ===
                'teacher'
                    ? 'External language teacher & strategy'
                    : 'Governed intelligence conversation';
        }

        if (input) {
            input.placeholder =
                conversation.mode ===
                'teacher'
                    ? 'Talk directly with the OpenAI Teacher…'
                    : 'Talk to Legend® Ai…';
        }
    }

    function renderHistory() {
        if (
            !history ||
            !historyEmpty
        ) {
            return;
        }

        history.replaceChildren();

        const conversations =
            [...state.conversations]
                .sort(
                    (a, b) =>
                        new Date(
                            b.updatedUtc
                        ) -
                        new Date(
                            a.updatedUtc
                        )
                );

        if (
            conversations.length === 0
        ) {
            historyEmpty.classList.add(
                'is-visible'
            );

            return;
        }

        historyEmpty.classList.remove(
            'is-visible'
        );

        for (
            const conversation
            of conversations
        ) {
            const button =
                document.createElement(
                    'button'
                );

            button.type = 'button';

            button.className =
                'legend-founder-ai-history-item';

            if (
                conversation.id ===
                state.activeConversationId
            ) {
                button.classList.add(
                    'is-active'
                );
            }

            const main =
                document.createElement(
                    'span'
                );

            main.className =
                'legend-founder-ai-history-main';

            const title =
                document.createElement(
                    'span'
                );

            title.className =
                'legend-founder-ai-history-title';

            title.textContent =
                conversation.title ||
                'New conversation';

            const preview =
                document.createElement(
                    'span'
                );

            preview.className =
                'legend-founder-ai-history-preview';

            const last =
                conversation.messages[
                    conversation.messages
                        .length - 1
                ];

            preview.textContent =
                last?.content ||
                (
                    conversation.mode ===
                    'teacher'
                        ? 'OpenAI Teacher'
                        : 'Legend® Ai'
                );

            const time =
                document.createElement(
                    'span'
                );

            time.className =
                'legend-founder-ai-history-time';

            time.textContent =
                formatRelativeTime(
                    conversation.updatedUtc
                );

            main.appendChild(title);
            main.appendChild(preview);

            button.appendChild(main);
            button.appendChild(time);

            button.addEventListener(
                'click',
                () =>
                    openConversation(
                        conversation.id
                    )
            );

            history.appendChild(
                button
            );
        }
    }

    function formatRelativeTime(
        iso
    ) {
        const timestamp =
            new Date(iso).getTime();

        if (
            Number.isNaN(timestamp)
        ) {
            return '';
        }

        const elapsed =
            Date.now() - timestamp;

        const minutes =
            Math.floor(
                elapsed / 60000
            );

        if (minutes < 1) {
            return 'Now';
        }

        if (minutes < 60) {
            return `${minutes}m`;
        }

        const hours =
            Math.floor(
                minutes / 60
            );

        if (hours < 24) {
            return `${hours}h`;
        }

        const days =
            Math.floor(
                hours / 24
            );

        if (days < 7) {
            return `${days}d`;
        }

        return new Date(
            timestamp
        ).toLocaleDateString(
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

        if (
            conversation.messages
                .length === 0
        ) {
            if (welcome) {
                transcript.appendChild(
                    welcome.cloneNode(true)
                );
            }
        } else {
            for (
                const message
                of conversation.messages
            ) {
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

        scrollToBottom();
    }

    function appendBubble(
        role,
        content,
        scroll = true
    ) {
        if (!transcript) {
            return;
        }

        const shell =
            document.createElement(
                'div'
            );

        shell.className =
            `legend-founder-ai-message ${
                role === 'user'
                    ? 'is-user'
                    : 'is-assistant'
            }`;

        const bubble =
            document.createElement(
                'div'
            );

        bubble.className =
            'legend-founder-ai-bubble';

        bubble.textContent =
            content;

        shell.appendChild(
            bubble
        );

        transcript.appendChild(
            shell
        );

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

        if (send) {
            send.disabled =
                value;
        }

        if (input) {
            input.disabled =
                value;
        }

        if (newConversation) {
            newConversation.disabled =
                value;
        }

        if (clearHistory) {
            clearHistory.disabled =
                value;
        }

        for (
            const button
            of modeButtons
        ) {
            button.disabled =
                value;
        }

        if (status) {
            status.textContent =
                message;
        }
    }

    function resizeInput() {
        if (!input) {
            return;
        }

        input.style.height =
            'auto';

        input.style.height =
            `${
                Math.min(
                    input.scrollHeight,
                    145
                )
            }px`;
    }

    input?.addEventListener(
        'input',
        resizeInput
    );

    input?.addEventListener(
        'keydown',
        event => {
            if (
                event.key ===
                    'Enter' &&
                !event.shiftKey
            ) {
                event.preventDefault();

                if (!busy) {
                    form?.requestSubmit();
                }
            }
        }
    );

    newConversation
        ?.addEventListener(
            'click',
            startNewConversation
        );

    clearHistory
        ?.addEventListener(
            'click',
            clearAllHistory
        );

    for (
        const button
        of modeButtons
    ) {
        button.addEventListener(
            'click',
            () =>
                setMode(
                    button.dataset
                        .legendAiMode ||
                    'legend'
                )
        );
    }

    form?.addEventListener(
        'submit',
        async event => {
            event.preventDefault();

            if (
                busy ||
                !input
            ) {
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
                conversation.messages
                    .length >
                MAX_MESSAGES
            ) {
                conversation.messages.splice(
                    0,
                    conversation.messages
                        .length -
                        MAX_MESSAGES
                );
            }

            conversation.updatedUtc =
                new Date().toISOString();

            updateConversationTitle(
                conversation
            );

            saveState();

            input.value = '';

            resizeInput();
            renderAll();

            setBusy(
                true,
                conversation.mode ===
                    'teacher'
                    ? 'OpenAI Teacher is reasoning…'
                    : 'Legend® Ai is reasoning and may inspect governed system state…'
            );

            try {
                const token =
                    form.querySelector(
                        'input[name="__RequestVerificationToken"]'
                    )?.value || '';

                const response =
                    await fetch(
                        modalElement.dataset
                            .chatUrl,
                        {
                            method: 'POST',

                            credentials:
                                'same-origin',

                            headers: {
                                'Content-Type':
                                    'application/json',

                                'RequestVerificationToken':
                                    token,

                                'X-Requested-With':
                                    'XMLHttpRequest'
                            },

                            body:
                                JSON.stringify({
                                    mode:
                                        conversation.mode,

                                    messages:
                                        conversation
                                            .messages
                                })
                        }
                    );

                const rawResponse =
                    await response.text();

                let result = null;

                try {
                    result =
                        rawResponse
                            ? JSON.parse(
                                rawResponse
                            )
                            : null;
                } catch {
                    result = null;
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
                    conversation.messages
                        .length >
                    MAX_MESSAGES
                ) {
                    conversation.messages.splice(
                        0,
                        conversation.messages
                            .length -
                            MAX_MESSAGES
                    );
                }

                conversation.updatedUtc =
                    new Date().toISOString();

                saveState();
                renderAll();

                setBusy(false, '');

                input.focus();
            } catch (error) {
                setBusy(
                    false,
                    error instanceof Error
                        ? error.message
                        : 'Legend® Ai could not complete that response.'
                );

                input.focus();
            }
        }
    );

    renderAll();
})();
