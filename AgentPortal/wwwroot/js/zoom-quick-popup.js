// zoom-quick-popup.js — compact Zoom Links popup, shared across CRM pages

(function () {
    'use strict';

    // Inject CSS once
    if (!document.getElementById('zoom-qp-style')) {
        const s = document.createElement('style');
        s.id = 'zoom-qp-style';
        s.textContent = `
.zoom-qp {
    position: fixed;
    z-index: 9900;
    width: 320px;
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
    max-height: 280px;
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
.zoom-qp-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: .6rem;
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
    font-size: .75rem;
    font-weight: 600;
    color: #64748b;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 180px;
}
.zoom-qp-copy {
    flex-shrink: 0;
    padding: .3rem .7rem;
    border-radius: 7px;
    border: 1.5px solid rgba(221,180,87,.45);
    background: rgba(221,180,87,.1);
    color: #f6e6b4;
    font-size: .78rem;
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

    const API = '/api/zoom-links';
    let popup = null;
    let activeTrigger = null;

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
        popup.querySelector('.zoom-qp-close').addEventListener('click', close);
        return popup;
    }

    function position(trigger) {
        const rect = trigger.getBoundingClientRect();

        popup.style.top = (rect.bottom + 6) + 'px';
        popup.style.left = rect.left + 'px';

        // keep within viewport horizontally
        requestAnimationFrame(() => {
            const pr = popup.getBoundingClientRect();
            if (pr.right > window.innerWidth - 8) {
                popup.style.left = Math.max(8, window.innerWidth - pr.width - 8) + 'px';
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

            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'zoom-qp-copy';
            copyBtn.textContent = 'Copy';
            copyBtn.addEventListener('click', () => {
                navigator.clipboard.writeText(link.url).then(() => {
                    copyBtn.textContent = 'Copied!';
                    copyBtn.classList.add('copied');
                    setTimeout(() => { copyBtn.textContent = 'Copy'; copyBtn.classList.remove('copied'); }, 1800);
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

            row.appendChild(info);
            row.appendChild(copyBtn);
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
        popup.classList.remove('open');
        activeTrigger = null;
    }

    // Global event delegation
    document.addEventListener('click', (e) => {
        const closeBtn = e.target.closest?.('.zoom-qp-close');
        if (closeBtn) { close(); return; }

        const trigger = e.target.closest?.('[data-zoom-popup]');
        if (trigger) {
            e.stopPropagation();
            if (activeTrigger === trigger && popup?.classList.contains('open')) {
                close();
            } else {
                open(trigger);
            }
            return;
        }

        // click outside closes
        if (popup?.classList.contains('open') && !e.target.closest?.('.zoom-qp')) {
            close();
        }
    }, true);

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && popup?.classList.contains('open')) close();
    });

    // Reposition on resize (position:fixed is viewport-relative so scroll isn't needed)
    window.addEventListener('resize', () => {
        if (activeTrigger && popup?.classList.contains('open')) position(activeTrigger);
    }, { passive: true });
})();
