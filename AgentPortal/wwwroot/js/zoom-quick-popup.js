// zoom-quick-popup.js — compact Zoom Links popup, shared across CRM pages
// Each link row has: [Copy] [⋮ → Text Link / Email Link]

(function () {
    'use strict';

    // ─── Inject CSS once ────────────────────────────────────────────────────
    if (!document.getElementById('zoom-qp-style')) {
        const s = document.createElement('style');
        s.id = 'zoom-qp-style';
        s.textContent = `
/* ── Main popup ─────────────────────────────────────── */
.zoom-qp {
    position: fixed;
    z-index: 9900;
    width: 340px;
    border-radius: 16px;
    border: 1.5px solid rgba(221,180,87,.55);
    background: linear-gradient(160deg, rgba(8,18,36,.98), rgba(14,29,56,.98));
    box-shadow: 0 24px 56px rgba(0,0,0,.52), 0 0 0 1px rgba(255,255,255,.05) inset;
    color: #f1f5f9;
    font-family: inherit;
    display: none;
    flex-direction: column;
    overflow: hidden;
}
.zoom-qp.open { display: flex; }

.zoom-qp-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: .75rem 1rem .65rem;
    border-bottom: 1px solid rgba(221,180,87,.22);
}
.zoom-qp-title {
    font-size: .8rem;
    font-weight: 900;
    letter-spacing: .1em;
    text-transform: uppercase;
    color: #e7c06d;
}
.zoom-qp-close {
    width: 28px;
    height: 28px;
    border-radius: 8px;
    border: 1px solid rgba(166,128,35,.4);
    background: rgba(255,255,255,.05);
    color: #f1f5f9;
    font-size: 1.15rem;
    line-height: 1;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    transition: background .12s;
}
.zoom-qp-close:hover { background: rgba(255,255,255,.12); }

.zoom-qp-list {
    max-height: 300px;
    overflow-y: auto;
    padding: .5rem .75rem;
    display: flex;
    flex-direction: column;
    gap: .4rem;
}
.zoom-qp-empty {
    color: #94a3b8;
    font-size: .88rem;
    font-weight: 600;
    text-align: center;
    padding: .75rem 0;
    line-height: 1.5;
}

/* ── Row ─────────────────────────────────────────────── */
.zoom-qp-row {
    display: flex;
    align-items: center;
    gap: .5rem;
    padding: .5rem .6rem;
    border-radius: 10px;
    border: 1px solid rgba(221,180,87,.2);
    background: rgba(255,255,255,.03);
    transition: border-color .12s, background .12s;
}
.zoom-qp-row:hover {
    border-color: rgba(221,180,87,.4);
    background: rgba(255,255,255,.06);
}
.zoom-qp-info {
    display: flex;
    flex-direction: column;
    gap: .1rem;
    min-width: 0;
    flex: 1;
}
.zoom-qp-name {
    font-weight: 800;
    font-size: .88rem;
    color: #f1f5f9;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.zoom-qp-url {
    font-size: .72rem;
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
    gap: .3rem;
    flex-shrink: 0;
}
.zoom-qp-copy {
    padding: .28rem .62rem;
    border-radius: 7px;
    border: 1.5px solid rgba(221,180,87,.45);
    background: rgba(221,180,87,.1);
    color: #f6e6b4;
    font-size: .76rem;
    font-weight: 800;
    cursor: pointer;
    transition: background .12s, border-color .12s;
    white-space: nowrap;
}
.zoom-qp-copy:hover,
.zoom-qp-copy.copied {
    background: rgba(221,180,87,.24);
    border-color: rgba(221,180,87,.75);
}
.zoom-qp-dots {
    width: 28px;
    height: 28px;
    border-radius: 7px;
    border: 1px solid rgba(221,180,87,.28);
    background: transparent;
    color: #94a3b8;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 1rem;
    line-height: 1;
    letter-spacing: 0;
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
    padding: .5rem 1rem .65rem;
    border-top: 1px solid rgba(221,180,87,.16);
    font-size: .75rem;
    font-weight: 600;
    color: #475569;
    text-align: center;
}
.zoom-qp-foot a {
    color: #e7c06d;
    text-decoration: none;
}
.zoom-qp-foot a:hover { text-decoration: underline; }
        `;
        document.head.appendChild(s);
    }

    // ─── State ──────────────────────────────────────────────────────────────
    const API = '/api/zoom-links';
    let popup        = null;
    let activeTrigger = null;
    let actionsMenu  = null;
    let activeDotsUrl = null;  // zoom URL for the currently-open actions menu

    // ─── Contact context helpers ────────────────────────────────────────────
    // Reads the current contact's phone from the quick view drawer OR lead bridge.
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
        const uri     = `mailto:${email}?subject=${subject}&body=${body}`;
        window.location.href = uri;
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
            if (mr.right > window.innerWidth - 8) {
                actionsMenu.style.left = Math.max(8, window.innerWidth - mr.width - 8) + 'px';
            }
            if (mr.bottom > window.innerHeight - 8) {
                actionsMenu.style.top = Math.max(8, rect.top - mr.height - 4) + 'px';
            }
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
        popup.setAttribute('aria-label', 'Zoom Links');
        popup.innerHTML = `
            <div class="zoom-qp-head">
                <span class="zoom-qp-title">Zoom Links</span>
                <button type="button" class="zoom-qp-close" aria-label="Close">×</button>
            </div>
            <div class="zoom-qp-list"><div class="zoom-qp-empty">Loading…</div></div>
            <div class="zoom-qp-foot">Manage links on <a href="/" target="_blank" rel="noopener">Home</a></div>
        `;
        document.body.appendChild(popup);
        return popup;
    }

    function position(trigger) {
        const rect = trigger.getBoundingClientRect();
        popup.style.top  = (rect.bottom + 6) + 'px';
        popup.style.left = rect.left + 'px';

        requestAnimationFrame(() => {
            const pr = popup.getBoundingClientRect();
            if (pr.right > window.innerWidth - 8) {
                popup.style.left = Math.max(8, window.innerWidth - pr.width - 8) + 'px';
            }
            if (pr.bottom > window.innerHeight - 8) {
                popup.style.top = Math.max(8, rect.top - pr.height - 6) + 'px';
            }
        });
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

            const url = document.createElement('span');
            url.className = 'zoom-qp-url';
            url.textContent = link.url;

            info.appendChild(name);
            info.appendChild(url);

            // Action buttons wrapper
            const actions = document.createElement('div');
            actions.className = 'zoom-qp-row-actions';

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
            dotsBtn.innerHTML = '&#8942;'; // ⋮ vertical ellipsis
            dotsBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                if (actionsMenu?.classList.contains('open') && activeDotsUrl === link.url) {
                    closeActionsMenu();
                } else {
                    closeActionsMenu();
                    openActionsMenu(dotsBtn, link.url);
                }
            });

            actions.appendChild(copyBtn);
            actions.appendChild(dotsBtn);
            row.appendChild(info);
            row.appendChild(actions);
            list.appendChild(row);
        });
    }

    function open(trigger) {
        getOrCreatePopup();
        activeTrigger = trigger;
        popup.classList.add('open');
        position(trigger);
        loadAndRender();
    }

    function close() {
        if (!popup) return;
        closeActionsMenu();
        popup.classList.remove('open');
        activeTrigger = null;
    }

    // ─── Global event delegation (capture phase) ────────────────────────────
    document.addEventListener('click', (e) => {
        // 1. Popup close button
        if (e.target.closest?.('.zoom-qp-close')) {
            close();
            return;
        }

        // 2. Action submenu buttons (Text / Email)
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

        // 3. Dots button — handled inline via addEventListener on the element;
        //    just stop propagation so it doesn't trigger outside-click close
        if (e.target.closest?.('.zoom-qp-dots')) {
            e.stopPropagation();
            return;
        }

        // 4. Zoom trigger button
        const trigger = e.target.closest?.('[data-zoom-popup]');
        if (trigger) {
            e.stopPropagation();
            closeActionsMenu();
            if (activeTrigger === trigger && popup?.classList.contains('open')) {
                close();
            } else {
                open(trigger);
            }
            return;
        }

        // 5. Click inside actions menu — don't close anything
        if (e.target.closest?.('.zoom-qp-actions-menu')) {
            return;
        }

        // 6. Click inside popup — don't close popup
        if (e.target.closest?.('.zoom-qp')) {
            // Close actions menu if open and click is outside it
            if (actionsMenu?.classList.contains('open')) {
                closeActionsMenu();
            }
            return;
        }

        // 7. Click completely outside — close everything
        if (actionsMenu?.classList.contains('open')) {
            closeActionsMenu();
        }
        if (popup?.classList.contains('open')) {
            close();
        }
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

    // Reposition on resize
    window.addEventListener('resize', () => {
        if (activeTrigger && popup?.classList.contains('open')) position(activeTrigger);
    }, { passive: true });
})();
