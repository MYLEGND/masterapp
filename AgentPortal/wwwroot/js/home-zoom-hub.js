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

    // ── Actions submenu (body-level, avoids overflow:hidden clipping) ───────
    let actionsMenu = null;
    let activeDotsUrl = null;

    function getOrCreateActionsMenu() {
        if (actionsMenu) return actionsMenu;
        actionsMenu = document.createElement('div');
        actionsMenu.className = 'zoom-qp-actions-menu';
        actionsMenu.style.cssText = 'display:none;position:fixed;z-index:9960;background:#1e2a3a;border:1px solid rgba(255,255,255,.12);border-radius:8px;padding:4px 0;min-width:130px;box-shadow:0 8px 24px rgba(0,0,0,.45);';
        actionsMenu.innerHTML =
            '<button type="button" class="zoom-qp-action-btn" data-action="text" style="display:block;width:100%;padding:8px 14px;background:none;border:none;color:#c9d8ea;font-size:12px;text-align:left;cursor:pointer;white-space:nowrap;">📱 Text Link</button>' +
            '<button type="button" class="zoom-qp-action-btn" data-action="email" style="display:block;width:100%;padding:8px 14px;background:none;border:none;color:#c9d8ea;font-size:12px;text-align:left;cursor:pointer;white-space:nowrap;">✉️ Email Link</button>';
        document.body.appendChild(actionsMenu);
        return actionsMenu;
    }

    function openActionsMenu(dotsBtn, linkUrl) {
        const menu = getOrCreateActionsMenu();
        activeDotsUrl = linkUrl;
        const r = dotsBtn.getBoundingClientRect();
        let top = r.bottom + 4;
        const menuH = 80;
        if (top + menuH > window.innerHeight - 8) top = r.top - menuH - 4;
        menu.style.top  = top + 'px';
        menu.style.left = Math.max(8, r.right - 130) + 'px';
        menu.style.display = 'block';
    }

    function closeActionsMenu() {
        if (actionsMenu) actionsMenu.style.display = 'none';
        activeDotsUrl = null;
    }

    function getContactPhone() {
        return (
            document.getElementById('dPhoneInput')?.value?.trim() ||
            document.querySelector('[data-lf-value="phone"]')?.textContent?.trim() ||
            ''
        );
    }

    function getContactEmail() {
        return (
            document.getElementById('dEmailInput')?.value?.trim() ||
            document.querySelector('[data-lf-value="email"]')?.textContent?.trim() ||
            ''
        );
    }

    function sendTextLink(url) {
        const phone = getContactPhone();
        const body  = `Here's my Zoom link: ${url}`;
        const uri   = phone ? `sms:${phone}?body=${encodeURIComponent(body)}` : `sms:?body=${encodeURIComponent(body)}`;
        window.location.href = uri;
    }

    function sendEmailLink(url) {
        const email   = getContactEmail();
        const subject = encodeURIComponent('Zoom Meeting Link');
        const body    = encodeURIComponent(`Hi,\n\nHere's the Zoom link for our meeting:\n${url}\n\nLooking forward to connecting with you.`);
        const uri     = `mailto:${email}?subject=${subject}&body=${body}`;
        const a = document.createElement('a');
        a.href = uri; a.style.display = 'none';
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
    }

    // Close actions menu on outside click
    document.addEventListener('click', function(e) {
        if (!actionsMenu || actionsMenu.style.display === 'none') return;
        if (actionsMenu.contains(e.target)) return;
        if (e.target.closest('[data-zoom-hub-dots]')) return;
        closeActionsMenu();
    }, true);

    // Handle action button clicks
    document.addEventListener('click', function(e) {
        const btn = e.target.closest('.zoom-qp-action-btn');
        if (!btn || !actionsMenu || actionsMenu.style.display === 'none') return;
        e.stopPropagation();
        const action = btn.dataset.action;
        const url = activeDotsUrl;
        closeActionsMenu();
        if (action === 'text')  sendTextLink(url);
        if (action === 'email') sendEmailLink(url);
    }, true);

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
        if (e.key === 'Escape') {
            if (actionsMenu && actionsMenu.style.display !== 'none') {
                closeActionsMenu();
            } else if (hub.classList.contains('open')) {
                close();
            }
        }
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

            const dotsBtn = document.createElement('button');
            dotsBtn.type = 'button';
            dotsBtn.className = 'zoom-link-btn zoom-link-btn-dots';
            dotsBtn.setAttribute('data-zoom-hub-dots', '');
            dotsBtn.setAttribute('aria-label', 'More actions');
            dotsBtn.style.cssText = 'padding:0 7px;font-size:16px;letter-spacing:1px;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.12);border-radius:5px;color:#8ba5c2;cursor:pointer;line-height:1;';
            dotsBtn.textContent = '⋮';
            dotsBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const menu = getOrCreateActionsMenu();
                if (activeDotsUrl === link.url && menu.style.display !== 'none') {
                    closeActionsMenu();
                } else {
                    openActionsMenu(dotsBtn, link.url);
                }
            });

            const delBtn = document.createElement('button');
            delBtn.type = 'button';
            delBtn.className = 'zoom-link-btn zoom-link-btn-delete';
            delBtn.textContent = 'Remove';
            delBtn.addEventListener('click', () => deleteLink(link.id));

            actions.appendChild(copyBtn);
            actions.appendChild(dotsBtn);
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
