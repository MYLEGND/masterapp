// zoom-quick-popup.js — centered Zoom Links modal, shared across CRM pages
// Each link row has: [Open] [Copy] [⋮ → Text Link / Email Link]

(function () {
    'use strict';

    // ─── Inject CSS once ────────────────────────────────────────────────────
    if (!document.getElementById('zoom-qp-style')) {
        const s = document.createElement('style');
        s.id = 'zoom-qp-style';
        s.textContent = `
/* ── Full-screen overlay ─────────────────────────────── */
.zoom-qp {
    position: fixed;
    inset: 0;
    z-index: 9900;
    display: none;
    align-items: center;
    justify-content: center;
    padding: 1.5rem 1rem;
}
.zoom-qp.open { display: flex; }

.zoom-qp-backdrop {
    position: absolute;
    inset: 0;
    background: rgba(7,12,23,.72);
    backdrop-filter: blur(7px);
}

/* ── Centered dialog shell ───────────────────────────── */
.zoom-qp-dialog {
    position: relative;
    z-index: 1;
    width: min(680px, 96vw);
    max-height: min(88vh, 820px);
    overflow: auto;
    border-radius: 24px;
    border: 1.8px solid rgba(221,180,87,.5);
    background:
        radial-gradient(860px 360px at -6% -10%, rgba(221,180,87,.2), transparent 56%),
        radial-gradient(760px 340px at 102% -4%, rgba(84,120,176,.14), transparent 60%),
        linear-gradient(168deg, rgba(8,18,36,.995), rgba(14,29,56,.995) 56%, rgba(16,34,64,.995));
    color: #f1f5f9;
    font-family: inherit;
    box-shadow:
        0 40px 100px rgba(0,0,0,.56),
        0 0 0 1px rgba(255,255,255,.06) inset,
        0 24px 42px rgba(221,180,87,.1);
    padding: 1.75rem;
    display: flex;
    flex-direction: column;
    gap: 0;
}

.zoom-qp-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    position: sticky;
    top: 0;
    z-index: 5;
    background: linear-gradient(180deg, rgba(8,18,36,.99), rgba(14,29,56,.98));
    padding-bottom: 1.1rem;
    border-bottom: 1px solid rgba(221,180,87,.22);
    margin-bottom: 1.25rem;
    gap: 1rem;
}
.zoom-qp-head-copy {
    display: flex;
    flex-direction: column;
    gap: .3rem;
}
.zoom-qp-kicker {
    font-size: .76rem;
    font-weight: 900;
    letter-spacing: .1em;
    text-transform: uppercase;
    color: #e7c06d;
}
.zoom-qp-title {
    font-size: 1.55rem;
    font-weight: 950;
    color: #f8fafc;
    line-height: 1.15;
}
.zoom-qp-sub {
    margin: 0;
    color: #94a3b8;
    font-size: .88rem;
    font-weight: 600;
    line-height: 1.5;
}
.zoom-qp-close {
    width: 44px;
    height: 44px;
    border-radius: 10px;
    border: 2px solid rgba(221,180,87,.8);
    background: linear-gradient(180deg, rgba(15,26,50,.98), rgba(11,21,41,.98));
    color: #ffffff;
    font-size: 1.9rem;
    line-height: 1;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    transition: background .12s, border-color .12s, transform .12s;
    box-shadow: 0 10px 20px rgba(0,0,0,.34);
}
.zoom-qp-close:hover {
    background: linear-gradient(180deg, rgba(20,35,66,.98), rgba(15,29,56,.98));
    border-color: rgba(242,207,127,.95);
    transform: translateY(-1px);
}

.zoom-qp-list {
    max-height: 420px;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: .55rem;
}
.zoom-qp-empty {
    color: #94a3b8;
    font-size: .9rem;
    font-weight: 600;
    text-align: center;
    padding: 1.5rem 0;
    line-height: 1.6;
}

/* ── Row ─────────────────────────────────────────────── */
.zoom-qp-row {
    display: flex;
    align-items: center;
    gap: .75rem;
    padding: .75rem .9rem;
    border-radius: 14px;
    border: 1.5px solid rgba(221,180,87,.2);
    background: rgba(255,255,255,.03);
    transition: border-color .12s, background .12s;
}
.zoom-qp-row:hover {
    border-color: rgba(221,180,87,.42);
    background: rgba(255,255,255,.06);
}
.zoom-qp-info {
    display: flex;
    flex-direction: column;
    gap: .15rem;
    min-width: 0;
    flex: 1;
}
.zoom-qp-name {
    font-weight: 800;
    font-size: .95rem;
    color: #f1f5f9;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.zoom-qp-url {
    font-size: .77rem;
    font-weight: 600;
    color: #64748b;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

/* ── Row action buttons ──────────────────────────────── */
.zoom-qp-row-actions {
    display: flex;
    align-items: center;
    gap: .4rem;
    flex-shrink: 0;
}
.zoom-qp-open {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: .32rem .72rem;
    border-radius: 8px;
    border: 1.5px solid rgba(100,180,120,.45);
    background: rgba(100,180,120,.1);
    color: #a8e6bb;
    font-size: .8rem;
    font-weight: 800;
    text-decoration: none;
    white-space: nowrap;
    cursor: pointer;
    transition: background .12s, border-color .12s;
    font-family: inherit;
}
.zoom-qp-open:hover {
    background: rgba(100,180,120,.24);
    border-color: rgba(100,180,120,.75);
    color: #a8e6bb;
}
.zoom-qp-copy {
    padding: .32rem .72rem;
    border-radius: 8px;
    border: 1.5px solid rgba(221,180,87,.45);
    background: rgba(221,180,87,.1);
    color: #f6e6b4;
    font-size: .8rem;
    font-weight: 800;
    cursor: pointer;
    transition: background .12s, border-color .12s;
    white-space: nowrap;
    font-family: inherit;
}
.zoom-qp-copy:hover,
.zoom-qp-copy.copied {
    background: rgba(221,180,87,.24);
    border-color: rgba(221,180,87,.75);
}
.zoom-qp-dots {
    width: 32px;
    height: 32px;
    border-radius: 8px;
    border: 1.5px solid rgba(221,180,87,.28);
    background: transparent;
    color: #94a3b8;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 1.1rem;
    line-height: 1;
    transition: background .12s, border-color .12s, color .12s;
    flex-shrink: 0;
}
.zoom-qp-dots:hover {
    background: rgba(221,180,87,.14);
    border-color: rgba(221,180,87,.6);
    color: #e7c06d;
}

/* ── Actions submenu (body-appended, position:fixed) ─── */
.zoom-qp-actions-menu {
    position: fixed;
    z-index: 9950;
    min-width: 154px;
    border-radius: 10px;
    border: 1.5px solid rgba(221,180,87,.48);
    background: linear-gradient(160deg, rgba(10,20,42,.99), rgba(16,32,60,.99));
    box-shadow: 0 14px 36px rgba(0,0,0,.55), 0 0 0 1px rgba(255,255,255,.04) inset;
    display: none;
    flex-direction: column;
    overflow: hidden;
    padding: .3rem 0;
}
.zoom-qp-actions-menu.open { display: flex; }

.zoom-qp-action-btn {
    display: flex;
    align-items: center;
    gap: .55rem;
    width: 100%;
    padding: .58rem 1rem;
    background: transparent;
    border: none;
    color: #f1f5f9;
    font-size: .84rem;
    font-weight: 700;
    cursor: pointer;
    text-align: left;
    transition: background .1s, color .1s;
    white-space: nowrap;
    font-family: inherit;
}
.zoom-qp-action-btn:hover {
    background: rgba(221,180,87,.14);
    color: #e7c06d;
}
.zoom-qp-action-icon {
    font-size: .95rem;
    line-height: 1;
    flex-shrink: 0;
}

/* ── Footer ─────────────────────────────────────────── */
.zoom-qp-foot {
    margin-top: 1.1rem;
    padding-top: .8rem;
    border-top: 1px solid rgba(221,180,87,.16);
    font-size: .78rem;
    font-weight: 600;
    color: #475569;
    text-align: center;
}
.zoom-qp-foot a {
    color: #e7c06d;
    text-decoration: none;
}
.zoom-qp-foot a:hover { text-decoration: underline; }

@media (max-width: 900px) {
    .zoom-qp {
        padding: max(10px, env(safe-area-inset-top)) 10px max(10px, env(safe-area-inset-bottom)) 10px;
    }
    .zoom-qp-dialog {
        width: min(680px, 98vw);
        max-height: calc(100dvh - 20px);
        border-radius: 16px;
        padding: 1rem .85rem .95rem;
    }
    .zoom-qp-head {
        margin: -1rem -.85rem 1rem;
        padding: .85rem .85rem .75rem;
        border-radius: 16px 16px 0 0;
    }
    .zoom-qp-close {
        width: 46px;
        height: 46px;
        font-size: 2rem;
    }
}
        `;
        document.head.appendChild(s);
    }

    // ─── State ──────────────────────────────────────────────────────────────
    const API = '/api/zoom-links';
    let popup         = null;
    let actionsMenu   = null;
    let activeDotsUrl = null;

    // ─── Contact context helpers ────────────────────────────────────────────
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

    // ─── Send actions ────────────────────────────────────────────────────────
    function sendTextLink(url) {
        const phone = getContactPhone();
        const body  = `Here's my Zoom link: ${url}`;
        const uri   = phone
            ? `sms:${phone}?body=${encodeURIComponent(body)}`
            : `sms:?body=${encodeURIComponent(body)}`;
        window.location.href = uri;
    }

    function sendEmailLink(url) {
        const email   = getContactEmail();
        const subject = encodeURIComponent('Zoom Meeting Link');
        const body    = encodeURIComponent(`Hi,\n\nHere's the Zoom link for our meeting:\n${url}\n\nLooking forward to connecting with you.`);
        const compose = `https://outlook.office.com/mail/deeplink/compose?to=${encodeURIComponent(email)}&subject=${subject}&body=${body}`;
        window.open(compose, '_blank', 'noopener');
    }

    // ─── Actions submenu ─────────────────────────────────────────────────────
    function getOrCreateActionsMenu() {
        if (actionsMenu) return actionsMenu;
        actionsMenu = document.createElement('div');
        actionsMenu.className = 'zoom-qp-actions-menu';
        actionsMenu.setAttribute('role', 'menu');
        actionsMenu.innerHTML = `
            <button type="button" class="zoom-qp-action-btn" data-zoom-action="text">
                <span class="zoom-qp-action-icon">💬</span> Text Link
            </button>
            <button type="button" class="zoom-qp-action-btn" data-zoom-action="email">
                <span class="zoom-qp-action-icon">✉️</span> Email Link
            </button>
        `;
        document.body.appendChild(actionsMenu);
        return actionsMenu;
    }

    function openActionsMenu(dotsBtn, linkUrl) {
        getOrCreateActionsMenu();
        activeDotsUrl = linkUrl;
        actionsMenu.classList.add('open');

        const rect = dotsBtn.getBoundingClientRect();
        actionsMenu.style.top  = (rect.bottom + 4) + 'px';
        actionsMenu.style.left = (rect.right - actionsMenu.offsetWidth) + 'px';

        requestAnimationFrame(() => {
            const mr = actionsMenu.getBoundingClientRect();
            if (mr.right > window.innerWidth - 8)
                actionsMenu.style.left = Math.max(8, window.innerWidth - mr.width - 8) + 'px';
            if (mr.bottom > window.innerHeight - 8)
                actionsMenu.style.top = Math.max(8, rect.top - mr.height - 4) + 'px';
        });
    }

    function closeActionsMenu() {
        actionsMenu?.classList.remove('open');
        activeDotsUrl = null;
    }

    // ─── Main popup ──────────────────────────────────────────────────────────
    function getOrCreatePopup() {
        if (popup) return popup;
        popup = document.createElement('div');
        popup.className = 'zoom-qp';
        popup.setAttribute('role', 'dialog');
        popup.setAttribute('aria-modal', 'true');
        popup.setAttribute('aria-label', 'Zoom Links');
        popup.innerHTML = `
            <div class="zoom-qp-backdrop"></div>
            <div class="zoom-qp-dialog">
                <div class="zoom-qp-head">
                    <div class="zoom-qp-head-copy">
                        <div class="zoom-qp-kicker">Legend™ Quick Access</div>
                        <div class="zoom-qp-title">Zoom Links</div>
                        <p class="zoom-qp-sub">Launch your saved Zoom meeting links instantly. Open, copy, or send to your contact.</p>
                    </div>
                    <button type="button" class="zoom-qp-close" aria-label="Close">×</button>
                </div>
                <div class="zoom-qp-list"><div class="zoom-qp-empty">Loading…</div></div>
                <div class="zoom-qp-foot">Manage links on the <a href="/" target="_blank" rel="noopener">Home</a> page</div>
            </div>
        `;
        document.body.appendChild(popup);
        return popup;
    }

    async function loadAndRender() {
        const list = popup.querySelector('.zoom-qp-list');
        list.innerHTML = '<div class="zoom-qp-empty">Loading…</div>';
        try {
            const res = await fetch(API);
            if (!res.ok) throw new Error();
            const links = await res.json();
            renderLinks(list, links);
        } catch {
            list.innerHTML = '<div class="zoom-qp-empty">Could not load links.</div>';
        }
    }

    function renderLinks(list, links) {
        list.innerHTML = '';
        if (!links.length) {
            list.innerHTML = '<div class="zoom-qp-empty">No Zoom links saved yet.<br>Add them from the Home page.</div>';
            return;
        }
        links.forEach(link => {
            const row = document.createElement('div');
            row.className = 'zoom-qp-row';

            // Info block
            const info = document.createElement('div');
            info.className = 'zoom-qp-info';

            const name = document.createElement('span');
            name.className = 'zoom-qp-name';
            name.textContent = link.name;

            const urlSpan = document.createElement('span');
            urlSpan.className = 'zoom-qp-url';
            urlSpan.textContent = link.url;

            info.appendChild(name);
            info.appendChild(urlSpan);

            // Action buttons wrapper
            const actions = document.createElement('div');
            actions.className = 'zoom-qp-row-actions';

            // Open button
            const openBtn = document.createElement('a');
            openBtn.className = 'zoom-qp-open';
            openBtn.href = link.url;
            openBtn.target = '_blank';
            openBtn.rel = 'noopener';
            openBtn.textContent = 'Open';
            openBtn.addEventListener('click', e => e.stopPropagation());

            // Copy button
            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'zoom-qp-copy';
            copyBtn.textContent = 'Copy';
            copyBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                closeActionsMenu();
                navigator.clipboard.writeText(link.url).then(() => {
                    copyBtn.textContent = 'Copied!';
                    copyBtn.classList.add('copied');
                    setTimeout(() => {
                        copyBtn.textContent = 'Copy';
                        copyBtn.classList.remove('copied');
                    }, 1800);
                }).catch(() => {
                    const ta = document.createElement('textarea');
                    ta.value = link.url;
                    ta.style.cssText = 'position:fixed;opacity:0;';
                    document.body.appendChild(ta);
                    ta.select();
                    document.execCommand('copy');
                    document.body.removeChild(ta);
                    copyBtn.textContent = 'Copied!';
                    setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1800);
                });
            });

            // Dots (⋮) button — opens Text/Email submenu
            const dotsBtn = document.createElement('button');
            dotsBtn.type = 'button';
            dotsBtn.className = 'zoom-qp-dots';
            dotsBtn.setAttribute('aria-label', 'More actions');
            dotsBtn.dataset.linkUrl = link.url;
            dotsBtn.innerHTML = '&#8942;';

            actions.appendChild(openBtn);
            actions.appendChild(copyBtn);
            actions.appendChild(dotsBtn);
            row.appendChild(info);
            row.appendChild(actions);
            list.appendChild(row);
        });
    }

    function open() {
        getOrCreatePopup();
        popup.classList.add('open');
        document.body.style.overflow = 'hidden';
        loadAndRender();
    }

    function close() {
        if (!popup) return;
        closeActionsMenu();
        popup.classList.remove('open');
        document.body.style.overflow = '';
    }

    // ─── Global event delegation ────────────────────────────────────────────
    document.addEventListener('click', (e) => {
        // 1. Popup close button
        if (e.target.closest?.('.zoom-qp-close')) {
            close();
            return;
        }

        // 2. Backdrop click — close
        if (e.target.closest?.('.zoom-qp-backdrop')) {
            close();
            return;
        }

        // 3. Action submenu buttons (Text / Email)
        const actionBtn = e.target.closest?.('[data-zoom-action]');
        if (actionBtn) {
            const action = actionBtn.dataset.zoomAction;
            const url    = activeDotsUrl || '';
            closeActionsMenu();
            close();
            if (action === 'text')  sendTextLink(url);
            if (action === 'email') sendEmailLink(url);
            return;
        }

        // 4. Dots button — toggle actions menu
        const dotsEl = e.target.closest?.('.zoom-qp-dots');
        if (dotsEl) {
            e.stopPropagation();
            const linkUrl = dotsEl.dataset.linkUrl;
            if (actionsMenu?.classList.contains('open') && activeDotsUrl === linkUrl) {
                closeActionsMenu();
            } else {
                closeActionsMenu();
                openActionsMenu(dotsEl, linkUrl);
            }
            return;
        }

        // 5. Zoom trigger button
        const trigger = e.target.closest?.('[data-zoom-popup]');
        if (trigger) {
            e.stopPropagation();
            closeActionsMenu();
            if (popup?.classList.contains('open')) {
                close();
            } else {
                open();
            }
            return;
        }

        // 6. Click inside actions menu — don't close anything
        if (e.target.closest?.('.zoom-qp-actions-menu')) return;

        // 7. Click inside dialog — close actions menu if open, otherwise keep dialog open
        if (e.target.closest?.('.zoom-qp-dialog')) {
            if (actionsMenu?.classList.contains('open')) closeActionsMenu();
            return;
        }

        // 8. Click completely outside — close everything
        if (actionsMenu?.classList.contains('open')) closeActionsMenu();
        if (popup?.classList.contains('open')) close();
    }, true);

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            if (actionsMenu?.classList.contains('open')) {
                closeActionsMenu();
            } else if (popup?.classList.contains('open')) {
                close();
            }
        }
    });
})();
