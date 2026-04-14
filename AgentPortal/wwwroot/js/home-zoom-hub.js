// home-zoom-hub.js — Zoom Links hub for AgentPortal Home

(function () {
    'use strict';

    const hub = document.getElementById('homeZoomHub');
    if (!hub) return;

    const backdrop = document.getElementById('homeZoomBackdrop');
    const closeBtn = document.getElementById('homeZoomClose');
    const trigger = document.getElementById('homeZoomTrigger');
    const listEl = document.getElementById('homeZoomList');
    const emptyEl = document.getElementById('homeZoomEmpty');
    const form = document.getElementById('homeZoomForm');
    const nameInput = document.getElementById('homeZoomName');
    const urlInput = document.getElementById('homeZoomUrl');
    const saveBtn = document.getElementById('homeZoomSave');
    const errorEl = document.getElementById('homeZoomError');

    const API = '/api/zoom-links';

    function open() {
        hub.hidden = false;
        requestAnimationFrame(() => hub.classList.add('open'));
        document.body.style.overflow = 'hidden';
        loadLinks();
    }

    function close() {
        hub.classList.remove('open');
        document.body.style.overflow = '';
        hub.addEventListener('transitionend', () => { hub.hidden = true; }, { once: true });
    }

    trigger?.addEventListener('click', open);
    closeBtn?.addEventListener('click', close);
    backdrop?.addEventListener('click', close);

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && hub.classList.contains('open')) close();
    });

    async function loadLinks() {
        try {
            const res = await fetch(API);
            if (!res.ok) throw new Error('Failed to load');
            const links = await res.json();
            renderLinks(links);
        } catch {
            renderLinks([]);
        }
    }

    function renderLinks(links) {
        listEl.innerHTML = '';
        if (!links.length) {
            emptyEl.hidden = false;
            return;
        }
        emptyEl.hidden = true;
        links.forEach(link => {
            const row = document.createElement('div');
            row.className = 'zoom-link-row';
            row.dataset.id = link.id;

            const info = document.createElement('div');
            info.className = 'zoom-link-info';

            const name = document.createElement('span');
            name.className = 'zoom-link-name';
            name.textContent = link.name;

            const url = document.createElement('span');
            url.className = 'zoom-link-url';
            url.textContent = link.url;

            info.appendChild(name);
            info.appendChild(url);

            const actions = document.createElement('div');
            actions.className = 'zoom-link-actions';

            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'zoom-link-btn zoom-link-btn-copy';
            copyBtn.textContent = 'Copy';
            copyBtn.addEventListener('click', () => copyLink(link.url, copyBtn));

            const delBtn = document.createElement('button');
            delBtn.type = 'button';
            delBtn.className = 'zoom-link-btn zoom-link-btn-delete';
            delBtn.textContent = 'Remove';
            delBtn.addEventListener('click', () => deleteLink(link.id));

            actions.appendChild(copyBtn);
            actions.appendChild(delBtn);
            row.appendChild(info);
            row.appendChild(actions);
            listEl.appendChild(row);
        });
    }

    function copyLink(url, btn) {
        navigator.clipboard.writeText(url).then(() => {
            const orig = btn.textContent;
            btn.textContent = 'Copied!';
            btn.classList.add('copied');
            setTimeout(() => {
                btn.textContent = orig;
                btn.classList.remove('copied');
            }, 1800);
        }).catch(() => {
            // fallback
            const ta = document.createElement('textarea');
            ta.value = url;
            ta.style.cssText = 'position:fixed;opacity:0;';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
            btn.textContent = 'Copied!';
            setTimeout(() => { btn.textContent = 'Copy'; }, 1800);
        });
    }

    async function deleteLink(id) {
        try {
            const res = await fetch(`${API}/${id}`, { method: 'DELETE' });
            if (!res.ok) throw new Error('Delete failed');
            await loadLinks();
        } catch {
            showError('Could not remove link. Please try again.');
        }
    }

    form?.addEventListener('submit', async (e) => {
        e.preventDefault();
        hideError();

        const name = nameInput.value.trim();
        const url = urlInput.value.trim();

        if (!name) { showError('Please enter a name for this link.'); return; }
        if (!url) { showError('Please enter a URL.'); return; }

        saveBtn.disabled = true;
        saveBtn.textContent = 'Saving…';

        try {
            const res = await fetch(API, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, url })
            });
            if (!res.ok) {
                const msg = await res.text();
                throw new Error(msg || 'Save failed');
            }
            nameInput.value = '';
            urlInput.value = '';
            await loadLinks();
        } catch (err) {
            showError(err.message || 'Could not save link. Please try again.');
        } finally {
            saveBtn.disabled = false;
            saveBtn.textContent = 'Save Link';
        }
    });

    function showError(msg) {
        if (!errorEl) return;
        errorEl.textContent = msg;
        errorEl.hidden = false;
    }

    function hideError() {
        if (!errorEl) return;
        errorEl.hidden = true;
        errorEl.textContent = '';
    }
})();
