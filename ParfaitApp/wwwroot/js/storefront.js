window.ParfaitStorefront = (() => {
    const context = window.PARFAIT_COMMERCE_CONTEXT || {};
    const storageKey = String(context.cartStorageKey || "parfaitCart");
    const discountStorageKey = storageKey === "parfaitCart" ? "parfaitDiscountCode" : storageKey + ":discount";
    const emitCartUpdated = () => {
        window.dispatchEvent(new Event("parfait-cart-updated"));
        window.dispatchEvent(new Event("legend-commerce-cart-updated"));
    };
    const quantity = item => item.lots.reduce((sum, lot) => sum + lot.quantity, 0);
    const load = () => {
        const raw = localStorage.getItem(storageKey);
        const parsed = raw ? JSON.parse(raw) : null;
        if (parsed && !Array.isArray(parsed) && parsed.version !== 1) throw new Error("This cart needs a page refresh before it can be changed.");
        if (parsed?.version === 1) return parsed;
        return { version: 1, revision: 0, commands: {}, checkouts: {}, items: (parsed || []).map(item => ({ ...item,
            lots: [{ id: crypto.randomUUID(), quantity: Math.max(0, Number(item.quantity || 0)) }] })) };
    };
    const items = state => state.items.map(item => ({ ...item, quantity: quantity(item) }));
    const readCart = () => {
        try {
            const raw = localStorage.getItem(storageKey);
            const parsed = raw ? JSON.parse(raw) : null;
            const cart = Array.isArray(parsed) ? parsed : parsed?.version === 1 ? items(parsed) : [];
            Object.defineProperty(cart, "revision", { value: parsed?.version === 1 ? parsed.revision : 0 });
            return cart;
        } catch { return []; }
    };
    const save = (state, changed = false) => {
        if (changed) state.revision++;
        state.items.forEach(item => delete item.quantity); // Quantity is projected from owned lots.
        localStorage.setItem(storageKey, JSON.stringify(state)); // Items and receipts commit together.
        if (changed) emitCartUpdated();
    };
    const locked = operation => {
        if (!navigator.locks?.request) return Promise.reject(new Error("This browser cannot safely update your cart. Use an up-to-date browser."));
        return navigator.locks.request("legend-cart:" + storageKey, async () => {
            const state = load();
            save(state); // Prove writable storage / migrate old arrays before any network effect.
            return operation(state);
        });
    };
    const resize = (item, desired) => {
        const current = quantity(item);
        if (desired > current) item.lots.push({ id: crypto.randomUUID(), quantity: desired - current });
        else {
            let remove = current - desired;
            for (let i = item.lots.length - 1; i >= 0 && remove > 0; i--) {
                const removed = Math.min(remove, item.lots[i].quantity);
                item.lots[i].quantity -= removed; remove -= removed;
            }
            item.lots = item.lots.filter(lot => lot.quantity > 0);
        }
    };
    const updateQuantity = (key, desired) => locked(state => {
        const item = state.items.find(row => row.key === key);
        if (!item) return items(state);
        const next = Math.max(0, Math.floor(Number(desired) || 0));
        resize(item, next); state.items = state.items.filter(row => quantity(row) > 0);
        save(state, true); return items(state);
    });
    const adjustQuantity = (key, delta) => locked(state => {
        const item = state.items.find(row => row.key === key);
        if (!item) return items(state);
        resize(item, Math.max(0, quantity(item) + Math.trunc(Number(delta) || 0)));
        state.items = state.items.filter(row => quantity(row) > 0);
        save(state, true); return items(state);
    });
    const removeItem = key => updateQuantity(key, 0);
    const clearCart = () => locked(state => { state.items = []; save(state, true); return []; });
    const applyQuote = (quote, revision) => locked(state => {
        if (state.revision !== revision) return { stale: true, changed: false };
        let changed = false;
        for (const item of state.items) {
            const quoted = quote.items.find(row => row.key === item.key);
            const next = Math.max(0, Number(quoted?.quantity || 0));
            if (quantity(item) !== next) { resize(item, next); changed = true; }
            if (quoted) Object.assign(item, { priceCents: quoted.unitPriceCents, compareAtPriceCents: quoted.compareAtPriceCents,
                imageUrl: quoted.imageUrl || item.imageUrl, name: quoted.name || item.name, slug: quoted.slug || item.slug,
                badge: quoted.badge || item.badge });
        }
        state.items = state.items.filter(item => quantity(item) > 0);
        save(state, changed); return { stale: false, changed };
    });
    const addItem = (item, suppliedCommandId = null) => locked(async state => {
        const requested = Math.max(1, Math.floor(Number(item.quantity) || 1));
        const fingerprint = `${item.id}:${item.size}:${requested}`;
        let eventId = suppliedCommandId;
        if (!eventId) eventId = Object.keys(state.commands).find(id => state.commands[id].status === "pending" &&
            state.commands[id].fingerprint === fingerprint && state.commands[id].revision === state.revision) || crypto.randomUUID();
        let command = state.commands[eventId];
        if (command && command.fingerprint !== fingerprint) throw new Error("This cart command does not match its original item.");
        if (command?.status === "applied" || command?.status === "discarded") return items(state);
        if (command && command.revision !== state.revision) {
            command.status = "discarded"; save(state); return items(state);
        }
        const existing = state.items.find(row => row.key === item.key);
        if (!command) {
            command = { fingerprint, revision: state.revision, currentQuantity: existing ? quantity(existing) : 0, status: "pending" };
            state.commands[eventId] = command; save(state);
        }
        const response = await fetch((context.storeRootPath || "/store") + "/cart/items", {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ eventId, productId: item.id, size: item.size, quantity: requested, currentQuantity: command.currentQuantity,
                sessionId: window.LegendAnalytics?.ids?.getSessionId?.(), visitorId: window.LegendAnalytics?.ids?.getVisitorId?.(),
                url: location.href, referrer: document.referrer })
        });
        if (!response.ok) throw new Error("Unable to add this item. Refresh the page and try again.");
        const accepted = await response.json();
        if (!Number.isSafeInteger(accepted.acceptedQuantity) || accepted.acceptedQuantity < 0 || accepted.acceptedQuantity > requested ||
            !Number.isSafeInteger(accepted.priceCents) || accepted.priceCents <= 0 || accepted.eventId !== eventId)
            throw new Error("The cart response could not be verified. Please try again.");
        if (accepted.acceptedQuantity > 0) {
            const target = existing || { ...item, lots: [] };
            target.priceCents = accepted.priceCents;
            target.lots.push({ id: eventId, quantity: accepted.acceptedQuantity });
            if (!existing) state.items.push(target);
        }
        command.status = "applied"; save(state, true); return items(state);
    });
    const bindCheckout = (attemptId, checkoutItems, revision) => locked(state => {
        if (!attemptId) throw new Error("Refresh checkout before paying.");
        if (state.revision !== revision) throw Object.assign(new Error("Your cart changed. Review the updated order before paying."), { code: "checkout_cart_changed" });
        const lots = [];
        for (const requested of checkoutItems) {
            const item = state.items.find(row => row.id === requested.id && row.size === requested.size);
            let count = Number(requested.quantity);
            if (!item || count <= 0 || quantity(item) < count) throw new Error("Your cart changed. Review it before paying.");
            for (const lot of item.lots) {
                const consumed = Math.min(count, lot.quantity);
                if (consumed > 0) lots.push({ key: item.key, id: lot.id, quantity: consumed });
                count -= consumed;
            }
        }
        const original = state.checkouts[attemptId];
        if (original) {
            if (original.reconciled || JSON.stringify(original.lots) !== JSON.stringify(lots))
                throw Object.assign(new Error("Your cart changed. Review the updated checkout before paying."), { code: "checkout_cart_changed" });
            return { submitted: original.submitted !== false };
        }
        state.checkouts[attemptId] = { lots, reconciled: false, submitted: false }; save(state);
        return { submitted: false };
    });
    const ensurePaymentReady = () => locked(() => true);
    const markCheckoutSubmitted = attemptId => locked(state => {
        const snapshot = state.checkouts[attemptId];
        if (!snapshot || snapshot.reconciled) throw new Error("Refresh checkout before paying.");
        snapshot.submitted = true; save(state); // Durable BEFORE the request can reach a payment provider.
    });
    const abandonUnsubmittedCheckout = attemptId => locked(state => {
        const snapshot = state.checkouts[attemptId];
        if (!snapshot || snapshot.submitted !== false || snapshot.reconciled) return false;
        delete state.checkouts[attemptId]; save(state); return true;
    });
    const reconcilePaidCheckout = attemptId => locked(state => {
        const snapshot = attemptId && state.checkouts[attemptId];
        if (!snapshot || snapshot.reconciled) return false;
        for (const bound of snapshot.lots) {
            const item = state.items.find(row => row.key === bound.key);
            const lot = item?.lots.find(row => row.id === bound.id);
            if (lot) lot.quantity = Math.max(0, lot.quantity - bound.quantity);
        }
        state.items.forEach(item => item.lots = item.lots.filter(lot => lot.quantity > 0));
        state.items = state.items.filter(item => quantity(item) > 0);
        snapshot.reconciled = true; save(state, true);
        if (state.items.length === 0) localStorage.removeItem(discountStorageKey);
        return true;
    });
    const money = cents => `$${(Number(cents || 0) / 100).toFixed(2)}`;
    const itemCount = cart => cart.reduce((sum, item) => sum + Number(item.quantity || 0), 0);
    const subtotal = cart => cart.reduce((sum, item) => sum + Number(item.priceCents || 0) * Number(item.quantity || 0), 0);
    const normalizeDiscountCode = value => (value || "").trim().toUpperCase().replace(/[^A-Z0-9]+/g, "-").replace(/^-+|-+$/g, "");
    const readDiscountCode = () => localStorage.getItem(discountStorageKey) || "";
    const writeDiscountCode = value => {
        const normalized = normalizeDiscountCode(value);
        if (normalized) localStorage.setItem(discountStorageKey, normalized); else localStorage.removeItem(discountStorageKey);
        emitCartUpdated(); return normalized;
    };
    const clearDiscountCode = () => { localStorage.removeItem(discountStorageKey); emitCartUpdated(); };
    window.addEventListener("storage", event => { if (event.key === storageKey) emitCartUpdated(); });
    return { context, storageKey, discountStorageKey, readCart, clearCart, applyQuote, bindCheckout, ensurePaymentReady, markCheckoutSubmitted, abandonUnsubmittedCheckout, reconcilePaidCheckout,
        emitCartUpdated, money, itemCount, subtotal, normalizeDiscountCode, readDiscountCode, writeDiscountCode, clearDiscountCode,
        addItem, updateQuantity, adjustQuantity, removeItem };
})();
